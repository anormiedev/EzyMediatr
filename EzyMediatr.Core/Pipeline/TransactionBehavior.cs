using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Transactions;

namespace EzyMediatr.Core.Pipeline;

public class TransactionBehavior<TRequest, TResponse>(IServiceProvider serviceProvider, EzyMediatrOptions options) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!options.WrapAllRequests && request is not ITransactionalRequest)
        {
            return await next();
        }

        if (options.UnitOfWorkSelector is null)
        {
            throw new InvalidOperationException("UnitOfWork is not configured. Call UseDapper/UseEfCore/UseUnitOfWorkFactory in AddEzyMediatr.");
        }

        var factory = options.UnitOfWorkSelector(request, serviceProvider);
        await using var uow = await factory.CreateAsync(cancellationToken);
        return await uow.ExecuteAsync(_ => next(), cancellationToken);
    }
}
