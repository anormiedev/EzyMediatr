using System.Data;
using System.Data.Common;

namespace EzyMediatr.Core.Transactions;

public sealed class DapperUnitOfWork : IUnitOfWork, ISqlUnitOfWork
{
    private readonly IDbConnection _connection;
    private IDbTransaction? _transaction;

    public IDbConnection Connection => _connection;
    public IDbTransaction? Transaction => _transaction;

    public DapperUnitOfWork(IDbConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public async Task<TResponse> ExecuteAsync<TResponse>(Func<CancellationToken, Task<TResponse>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            await OpenIfNeededAsync(cancellationToken).ConfigureAwait(false);
            _transaction = _connection is DbConnection dbConnection
                ? await dbConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : _connection.BeginTransaction();

            var response = await operation(cancellationToken).ConfigureAwait(false);
            if (_transaction is DbTransaction dbTransaction)
            {
                await dbTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _transaction.Commit();
            }

            return response;
        }
        catch
        {
            try
            {
                if (_transaction is DbTransaction dbTransaction)
                {
                    await dbTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    _transaction?.Rollback();
                }
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
        try
        {
            if (_transaction is DbTransaction dbTransaction)
            {
                await dbTransaction.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _transaction?.Dispose();
            }
        }
        finally
        {
            if (_connection is DbConnection dbConnection)
            {
                await dbConnection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _connection.Dispose();
            }
        }
    }

    private async Task OpenIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_connection.State == ConnectionState.Open)
        {
            return;
        }

        if (_connection is DbConnection db)
        {
            await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _connection.Open();
        }
    }
}

public sealed class DapperUnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly Func<IDbConnection> _connectionFactory;

    public DapperUnitOfWorkFactory(Func<IDbConnection> connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken = default)
    {
        IUnitOfWork uow = new DapperUnitOfWork(_connectionFactory());
        return Task.FromResult(uow);
    }
}
