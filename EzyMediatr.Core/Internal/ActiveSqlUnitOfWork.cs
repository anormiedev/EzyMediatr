using System.Data;
using EzyMediatr.Core.Transactions;

namespace EzyMediatr.Core.Internal;

/// <summary>
/// Resolves the Dapper unit of work active in the current asynchronous dispatch flow.
/// </summary>
internal sealed class ActiveSqlUnitOfWork(DapperUnitOfWorkAccessor accessor) : ISqlUnitOfWork
{
    public IDbConnection Connection => accessor.GetCurrent().Connection;
    public IDbTransaction? Transaction => accessor.GetCurrent().Transaction;
}
