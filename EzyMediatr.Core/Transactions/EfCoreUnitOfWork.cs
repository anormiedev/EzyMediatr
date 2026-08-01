using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EzyMediatr.Core.Transactions;

public sealed class EfCoreUnitOfWork<TContext>(TContext context, bool ownsContext = true) : IUnitOfWork where TContext : DbContext
{
    public async Task<TResponse> ExecuteAsync<TResponse>(Func<CancellationToken, Task<TResponse>> operation, CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var response = await operation(cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return response;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the exception raised by the operation or commit.
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (ownsContext)
        {
            await context.DisposeAsync();
        }
    }
}

public sealed class EfCoreUnitOfWorkFactory<TContext> : IUnitOfWorkFactory where TContext : DbContext
{
    private readonly IDbContextFactory<TContext>? _contextFactory;
    private readonly Func<TContext>? _contextResolver;
    private readonly bool _ownsContext;

    public EfCoreUnitOfWorkFactory(IDbContextFactory<TContext> contextFactory)
    {
        _contextFactory = contextFactory;
        _ownsContext = true;
    }

    public EfCoreUnitOfWorkFactory(Func<TContext> contextResolver)
    {
        _contextResolver = contextResolver;
        _ownsContext = false;
    }

    public async Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken = default)
    {
        var context = _contextResolver is not null
            ? _contextResolver()
            : await _contextFactory!.CreateDbContextAsync(cancellationToken);
        return new EfCoreUnitOfWork<TContext>(context, _ownsContext);
    }
}
