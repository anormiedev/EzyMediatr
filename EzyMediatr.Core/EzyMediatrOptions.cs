using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Transactions;

namespace EzyMediatr.Core;

public class EzyMediatrOptions
{
    public bool AddValidationBehavior { get; set; } = true;
    public bool WrapAllRequests { get; private set; }

    internal Func<IBaseRequest, IServiceProvider, CancellationToken, ValueTask<IUnitOfWork>>? UnitOfWorkFactory { get; private set; }

    internal bool UsesDapper { get; private set; }

    public EzyMediatrOptions WrapEveryRequest()
    {
        WrapAllRequests = true;
        return this;
    }

    public EzyMediatrOptions UseDapper(Func<IServiceProvider, IDbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        UnitOfWorkFactory = (_, sp, _) => ValueTask.FromResult<IUnitOfWork>(new DapperUnitOfWork(connectionFactory(sp)));
        UsesDapper = true;
        return this;
    }


    public EzyMediatrOptions UseDapper(Func<IBaseRequest, IServiceProvider, IDbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        UnitOfWorkFactory = (request, sp, _) => ValueTask.FromResult<IUnitOfWork>(new DapperUnitOfWork(connectionFactory(request, sp)));
        UsesDapper = true;
        return this;
    }


    public EzyMediatrOptions UseEfCore<TContext>() where TContext : DbContext
    {
        UnitOfWorkFactory = (_, sp, _) => ValueTask.FromResult<IUnitOfWork>(
            new EfCoreUnitOfWork<TContext>(sp.GetRequiredService<TContext>(), ownsContext: false));
        UsesDapper = false;
        return this;
    }


    public EzyMediatrOptions UseEfCore<TContext>(Func<IBaseRequest, bool> when) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(when);
        UnitOfWorkFactory = (request, sp, _) =>
        {
            if (!when(request))
            {
                throw new InvalidOperationException("No unit of work configured for this request.");
            }

            return ValueTask.FromResult<IUnitOfWork>(
                new EfCoreUnitOfWork<TContext>(sp.GetRequiredService<TContext>(), ownsContext: false));
        };
        UsesDapper = false;
        return this;
    }


    public EzyMediatrOptions UseUnitOfWorkFactory(Func<IServiceProvider, IUnitOfWorkFactory> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        UnitOfWorkFactory = (_, sp, cancellationToken) =>
            new ValueTask<IUnitOfWork>(resolver(sp).CreateAsync(cancellationToken));
        UsesDapper = false;
        return this;
    }
}
