using System.Reflection;
using System.Reflection.Metadata;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;
using EzyMediatr.Core.Transactions;
using EzyMediatr.Core.Internal;
using EzyMediatr.DependencyInjection.Generated;
using Microsoft.EntityFrameworkCore;
using System.Data;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    internal static void RegisterHandlersAndValidators(IServiceCollection services, Assembly[] assemblies)
    {
        var singleHandlerServices = GetSingleHandlerServices(services);

        foreach (var assembly in assemblies)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                var registerConcreteValidator = false;
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
                        continue;
                    }

                    if (definition == typeof(IValidator<>) && type.IsVisible)
                    {
                        services.TryAddEnumerable(ServiceDescriptor.Scoped(handlerInterface, type));
                        registerConcreteValidator = true;
                    }
                }

                if (registerConcreteValidator)
                {
                    services.TryAdd(ServiceDescriptor.Scoped(type.AsType(), type.AsType()));
                }
            }
        }
    }

    internal static bool RegisterGeneratedHandlersAndValidators(IServiceCollection services)
    {
        var singleHandlerServices = GetSingleHandlerServices(services);
        var firstGeneratedDescriptor = services.Count;

        if (!EzyMediatrGeneratedRegistration.Apply(services))
        {
            return false;
        }

        for (var index = firstGeneratedDescriptor; index < services.Count; index++)
        {
            var serviceType = services[index].ServiceType;
            if (IsSingleHandlerService(serviceType) && !singleHandlerServices.Add(serviceType))
            {
                throw new InvalidOperationException($"Multiple handlers were registered for '{serviceType}'. A request must have exactly one handler.");
            }
        }

        return true;
    }

    private static HashSet<Type> GetSingleHandlerServices(IServiceCollection services)
    {
        var singleHandlerServices = new HashSet<Type>();
        foreach (var descriptor in services)
        {
            if (IsSingleHandlerService(descriptor.ServiceType))
            {
                singleHandlerServices.Add(descriptor.ServiceType);
            }
        }

        return singleHandlerServices;
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
    private bool _unitOfWorkAccessorRegistered;
    private bool _dapperAccessRegistered;

    internal EzyMediatrBuilder(IServiceCollection services, Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        _services = services;
        _services.AddSingleton(Options);
        _services.AddScoped<IMediator, Mediator>();

        if (assemblies is { Length: > 0 })
        {
            var selectedAssemblies = GetDistinctAssemblies(assemblies);
            ServiceCollectionExtensions.RegisterHandlersAndValidators(_services, selectedAssemblies);
        }
        else if (ServiceCollectionExtensions.RegisterGeneratedHandlersAndValidators(_services))
        {
            UsesGeneratedRegistrations = true;
        }
        else
        {
            var discoveredAssemblies = DiscoverHandlerAssemblies();
            ServiceCollectionExtensions.RegisterHandlersAndValidators(_services, discoveredAssemblies);
        }

        var serviceRegistry = new Lazy<ServiceRegistrationRegistry>(
            () => new ServiceRegistrationRegistry(_services.Select(descriptor => descriptor.ServiceType)),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Options.SetServiceRegistry(serviceRegistry);
    }

    public EzyMediatrOptions Options { get; } = new();

    public bool UsesGeneratedRegistrations { get; }

    private static Assembly[] DiscoverHandlerAssemblies()
    {
        var coreAssemblyName = typeof(IBaseRequest).Assembly.GetName().Name!;
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        var handlerAssemblies = new List<Assembly>();

        foreach (var assembly in loadedAssemblies)
        {
            if (!CanReferenceCore(assembly))
            {
                continue;
            }

            if (ReferencesAssembly(assembly, coreAssemblyName))
            {
                handlerAssemblies.Add(assembly);
            }
        }

        return handlerAssemblies.ToArray();
    }

    private static bool CanReferenceCore(Assembly assembly)
    {
        if (assembly.IsDynamic || assembly == typeof(EzyMediatrBuilder).Assembly)
        {
            return false;
        }

        var fullName = assembly.FullName;
        if (fullName is null)
        {
            return true;
        }

        // Framework assemblies are built independently of application packages and cannot reference EzyMediatr.Core.
        return !fullName.EndsWith("PublicKeyToken=31bf3856ad364e35", StringComparison.OrdinalIgnoreCase) &&
               !fullName.EndsWith("PublicKeyToken=7cec85d7bea7798e", StringComparison.OrdinalIgnoreCase) &&
               !fullName.EndsWith("PublicKeyToken=adb9793829ddae60", StringComparison.OrdinalIgnoreCase) &&
               !fullName.EndsWith("PublicKeyToken=b03f5f7f11d50a3a", StringComparison.OrdinalIgnoreCase) &&
               !fullName.EndsWith("PublicKeyToken=b77a5c561934e089", StringComparison.OrdinalIgnoreCase) &&
               !fullName.EndsWith("PublicKeyToken=cc7b13ffcd2ddd51", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe bool ReferencesAssembly(Assembly assembly, string referencedAssemblyName)
    {
        // Inspect the runtime's existing metadata instead of allocating an AssemblyName object for every reference.
        if (assembly.TryGetRawMetadata(out var metadata, out var length))
        {
            var reader = new MetadataReader(metadata, length);
            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                if (reader.StringComparer.Equals(reference.Name, referencedAssemblyName))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            if (string.Equals(reference.Name, referencedAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
