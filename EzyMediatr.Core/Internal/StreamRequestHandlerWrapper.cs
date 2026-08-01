using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;

namespace EzyMediatr.Core.Internal;

internal abstract class StreamRequestHandlerWrapper<TResponse>
{
    public abstract IAsyncEnumerable<TResponse> Handle(
        IBaseRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

internal sealed class StreamRequestHandlerWrapper<TRequest, TResponse> : StreamRequestHandlerWrapper<TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    public override async IAsyncEnumerable<TResponse> Handle(
        IBaseRequest request,
        IServiceProvider serviceProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;

        async IAsyncEnumerable<TResponse> ExecuteHandler()
        {
            foreach (var pre in serviceProvider.GetServices<IRequestPreProcessor<TRequest>>())
            {
                await pre.Process(typedRequest, cancellationToken);
            }

            var handler = serviceProvider.GetRequiredService<IStreamRequestHandler<TRequest, TResponse>>();
            await foreach (var item in handler.Handle(typedRequest, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return item;
            }
        }

        var registeredBehaviors = serviceProvider.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>();
        var behaviors = registeredBehaviors as IStreamPipelineBehavior<TRequest, TResponse>[]
            ?? registeredBehaviors.ToArray();

        if (behaviors.Length == 0)
        {
            await foreach (var item in ExecuteHandler().WithCancellation(cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        StreamHandlerDelegate<TResponse> handlerDelegate = ExecuteHandler;

        for (var index = behaviors.Length - 1; index >= 0; index--)
        {
            var behavior = behaviors[index];
            var next = handlerDelegate;
            handlerDelegate = () => behavior.Handle(typedRequest, next, cancellationToken);
        }

        await foreach (var item in handlerDelegate().WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }
}

internal static class StreamRequestHandlerWrapperFactory<TResponse>
{
    private static readonly ConcurrentDictionary<Type, StreamRequestHandlerWrapper<TResponse>> Cache = new();

    public static StreamRequestHandlerWrapper<TResponse> Create(Type requestType)
    {
        return Cache.GetOrAdd(requestType, static type =>
        {
            var constructed = (StreamRequestHandlerWrapper<TResponse>)Activator.CreateInstance(
                typeof(StreamRequestHandlerWrapper<,>).MakeGenericType(type, typeof(TResponse)))!;
            return constructed;
        });
    }
}
