using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Internal;
using EzyMediatr.Core.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace EzyMediatr.Core.Pipeline;

public sealed class TransactionBehavior<TRequest, TResponse>(IServiceProvider serviceProvider, EzyMediatrOptions options) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{

    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
        => Execute(request, next, serviceProvider, options, cancellationToken);

    internal static Task<TResponse> Execute(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsRequired(request, options))
        {
            return next();
        }

        return ExecuteTransactional(request, next, serviceProvider, options, cancellationToken);
    }

    internal static bool IsRequired(TRequest request, EzyMediatrOptions options)
        => options.WrapAllRequests || request is ITransactionalRequest;

    internal static async Task<TResponse> ExecuteTransactional(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        IServiceProvider serviceProvider,
        EzyMediatrOptions options,
        CancellationToken cancellationToken)
    {
        var accessor = serviceProvider.GetService<UnitOfWorkAccessor>();
        if (accessor?.Current is not null)
        {
            return await next().ConfigureAwait(false);
        }

        if (options.UnitOfWorkFactory is null)
        {
            throw new InvalidOperationException("UnitOfWork is not configured. Call UseDapper/UseEfCore/UseUnitOfWorkFactory in AddEzyMediatr.");
        }

        var createdUnitOfWork = await options.UnitOfWorkFactory(request, serviceProvider, cancellationToken)
            .ConfigureAwait(false);
        var uow = createdUnitOfWork
            ?? throw new InvalidOperationException("The configured unit-of-work factory returned null.");
        await using var ownedUnitOfWork = uow;
        var previousUnitOfWork = accessor?.Push(uow);

        try
        {
            return await uow.ExecuteAsync(_ => next(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            accessor?.Restore(previousUnitOfWork);
        }
    }
}
