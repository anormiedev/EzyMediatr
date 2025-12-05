using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;
using EzyMediatr.Core.Transactions;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace EzyMediatr.DependencyInjection;

public static class ServiceCollectionExtensions
{

    public static EzyMediatrBuilder AddEzyMediatr(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        return new EzyMediatrBuilder(services, assemblies);
    }

    public static IServiceCollection AddEzyMediatr(
        this IServiceCollection services,
        Action<EzyMediatrOptions>? configure = null,
        params Assembly[] assemblies)
    {
        var builder = new EzyMediatrBuilder(services, assemblies);
        configure?.Invoke(builder.Options);
        builder.ApplyUnitOfWork();
        return services;
    }

    internal static void RegisterHandlers(IServiceCollection services, Assembly[] assemblies)
    {
        foreach (var type in assemblies
                     .SelectMany(a => a.DefinedTypes)
                     .Where(t => t is { IsAbstract: false, IsInterface: false } && !t.IsGenericTypeDefinition))
        {
            foreach (var handlerInterface in type.GetInterfaces().Where(i => i.IsGenericType))
            {
                var def = handlerInterface.GetGenericTypeDefinition();
                if (def == typeof(IRequestHandler<,>) || def == typeof(IStreamRequestHandler<,>) || def == typeof(INotificationHandler<>))
                {
                    services.AddScoped(handlerInterface, type);
                }

                if (def == typeof(IRequestPreProcessor<>) || def == typeof(IRequestPostProcessor<,>) ||
                    def == typeof(IPipelineBehavior<,>) || def == typeof(IStreamPipelineBehavior<,>))
                {
                    services.AddScoped(handlerInterface, type);
                }
            }
        }
    }
}

public sealed class EzyMediatrBuilder
{
    private readonly IServiceCollection _services;
    private readonly Assembly[] _assemblies;
    private bool _uowRegistered;

    internal EzyMediatrBuilder(IServiceCollection services, Assembly[] assemblies)
    {
        _services = services;
        _assemblies = assemblies is { Length: > 0 }
            ? assemblies
            : AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToArray();

        _services.AddSingleton(Options);
        _services.AddScoped<IMediator, Mediator>();

        ServiceCollectionExtensions.RegisterHandlers(_services, _assemblies);

        if (Options.AddValidationBehavior)
        {
            _services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
            _services.AddValidatorsFromAssemblies(_assemblies);
        }
    }

    public EzyMediatrOptions Options { get; } = new();

    public EzyMediatrBuilder UseDapper(Func<IServiceProvider, IDbConnection> connectionFactory)
    {
        Options.UseDapper(connectionFactory);
        ApplyUnitOfWork();
        return this;
    }

    public EzyMediatrBuilder UseEfCore<TContext>() where TContext : DbContext
    {
        Options.UseEfCore<TContext>();
        ApplyUnitOfWork();
        return this;
    }

    public EzyMediatrBuilder UseUnitOfWorkFactory(Func<IServiceProvider, IUnitOfWorkFactory> resolver)
    {
        Options.UseUnitOfWorkFactory(resolver);
        ApplyUnitOfWork();
        return this;
    }

    public EzyMediatrBuilder WrapEveryRequest()
    {
        Options.WrapEveryRequest();
        return this;
    }

    internal void ApplyUnitOfWork()
    {
        if (Options.UnitOfWorkSelector is null || _uowRegistered)
        {
            return;
        }

        _services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
        _uowRegistered = true;
    }
}
