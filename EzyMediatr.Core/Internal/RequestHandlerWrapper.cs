using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;

namespace EzyMediatr.Core.Internal;

internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(
        IBaseRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(
        IBaseRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var features = options.GetPipelineFeatures<TRequest>();

        // Keep the usual no-custom-behavior path free of delegate-chain allocations.
        if ((features & PipelineFeatures.RequestBehavior) == 0)
        {
            return ExecuteBuiltInPipeline(typedRequest, serviceProvider, options, features, cancellationToken);
        }

        return ExecuteWithBehaviors(typedRequest, serviceProvider, options, features, cancellationToken);
    }

    private static Task<TResponse> ExecuteWithBehaviors(
        TRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        PipelineFeatures features,
        CancellationToken cancellationToken)
    {
        var registeredBehaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();
        var behaviors = registeredBehaviors as IPipelineBehavior<TRequest, TResponse>[]
            ?? registeredBehaviors.ToArray();

        if (behaviors.Length == 0)
        {
            return ExecuteBuiltInPipeline(request, serviceProvider, options, features, cancellationToken);
        }

        RequestHandlerDelegate<TResponse> handlerDelegate = () =>
            ExecuteBuiltInPipeline(request, serviceProvider, options, features, cancellationToken);

        if (behaviors.Length == 1)
        {
            return behaviors[0].Handle(request, handlerDelegate, cancellationToken);
        }

        for (var index = behaviors.Length - 1; index >= 0; index--)
        {
            var behavior = behaviors[index];
            var next = handlerDelegate;
            handlerDelegate = () => behavior.Handle(request, next, cancellationToken);
        }

        return handlerDelegate();
    }

    private static Task<TResponse> ExecuteBuiltInPipeline(
        TRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        PipelineFeatures features,
        CancellationToken cancellationToken)
    {
        var validation = ValidationBehavior<TRequest, TResponse>.Validate(
            request,
            serviceProvider,
            options,
            (features & PipelineFeatures.Validator) != 0,
            cancellationToken);

        return validation.IsCompletedSuccessfully
            ? ExecuteAfterValidation(request, serviceProvider, options, features, cancellationToken)
            : AwaitValidationAndExecute(validation, request, serviceProvider, options, features, cancellationToken);
    }

    private static async Task<TResponse> AwaitValidationAndExecute(
        ValueTask validation,
        TRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        PipelineFeatures features,
        CancellationToken cancellationToken)
    {
        await validation.ConfigureAwait(false);
        return await ExecuteAfterValidation(request, serviceProvider, options, features, cancellationToken).ConfigureAwait(false);
    }

    private static Task<TResponse> ExecuteAfterValidation(
        TRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        PipelineFeatures features,
        CancellationToken cancellationToken)
    {
        if (!TransactionBehavior<TRequest, TResponse>.IsRequired(request, options))
        {
            return ExecuteHandler(request, serviceProvider, features, cancellationToken);
        }

        return ExecuteTransactional(request, serviceProvider, options, features, cancellationToken);
    }

    private static Task<TResponse> ExecuteTransactional(
        TRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        PipelineFeatures features,
        CancellationToken cancellationToken)
    {
        return TransactionBehavior<TRequest, TResponse>.ExecuteTransactional(
            request,
            () => ExecuteHandler(request, serviceProvider, features, cancellationToken),
            serviceProvider,
            options,
            cancellationToken);
    }

    private static Task<TResponse> ExecuteHandler(
        TRequest request,
        IServiceProvider serviceProvider,
        PipelineFeatures features,
        CancellationToken cancellationToken)
    {
        var hasPreProcessors = (features & PipelineFeatures.PreProcessor) != 0;
        var hasPostProcessors = (features & PipelineFeatures.PostProcessor) != 0;

        if (!hasPreProcessors && !hasPostProcessors)
        {
            return serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>()
                .Handle(request, cancellationToken);
        }

        // Microsoft DI returns arrays for IEnumerable<T>; retain them to avoid copying
        // while remaining compatible with other containers.
        var preProcessors = hasPreProcessors
            ? ToArray(serviceProvider.GetServices<IRequestPreProcessor<TRequest>>())
            : [];
        var postProcessors = hasPostProcessors
            ? ToArray(serviceProvider.GetServices<IRequestPostProcessor<TRequest, TResponse>>())
            : [];

        return ExecuteWithProcessors(request, serviceProvider, preProcessors, postProcessors, cancellationToken);
    }

    private static Task<TResponse> ExecuteWithProcessors(
        TRequest request,
        IServiceProvider serviceProvider,
        IRequestPreProcessor<TRequest>[] preProcessors,
        IRequestPostProcessor<TRequest, TResponse>[] postProcessors,
        CancellationToken cancellationToken)
    {
        // Stay on the caller's task when every stage completes synchronously. The
        // async helpers below allocate state only after a stage actually suspends.
        try
        {
            for (var index = 0; index < preProcessors.Length; index++)
            {
                var processing = preProcessors[index].Process(request, cancellationToken);
                if (!processing.IsCompletedSuccessfully)
                {
                    return AwaitPreProcessorsAndExecute(
                        processing,
                        index + 1,
                        request,
                        serviceProvider,
                        preProcessors,
                        postProcessors,
                        cancellationToken);
                }
            }

            return ExecuteHandlerAndPostProcessors(
                request,
                serviceProvider,
                postProcessors,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            var token = exception.CancellationToken.IsCancellationRequested
                ? exception.CancellationToken
                : new CancellationToken(canceled: true);
            return Task.FromCanceled<TResponse>(token);
        }
        catch (Exception exception)
        {
            return Task.FromException<TResponse>(exception);
        }
    }

    private static async Task<TResponse> AwaitPreProcessorsAndExecute(
        Task currentProcessor,
        int nextIndex,
        TRequest request,
        IServiceProvider serviceProvider,
        IRequestPreProcessor<TRequest>[] preProcessors,
        IRequestPostProcessor<TRequest, TResponse>[] postProcessors,
        CancellationToken cancellationToken)
    {
        await currentProcessor.ConfigureAwait(false);

        for (var index = nextIndex; index < preProcessors.Length; index++)
        {
            await preProcessors[index].Process(request, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteHandlerAndPostProcessors(
                request,
                serviceProvider,
                postProcessors,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<TResponse> ExecuteHandlerAndPostProcessors(
        TRequest request,
        IServiceProvider serviceProvider,
        IRequestPostProcessor<TRequest, TResponse>[] postProcessors,
        CancellationToken cancellationToken)
    {
        var handlerTask = serviceProvider
            .GetRequiredService<IRequestHandler<TRequest, TResponse>>()
            .Handle(request, cancellationToken);

        if (!handlerTask.IsCompletedSuccessfully)
        {
            return AwaitHandlerAndPostProcessors(handlerTask, request, postProcessors, cancellationToken);
        }

        var response = handlerTask.Result;
        for (var index = 0; index < postProcessors.Length; index++)
        {
            var processing = postProcessors[index].Process(request, response, cancellationToken);
            if (!processing.IsCompletedSuccessfully)
            {
                return AwaitPostProcessors(
                    processing,
                    index + 1,
                    request,
                    response,
                    postProcessors,
                    cancellationToken);
            }
        }

        return handlerTask;
    }

    private static async Task<TResponse> AwaitHandlerAndPostProcessors(
        Task<TResponse> handlerTask,
        TRequest request,
        IRequestPostProcessor<TRequest, TResponse>[] postProcessors,
        CancellationToken cancellationToken)
    {
        var response = await handlerTask.ConfigureAwait(false);
        foreach (var postProcessor in postProcessors)
        {
            await postProcessor.Process(request, response, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static async Task<TResponse> AwaitPostProcessors(
        Task currentProcessor,
        int nextIndex,
        TRequest request,
        TResponse response,
        IRequestPostProcessor<TRequest, TResponse>[] postProcessors,
        CancellationToken cancellationToken)
    {
        await currentProcessor.ConfigureAwait(false);

        for (var index = nextIndex; index < postProcessors.Length; index++)
        {
            await postProcessors[index].Process(request, response, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static TService[] ToArray<TService>(IEnumerable<TService> services)
        => services as TService[] ?? services.ToArray();
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
