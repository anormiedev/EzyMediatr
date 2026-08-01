using FluentValidation;
using EzyMediatr.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EzyMediatr.Core.Pipeline;

public sealed class ValidationBehavior<TRequest, TResponse>(IServiceProvider serviceProvider, EzyMediatrOptions options) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{

    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
        => ValidateAndContinue(request, next, serviceProvider, options, cancellationToken);

    internal static ValueTask Validate(
        TRequest request,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        bool mayHaveValidators,
        CancellationToken cancellationToken)
    {
        if (!options.AddValidationBehavior || !mayHaveValidators)
        {
            return ValueTask.CompletedTask;
        }

        var registeredValidators = serviceProvider.GetServices<IValidator<TRequest>>();
        var validators = registeredValidators as IValidator<TRequest>[]
            ?? registeredValidators.ToArray();

        return validators.Length == 0
            ? ValueTask.CompletedTask
            : new ValueTask(ValidateAll(request, validators, cancellationToken));
    }

    private static async Task ValidateAll(
        TRequest request,
        IValidator<TRequest>[] validators,
        CancellationToken cancellationToken)
    {
        List<FluentValidation.Results.ValidationFailure>? failures = null;
        var context = new ValidationContext<TRequest>(request);

        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);
            if (result.Errors.Count > 0)
            {
                failures ??= [];
                failures.AddRange(result.Errors);
            }
        }

        if (failures is not null)
        {
            throw new ValidationException(failures);
        }
    }

    private static async Task<TResponse> ValidateAndContinue(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken)
    {
        var mayHaveValidators = (options.GetPipelineFeatures<TRequest>() & PipelineFeatures.Validator) != 0;
        await Validate(request, serviceProvider, options, mayHaveValidators, cancellationToken).ConfigureAwait(false);
        return await next().ConfigureAwait(false);
    }
}
