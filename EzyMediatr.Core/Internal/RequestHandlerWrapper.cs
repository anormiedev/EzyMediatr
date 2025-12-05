using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;

namespace EzyMediatr.Core.Internal;

internal abstract class RequestHandlerWrapper
{
    public abstract Task<object?> Handle(IBaseRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : RequestHandlerWrapper
    where TRequest : IRequest<TResponse>
{
    public override async Task<object?> Handle(IBaseRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse().ToList();
        var preProcessors = serviceProvider.GetServices<IRequestPreProcessor<TRequest>>();
        var postProcessors = serviceProvider.GetServices<IRequestPostProcessor<TRequest, TResponse>>();

        foreach (var pre in preProcessors)
        {
            await pre.Process(typedRequest, cancellationToken);
        }

        RequestHandlerDelegate<TResponse> handlerDelegate = () => handler.Handle(typedRequest, cancellationToken);

        foreach (var behavior in behaviors)
        {
            var next = handlerDelegate;
            handlerDelegate = () => behavior.Handle(typedRequest, next, cancellationToken);
        }

        var response = await handlerDelegate();

        foreach (var post in postProcessors)
        {
            await post.Process(typedRequest, response, cancellationToken);
        }

        return response!;
    }
}

internal static class RequestHandlerWrapperFactory
{
    private static readonly ConcurrentDictionary<(Type Request, Type Response), RequestHandlerWrapper> Cache = new();

    public static RequestHandlerWrapper Create(Type requestType, Type responseType)
    {
        var key = (requestType, responseType);
        return Cache.GetOrAdd(key, static tuple =>
        {
            var constructed = (RequestHandlerWrapper)Activator.CreateInstance(
                typeof(RequestHandlerWrapper<,>).MakeGenericType(tuple.Request, tuple.Response))!;
            return constructed;
        });
    }
}
