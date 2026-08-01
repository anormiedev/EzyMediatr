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
        EzyMediatrOptions options,
        CancellationToken cancellationToken);
}

internal sealed class StreamRequestHandlerWrapper<TRequest, TResponse> : StreamRequestHandlerWrapper<TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    public override IAsyncEnumerable<TResponse> Handle(
        IBaseRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var features = options.GetPipelineFeatures<TRequest>();
        var hasPreProcessors = (features & PipelineFeatures.PreProcessor) != 0;

        return (features & PipelineFeatures.StreamBehavior) != 0
            ? ExecuteWithBehaviors(typedRequest, serviceProvider, hasPreProcessors, cancellationToken)
            : hasPreProcessors
                ? ExecuteHandlerWithPreProcessors(typedRequest, serviceProvider, cancellationToken)
                : ExecuteHandler(typedRequest, serviceProvider, cancellationToken);
    }

    private static async IAsyncEnumerable<TResponse> ExecuteHandler(
        TRequest request,
        IServiceProvider serviceProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var handler = serviceProvider.GetRequiredService<IStreamRequestHandler<TRequest, TResponse>>();
        await foreach (var item in handler.Handle(request, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private static async IAsyncEnumerable<TResponse> ExecuteHandlerWithPreProcessors(
        TRequest request,
        IServiceProvider serviceProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var preProcessor in serviceProvider.GetServices<IRequestPreProcessor<TRequest>>())
        {
            await preProcessor.Process(request, cancellationToken).ConfigureAwait(false);
        }

        var handler = serviceProvider.GetRequiredService<IStreamRequestHandler<TRequest, TResponse>>();
        await foreach (var item in handler.Handle(request, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private static async IAsyncEnumerable<TResponse> ExecuteWithBehaviors(
        TRequest request,
        IServiceProvider serviceProvider,
        bool hasPreProcessors,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var behaviors = ToArray(serviceProvider.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>());

        if (behaviors.Length == 0)
        {
            var handler = hasPreProcessors
                ? ExecuteHandlerWithPreProcessors(request, serviceProvider, cancellationToken)
                : ExecuteHandler(request, serviceProvider, cancellationToken);

            await foreach (var item in handler
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        StreamHandlerDelegate<TResponse> handlerDelegate = hasPreProcessors
            ? () => ExecuteHandlerWithPreProcessors(request, serviceProvider, cancellationToken)
            : () => ExecuteHandler(request, serviceProvider, cancellationToken);

        if (behaviors.Length == 1)
        {
            await foreach (var item in behaviors[0]
                               .Handle(request, handlerDelegate, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        for (var index = behaviors.Length - 1; index >= 0; index--)
        {
            var behavior = behaviors[index];
            var next = handlerDelegate;
            handlerDelegate = () => behavior.Handle(request, next, cancellationToken);
        }

        await foreach (var item in handlerDelegate()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private static TService[] ToArray<TService>(IEnumerable<TService> services)
        => services as TService[] ?? services.ToArray();
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
