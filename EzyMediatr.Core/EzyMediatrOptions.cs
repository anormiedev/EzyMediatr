using System.Data;
using System.Runtime.CompilerServices;
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

    private Lazy<ServiceRegistrationRegistry>? ServiceRegistry { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PipelineFeatures GetPipelineFeatures<TRequest>()
        => ServiceRegistry?.Value.GetFeatures<TRequest>() ?? PipelineFeatures.All;

    internal void SetServiceRegistry(Lazy<ServiceRegistrationRegistry> serviceRegistry)
    {
        ArgumentNullException.ThrowIfNull(serviceRegistry);
        ServiceRegistry = serviceRegistry;
    }

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
        {
            var factory = resolver(sp)
                ?? throw new InvalidOperationException("The configured unit-of-work resolver returned null.");
            var unitOfWork = factory.CreateAsync(cancellationToken)
                ?? throw new InvalidOperationException("The configured unit-of-work factory returned a null task.");
            return new ValueTask<IUnitOfWork>(unitOfWork);
        };
        UsesDapper = false;
        return this;
    }
}

internal sealed class ServiceRegistrationRegistry
{
    private readonly Dictionary<Type, PipelineFeatures> _featuresByRequestType = [];
    private PipelineFeatures _openGenericFeatures;

    public ServiceRegistrationRegistry(IEnumerable<Type> serviceTypes)
    {
        ArgumentNullException.ThrowIfNull(serviceTypes);

        foreach (var serviceType in serviceTypes)
        {
            if (!TryGetFeature(serviceType, out var requestType, out var feature))
            {
                continue;
            }

            if (requestType is null)
            {
                _openGenericFeatures |= feature;
                continue;
            }

            _featuresByRequestType.TryGetValue(requestType, out var existing);
            _featuresByRequestType[requestType] = existing | feature;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PipelineFeatures GetFeatures<TRequest>()
    {
        _featuresByRequestType.TryGetValue(typeof(TRequest), out var features);
        return features | _openGenericFeatures;
    }

    private static bool TryGetFeature(
        Type serviceType,
        out Type? requestType,
        out PipelineFeatures feature)
    {
        requestType = null;
        feature = PipelineFeatures.None;

        if (!serviceType.IsGenericType)
        {
            return false;
        }

        var definition = serviceType.IsGenericTypeDefinition
            ? serviceType
            : serviceType.GetGenericTypeDefinition();

        if (definition == typeof(FluentValidation.IValidator<>))
        {
            feature = PipelineFeatures.Validator;
        }
        else if (definition == typeof(Pipeline.IPipelineBehavior<,>))
        {
            feature = PipelineFeatures.RequestBehavior;
        }
        else if (definition == typeof(Pipeline.IStreamPipelineBehavior<,>))
        {
            feature = PipelineFeatures.StreamBehavior;
        }
        else if (definition == typeof(Pipeline.INotificationPipelineBehavior<>))
        {
            feature = PipelineFeatures.NotificationBehavior;
        }
        else if (definition == typeof(Pipeline.IRequestPreProcessor<>))
        {
            feature = PipelineFeatures.PreProcessor;
        }
        else if (definition == typeof(Pipeline.IRequestPostProcessor<,>))
        {
            feature = PipelineFeatures.PostProcessor;
        }

        if (feature == PipelineFeatures.None)
        {
            return false;
        }

        if (!serviceType.IsGenericTypeDefinition)
        {
            requestType = serviceType.GenericTypeArguments[0];
        }

        return true;
    }
}

[Flags]
internal enum PipelineFeatures : byte
{
    None = 0,
    Validator = 1 << 0,
    RequestBehavior = 1 << 1,
    StreamBehavior = 1 << 2,
    NotificationBehavior = 1 << 3,
    PreProcessor = 1 << 4,
    PostProcessor = 1 << 5,
    All = Validator | RequestBehavior | StreamBehavior | NotificationBehavior | PreProcessor | PostProcessor
}
