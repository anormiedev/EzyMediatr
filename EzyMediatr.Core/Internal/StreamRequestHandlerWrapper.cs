using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;

namespace EzyMediatr.Core.Internal;

internal abstract class StreamRequestHandlerWrapper
{
    public abstract IAsyncEnumerable<object?> Handle(IBaseRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class StreamRequestHandlerWrapper<TRequest, TResponse> : StreamRequestHandlerWrapper
    where TRequest : IStreamRequest<TResponse>
{
    public override async IAsyncEnumerable<object?> Handle(IBaseRequest request, IServiceProvider serviceProvider, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetRequiredService<IStreamRequestHandler<TRequest, TResponse>>();
        var behaviors = serviceProvider.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>().Reverse().ToList();
        var preProcessors = serviceProvider.GetServices<IRequestPreProcessor<TRequest>>();

        foreach (var pre in preProcessors)
        {
            await pre.Process(typedRequest, cancellationToken);
        }

        StreamHandlerDelegate<TResponse> handlerDelegate = () => handler.Handle(typedRequest, cancellationToken);

        foreach (var behavior in behaviors)
        {
            var next = handlerDelegate;
            handlerDelegate = () => behavior.Handle(typedRequest, next, cancellationToken);
        }

        await foreach (var item in handlerDelegate().WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }
}

internal static class StreamRequestHandlerWrapperFactory
{
    private static readonly ConcurrentDictionary<(Type Request, Type Response), StreamRequestHandlerWrapper> Cache = new();

    public static StreamRequestHandlerWrapper Create(Type requestType, Type responseType)
    {
        var key = (requestType, responseType);
        return Cache.GetOrAdd(key, static tuple =>
        {
            var constructed = (StreamRequestHandlerWrapper)Activator.CreateInstance(
                typeof(StreamRequestHandlerWrapper<,>).MakeGenericType(tuple.Request, tuple.Response))!;
            return constructed;
        });
    }
}
