using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;
using EzyMediatr.Core.Transactions;
using EzyMediatr.Core.Internal;
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
        var singleHandlerServices = new HashSet<Type>();
        foreach (var descriptor in services)
        {
            if (IsSingleHandlerService(descriptor.ServiceType))
            {
                singleHandlerServices.Add(descriptor.ServiceType);
            }
        }

        foreach (var assembly in assemblies)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                foreach (var handlerInterface in type.GetInterfaces())
                {
                    if (!handlerInterface.IsGenericType)
                    {
                        continue;
                    }

                    var definition = handlerInterface.GetGenericTypeDefinition();
                    if (definition == typeof(IRequestHandler<,>) || definition == typeof(IStreamRequestHandler<,>))
                    {
                        if (!singleHandlerServices.Add(handlerInterface))
                        {
                            throw new InvalidOperationException($"Multiple handlers were registered for '{handlerInterface}'. A request must have exactly one handler.");
                        }

                        services.AddScoped(handlerInterface, type);
                        continue;
                    }

                    if (definition == typeof(INotificationHandler<>) ||
                        definition == typeof(IRequestPreProcessor<>) ||
                        definition == typeof(IRequestPostProcessor<,>) ||
                        definition == typeof(IPipelineBehavior<,>) ||
                        definition == typeof(IStreamPipelineBehavior<,>) ||
                        definition == typeof(INotificationPipelineBehavior<>))
                    {
                        services.AddScoped(handlerInterface, type);
                    }
                }
            }
        }
    }

    private static bool IsSingleHandlerService(Type serviceType)
    {
        if (!serviceType.IsGenericType)
        {
            return false;
        }

        var definition = serviceType.GetGenericTypeDefinition();
        return definition == typeof(IRequestHandler<,>) || definition == typeof(IStreamRequestHandler<,>);
    }

    private static IEnumerable<TypeInfo> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.DefinedTypes;
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types
                .Where(type => type is not null)
                .Select(type => type!.GetTypeInfo());
        }
    }
}

public sealed class EzyMediatrBuilder
{
    private readonly IServiceCollection _services;
    private readonly Assembly[] _assemblies;
    private bool _unitOfWorkAccessorRegistered;
    private bool _dapperAccessRegistered;

    internal EzyMediatrBuilder(IServiceCollection services, Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        _services = services;
        _assemblies = assemblies is { Length: > 0 }
            ? GetDistinctAssemblies(assemblies)
            : DiscoverHandlerAssemblies();

        _services.AddSingleton(Options);
        _services.AddScoped<IMediator, Mediator>();

        ServiceCollectionExtensions.RegisterHandlers(_services, _assemblies);

        _services.AddValidatorsFromAssemblies(_assemblies);

        var serviceRegistry = new Lazy<ServiceRegistrationRegistry>(
            () => new ServiceRegistrationRegistry(_services.Select(descriptor => descriptor.ServiceType)),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Options.SetServiceRegistry(serviceRegistry);
    }

    public EzyMediatrOptions Options { get; } = new();

    private static Assembly[] DiscoverHandlerAssemblies()
    {
        var coreAssemblyName = typeof(IBaseRequest).Assembly.GetName();
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && assembly.GetReferencedAssemblies()
                .Any(reference => AssemblyName.ReferenceMatchesDefinition(reference, coreAssemblyName)))
            .Distinct()
            .ToArray();
    }

    private static Assembly[] GetDistinctAssemblies(Assembly[] assemblies)
    {
        if (Array.IndexOf(assemblies, null!) >= 0)
        {
            throw new ArgumentException("Assembly lists cannot contain null values.", nameof(assemblies));
        }

        return assemblies.Distinct().ToArray();
    }

    public EzyMediatrBuilder UseDapper(Func<IDbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        Options.UseDapper(_ => connectionFactory());
        ApplyUnitOfWork();
        return this;
    }

    public EzyMediatrBuilder UseDapper(Func<IServiceProvider, IDbConnection> connectionFactory)
    {
        Options.UseDapper(connectionFactory);
        ApplyUnitOfWork();
        return this;
    }

    public EzyMediatrBuilder UseDapper(Func<IBaseRequest, IServiceProvider, IDbConnection> connectionFactory)
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

    public EzyMediatrBuilder UseEfCore<TContext>(Action<DbContextOptionsBuilder> optionsAction) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsAction);
        _services.AddDbContextFactory<TContext>(optionsAction);
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
        if (Options.UnitOfWorkFactory is not null && !_unitOfWorkAccessorRegistered)
        {
            _services.AddScoped<UnitOfWorkAccessor>();
            _unitOfWorkAccessorRegistered = true;
        }

        if (Options.UsesDapper && !_dapperAccessRegistered)
        {
            _services.AddScoped<ISqlUnitOfWork, ActiveSqlUnitOfWork>();
            _dapperAccessRegistered = true;
        }
    }
}
