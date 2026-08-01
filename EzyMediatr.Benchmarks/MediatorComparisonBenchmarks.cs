using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;
using EzyMediatr.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using EzyMediator = EzyMediatr.Core.Abstractions.IMediator;
using MediatRMediator = MediatR.IMediator;

[MemoryDiagnoser]
[ShortRunJob]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class MediatorComparisonBenchmarks
{
    private ServiceProvider _ezyProvider = null!;
    private ServiceProvider _mediatRProvider = null!;
    private ServiceProvider _mediatRDefaultProvider = null!;
    private IServiceScope _ezyScope = null!;
    private IServiceScope _mediatRScope = null!;
    private IServiceScope _mediatRDefaultScope = null!;
    private EzyMediator _ezy = null!;
    private MediatRMediator _mediatR = null!;
    private MediatRMediator _mediatRDefault = null!;

    private readonly ComparisonRequest _request = new();
    private readonly ComparisonBehaviorRequest _behaviorRequest = new();
    private readonly ComparisonProcessedRequest _processedRequest = new();
    private readonly ComparisonNotification _notification = new();
    private readonly ComparisonStreamRequest _streamRequest = new();
    private readonly ComparisonBehaviorStreamRequest _behaviorStreamRequest = new();

    [GlobalSetup]
    public void Setup()
    {
        var ezyServices = new ServiceCollection();
        ezyServices.AddEzyMediatr(typeof(MediatorComparisonBenchmarks).Assembly);
        _ezyProvider = ezyServices.BuildServiceProvider();
        _ezyScope = _ezyProvider.CreateScope();
        _ezy = _ezyScope.ServiceProvider.GetRequiredService<EzyMediator>();

        var mediatRServices = CreateMediatRServices(useScopedHandlers: true, addExtensions: true);
        _mediatRProvider = mediatRServices.BuildServiceProvider();
        _mediatRScope = _mediatRProvider.CreateScope();
        _mediatR = _mediatRScope.ServiceProvider.GetRequiredService<MediatRMediator>();

        var mediatRDefaultServices = CreateMediatRServices(useScopedHandlers: false, addExtensions: false);
        _mediatRDefaultProvider = mediatRDefaultServices.BuildServiceProvider();
        _mediatRDefaultScope = _mediatRDefaultProvider.CreateScope();
        _mediatRDefault = _mediatRDefaultScope.ServiceProvider.GetRequiredService<MediatRMediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mediatRDefaultScope.Dispose();
        _mediatRDefaultProvider.Dispose();
        _mediatRScope.Dispose();
        _mediatRProvider.Dispose();
        _ezyScope.Dispose();
        _ezyProvider.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Request")]
    public Task<int> Ezy_Send() => _ezy.Send(_request);

    [Benchmark, BenchmarkCategory("Request")]
    public Task<int> MediatR_Send_ScopedHandler() => _mediatR.Send(_request);

    [Benchmark, BenchmarkCategory("Request")]
    public Task<int> MediatR_Send_DefaultTransientHandler() => _mediatRDefault.Send(_request);

    [Benchmark(Baseline = true), BenchmarkCategory("Behavior")]
    public Task<object> Ezy_Send_OneBehavior() => _ezy.Send(_behaviorRequest);

    [Benchmark, BenchmarkCategory("Behavior")]
    public Task<object> MediatR_Send_OneBehavior() => _mediatR.Send(_behaviorRequest);

    [Benchmark(Baseline = true), BenchmarkCategory("Processors")]
    public Task<object> Ezy_Send_Processors() => _ezy.Send(_processedRequest);

    [Benchmark, BenchmarkCategory("Processors")]
    public Task<object> MediatR_Send_Processors() => _mediatR.Send(_processedRequest);

    [Benchmark(Baseline = true), BenchmarkCategory("Notification")]
    public Task Ezy_Publish() => _ezy.Publish(_notification);

    [Benchmark, BenchmarkCategory("Notification")]
    public Task MediatR_Publish() => _mediatR.Publish(_notification);

    [Benchmark(Baseline = true), BenchmarkCategory("Stream")]
    public Task<int> Ezy_Stream() => Count(_ezy.Stream(_streamRequest));

    [Benchmark, BenchmarkCategory("Stream")]
    public Task<int> MediatR_Stream() => Count(_mediatR.CreateStream(_streamRequest));

    [Benchmark(Baseline = true), BenchmarkCategory("StreamBehavior")]
    public Task<int> Ezy_Stream_OneBehavior() => Count(_ezy.Stream(_behaviorStreamRequest));

    [Benchmark, BenchmarkCategory("StreamBehavior")]
    public Task<int> MediatR_Stream_OneBehavior() => Count(_mediatR.CreateStream(_behaviorStreamRequest));

    private static ServiceCollection CreateMediatRServices(
        bool useScopedHandlers,
        bool addExtensions)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<MediatorComparisonBenchmarks>();
        });

        if (!useScopedHandlers)
        {
            return services;
        }

        ReplaceWithScoped<MediatR.IRequestHandler<ComparisonRequest, int>, ComparisonRequestHandler>(services);
        ReplaceWithScoped<MediatR.IRequestHandler<ComparisonBehaviorRequest, object>, ComparisonBehaviorRequestHandler>(services);
        ReplaceWithScoped<MediatR.IRequestHandler<ComparisonProcessedRequest, object>, ComparisonProcessedRequestHandler>(services);
        ReplaceWithScoped<MediatR.INotificationHandler<ComparisonNotification>, ComparisonNotificationHandler>(services);
        ReplaceWithScoped<MediatR.IStreamRequestHandler<ComparisonStreamRequest, object>, ComparisonStreamHandler>(services);
        ReplaceWithScoped<MediatR.IStreamRequestHandler<ComparisonBehaviorStreamRequest, object>, ComparisonBehaviorStreamHandler>(services);
        if (addExtensions)
        {
            ReplaceWithScoped<MediatR.IPipelineBehavior<ComparisonBehaviorRequest, object>, MediatRComparisonBehavior>(services);
            ReplaceWithScoped<MediatR.IStreamPipelineBehavior<ComparisonBehaviorStreamRequest, object>, MediatRComparisonStreamBehavior>(services);
            ReplaceWithScoped<MediatR.Pipeline.IRequestPreProcessor<ComparisonProcessedRequest>, ComparisonPreProcessor>(services);
            ReplaceWithScoped<MediatR.Pipeline.IRequestPostProcessor<ComparisonProcessedRequest, object>, ComparisonPostProcessor>(services);
            services.AddScoped<
                MediatR.IPipelineBehavior<ComparisonProcessedRequest, object>,
                MediatR.Pipeline.RequestPreProcessorBehavior<ComparisonProcessedRequest, object>>();
            services.AddScoped<
                MediatR.IPipelineBehavior<ComparisonProcessedRequest, object>,
                MediatR.Pipeline.RequestPostProcessorBehavior<ComparisonProcessedRequest, object>>();
        }

        return services;
    }

    private static void ReplaceWithScoped<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        services.RemoveAll<TService>();
        services.AddScoped<TService, TImplementation>();
    }

    private static async Task<int> Count(IAsyncEnumerable<object> stream)
    {
        var count = 0;
        await foreach (var _ in stream.ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }
}

public sealed record ComparisonRequest : IRequest<int>, MediatR.IRequest<int>;

public sealed class ComparisonRequestHandler
    : IRequestHandler<ComparisonRequest, int>, MediatR.IRequestHandler<ComparisonRequest, int>
{
    private static readonly Task<int> Response = Task.FromResult(42);

    public Task<int> Handle(ComparisonRequest request, CancellationToken cancellationToken)
        => Response;
}

public sealed record ComparisonBehaviorRequest : IRequest<object>, MediatR.IRequest<object>;

public sealed class ComparisonBehaviorRequestHandler
    : IRequestHandler<ComparisonBehaviorRequest, object>,
      MediatR.IRequestHandler<ComparisonBehaviorRequest, object>
{
    private static readonly Task<object> Response = Task.FromResult<object>(new());

    public Task<object> Handle(ComparisonBehaviorRequest request, CancellationToken cancellationToken)
        => Response;
}

public sealed class EzyComparisonBehavior : IPipelineBehavior<ComparisonBehaviorRequest, object>
{
    public Task<object> Handle(
        ComparisonBehaviorRequest request,
        RequestHandlerDelegate<object> next,
        CancellationToken cancellationToken)
        => next();
}

public sealed class MediatRComparisonBehavior
    : MediatR.IPipelineBehavior<ComparisonBehaviorRequest, object>
{
    public Task<object> Handle(
        ComparisonBehaviorRequest request,
        MediatR.RequestHandlerDelegate<object> next,
        CancellationToken cancellationToken)
        => next();
}

public sealed record ComparisonProcessedRequest : IRequest<object>, MediatR.IRequest<object>;

public sealed class ComparisonProcessedRequestHandler
    : IRequestHandler<ComparisonProcessedRequest, object>,
      MediatR.IRequestHandler<ComparisonProcessedRequest, object>
{
    private static readonly Task<object> Response = Task.FromResult<object>(new());

    public Task<object> Handle(ComparisonProcessedRequest request, CancellationToken cancellationToken)
        => Response;
}

public sealed class ComparisonPreProcessor
    : IRequestPreProcessor<ComparisonProcessedRequest>,
      MediatR.Pipeline.IRequestPreProcessor<ComparisonProcessedRequest>
{
    public Task Process(ComparisonProcessedRequest request, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class ComparisonPostProcessor
    : IRequestPostProcessor<ComparisonProcessedRequest, object>,
      MediatR.Pipeline.IRequestPostProcessor<ComparisonProcessedRequest, object>
{
    public Task Process(
        ComparisonProcessedRequest request,
        object response,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed record ComparisonNotification : INotification, MediatR.INotification;

public sealed class ComparisonNotificationHandler
    : INotificationHandler<ComparisonNotification>,
      MediatR.INotificationHandler<ComparisonNotification>
{
    public Task Handle(ComparisonNotification notification, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed record ComparisonStreamRequest : IStreamRequest<object>, MediatR.IStreamRequest<object>;

public sealed class ComparisonStreamHandler
    : IStreamRequestHandler<ComparisonStreamRequest, object>,
      MediatR.IStreamRequestHandler<ComparisonStreamRequest, object>
{
    private static readonly object Response = new();

    public async IAsyncEnumerable<object> Handle(
        ComparisonStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return Response;
        await Task.CompletedTask;
    }
}

public sealed record ComparisonBehaviorStreamRequest
    : IStreamRequest<object>, MediatR.IStreamRequest<object>;

public sealed class ComparisonBehaviorStreamHandler
    : IStreamRequestHandler<ComparisonBehaviorStreamRequest, object>,
      MediatR.IStreamRequestHandler<ComparisonBehaviorStreamRequest, object>
{
    private static readonly object Response = new();

    public async IAsyncEnumerable<object> Handle(
        ComparisonBehaviorStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return Response;
        await Task.CompletedTask;
    }
}

public sealed class EzyComparisonStreamBehavior
    : IStreamPipelineBehavior<ComparisonBehaviorStreamRequest, object>
{
    public IAsyncEnumerable<object> Handle(
        ComparisonBehaviorStreamRequest request,
        StreamHandlerDelegate<object> next,
        CancellationToken cancellationToken)
        => next();
}

public sealed class MediatRComparisonStreamBehavior
    : MediatR.IStreamPipelineBehavior<ComparisonBehaviorStreamRequest, object>
{
    public IAsyncEnumerable<object> Handle(
        ComparisonBehaviorStreamRequest request,
        MediatR.StreamHandlerDelegate<object> next,
        CancellationToken cancellationToken)
        => next();
}
