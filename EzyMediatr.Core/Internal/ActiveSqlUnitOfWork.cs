using System.Data;
using EzyMediatr.Core.Transactions;

namespace EzyMediatr.Core.Internal;

/// <summary>
/// Resolves the Dapper unit of work active in the current asynchronous dispatch flow.
/// </summary>
internal sealed class ActiveSqlUnitOfWork(UnitOfWorkAccessor accessor) : ISqlUnitOfWork
{
    public IDbConnection Connection => accessor.GetCurrentSql().Connection;
    public IDbTransaction? Transaction => accessor.GetCurrentSql().Transaction;
}
