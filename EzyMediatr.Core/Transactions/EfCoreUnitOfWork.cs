using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EzyMediatr.Core.Transactions;

public sealed class EfCoreUnitOfWork<TContext> : IUnitOfWork where TContext : DbContext
{
    private readonly TContext _context;
    private readonly bool _ownsContext;

    public EfCoreUnitOfWork(TContext context, bool ownsContext = true)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ownsContext = ownsContext;
    }

    public async Task<TResponse> ExecuteAsync<TResponse>(Func<CancellationToken, Task<TResponse>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await operation(cancellationToken).ConfigureAwait(false);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
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
        if (_ownsContext)
        {
            await _context.DisposeAsync().ConfigureAwait(false);
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
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _ownsContext = true;
    }

    public EfCoreUnitOfWorkFactory(Func<TContext> contextResolver)
    {
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _ownsContext = false;
    }

    public async Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken = default)
    {
        var context = _contextResolver is not null
            ? _contextResolver()
            : await _contextFactory!.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return new EfCoreUnitOfWork<TContext>(context, _ownsContext);
    }
}
