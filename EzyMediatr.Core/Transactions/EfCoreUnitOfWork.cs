using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EzyMediatr.Core.Transactions;

public sealed class EfCoreUnitOfWork<TContext>(TContext context) : IUnitOfWork where TContext : DbContext
{
    private IDbContextTransaction? _transaction;

    public async Task<TResponse> ExecuteAsync<TResponse>(Func<CancellationToken, Task<TResponse>> operation, CancellationToken cancellationToken = default)
    {
        _transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var response = await operation(cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await _transaction.CommitAsync(cancellationToken);
            return response;
        }
        catch
        {
            if (_transaction is not null)
            {
                await _transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
        }

        await context.DisposeAsync();
    }
}

public sealed class EfCoreUnitOfWorkFactory<TContext> : IUnitOfWorkFactory where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;

    public EfCoreUnitOfWorkFactory(IDbContextFactory<TContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken = default)
    {
        var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return new EfCoreUnitOfWork<TContext>(context);
    }
}
