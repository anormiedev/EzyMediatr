using System.Data;

namespace EzyMediatr.Core.Transactions;

public interface IUnitOfWork : IAsyncDisposable
{
    Task<TResponse> ExecuteAsync<TResponse>(Func<CancellationToken, Task<TResponse>> operation, CancellationToken cancellationToken = default);
}

public interface ISqlUnitOfWork : IUnitOfWork
{
    IDbConnection Connection { get; }
    IDbTransaction? Transaction { get; }
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken = default);
}
