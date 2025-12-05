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

    public Func<IBaseRequest, IServiceProvider, IUnitOfWorkFactory>? UnitOfWorkSelector { get; private set; }

    public EzyMediatrOptions WrapEveryRequest()
    {
        WrapAllRequests = true;
        return this;
    }

    public EzyMediatrOptions UseDapper(Func<IServiceProvider, IDbConnection> connectionFactory)
    {
        UnitOfWorkSelector = (_, sp) => new DapperUnitOfWorkFactory(() => connectionFactory(sp));
        return this;
    }


    public EzyMediatrOptions UseDapper(Func<IBaseRequest, IServiceProvider, IDbConnection> connectionFactory)
    {
        UnitOfWorkSelector = (request, sp) => new DapperUnitOfWorkFactory(() => connectionFactory(request, sp));
        return this;
    }


    public EzyMediatrOptions UseEfCore<TContext>() where TContext : DbContext
    {
        UnitOfWorkSelector = (_, sp) =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<TContext>>();
            return new EfCoreUnitOfWorkFactory<TContext>(factory);
        };
        return this;
    }


    public EzyMediatrOptions UseEfCore<TContext>(Func<IBaseRequest, bool> when) where TContext : DbContext
    {
        UnitOfWorkSelector = (request, sp) =>
        {
            if (!when(request))
            {
                throw new InvalidOperationException("No unit of work configured for this request.");
            }

            var factory = sp.GetRequiredService<IDbContextFactory<TContext>>();
            return new EfCoreUnitOfWorkFactory<TContext>(factory);
        };
        return this;
    }


    public EzyMediatrOptions UseUnitOfWorkFactory(Func<IServiceProvider, IUnitOfWorkFactory> resolver)
    {
        UnitOfWorkSelector = (_, sp) => resolver(sp);
        return this;
    }
}
