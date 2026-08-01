using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;

namespace EzyMediatr.Core.Internal;

internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract ValueTask<TResponse> Handle(
        IBaseRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override async ValueTask<TResponse> Handle(
        IBaseRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;

        Task<TResponse> ExecuteHandler()
        {
            // Microsoft DI returns arrays for IEnumerable<T>; retain them to avoid copying
            // the common result while remaining compatible with other containers.
            var registeredPreProcessors = serviceProvider.GetServices<IRequestPreProcessor<TRequest>>();
            var preProcessors = registeredPreProcessors as IRequestPreProcessor<TRequest>[]
                ?? registeredPreProcessors.ToArray();
            var registeredPostProcessors = serviceProvider.GetServices<IRequestPostProcessor<TRequest, TResponse>>();
            var postProcessors = registeredPostProcessors as IRequestPostProcessor<TRequest, TResponse>[]
                ?? registeredPostProcessors.ToArray();

            return preProcessors.Length == 0 && postProcessors.Length == 0
                ? serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>()
                    .Handle(typedRequest, cancellationToken)
                : ExecuteWithProcessors(preProcessors, postProcessors);
        }

        async Task<TResponse> ExecuteWithProcessors(
            IRequestPreProcessor<TRequest>[] preProcessors,
            IRequestPostProcessor<TRequest, TResponse>[] postProcessors)
        {
            foreach (var preProcessor in preProcessors)
            {
                await preProcessor.Process(typedRequest, cancellationToken);
            }

            var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
            var response = await handler.Handle(typedRequest, cancellationToken);

            foreach (var postProcessor in postProcessors)
            {
                await postProcessor.Process(typedRequest, response, cancellationToken);
            }

            return response;
        }

        var registeredBehaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();
        var behaviors = registeredBehaviors as IPipelineBehavior<TRequest, TResponse>[]
            ?? registeredBehaviors.ToArray();

        // Keep the usual no-custom-behavior path free of delegate-chain allocations.
        if (behaviors.Length == 0)
        {
            await ValidationBehavior<TRequest, TResponse>.Validate(
                typedRequest,
                serviceProvider,
                options,
                cancellationToken);

            if (!TransactionBehavior<TRequest, TResponse>.IsRequired(typedRequest, options))
            {
                return await ExecuteHandler();
            }

            return await TransactionBehavior<TRequest, TResponse>.ExecuteTransactional(
                typedRequest,
                ExecuteHandler,
                serviceProvider,
                options,
                cancellationToken);
        }

        Task<TResponse> ExecuteBuiltInPipeline()
        {
            var validation = ValidationBehavior<TRequest, TResponse>.Validate(
                typedRequest,
                serviceProvider,
                options,
                cancellationToken);

            // With no validators, validation is synchronously complete and adds no Task.
            return validation.IsCompletedSuccessfully
                ? ExecuteAfterValidation()
                : AwaitValidation(validation);
        }

        Task<TResponse> ExecuteAfterValidation()
            => TransactionBehavior<TRequest, TResponse>.IsRequired(typedRequest, options)
                ? TransactionBehavior<TRequest, TResponse>.ExecuteTransactional(
                    typedRequest,
                    ExecuteHandler,
                    serviceProvider,
                    options,
                    cancellationToken)
                : ExecuteHandler();

        async Task<TResponse> AwaitValidation(ValueTask validation)
        {
            await validation;
            return await ExecuteAfterValidation();
        }

        RequestHandlerDelegate<TResponse> handlerDelegate = ExecuteBuiltInPipeline;

        for (var index = behaviors.Length - 1; index >= 0; index--)
        {
            var behavior = behaviors[index];
            var next = handlerDelegate;
            handlerDelegate = () => behavior.Handle(typedRequest, next, cancellationToken);
        }

        return await handlerDelegate();
    }
}

internal static class RequestHandlerWrapperFactory<TResponse>
{
    private static readonly ConcurrentDictionary<Type, RequestHandlerWrapper<TResponse>> Cache = new();

    public static RequestHandlerWrapper<TResponse> Create(Type requestType)
    {
        return Cache.GetOrAdd(requestType, static type =>
        {
            var constructed = (RequestHandlerWrapper<TResponse>)Activator.CreateInstance(
                typeof(RequestHandlerWrapper<,>).MakeGenericType(type, typeof(TResponse)))!;
            return constructed;
        });
    }
}
