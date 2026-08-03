using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using EzyMediatr.Core;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;
using EzyMediatr.Core.Transactions;
using EzyMediatr.DependencyInjection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

BenchmarkSwitcher.FromTypes(
    [typeof(DispatchBenchmarks), typeof(RegistrationBenchmarks), typeof(MediatorComparisonBenchmarks)])
    .Run(args);

[MemoryDiagnoser]
[ShortRunJob]
public class DispatchBenchmarks
{
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private IMediator _mediator = null!;
    private PlainRequestHandler _directHandler = null!;
    private BenchmarkNotificationHandler _directNotificationHandler = null!;
    private BenchmarkStreamHandler _directStreamHandler = null!;
    private readonly PlainRequest _plainRequest = new();
    private readonly RequestWithBehavior _requestWithBehavior = new();
    private readonly ValidatedRequest _validatedRequest = new();
    private readonly ProcessedRequest _processedRequest = new();
    private readonly TransactionalRequest _transactionalRequest = new();
    private readonly BenchmarkNotification _notification = new();
    private readonly BenchmarkNotificationWithBehavior _notificationWithBehavior = new();
    private readonly BenchmarkStreamRequest _streamRequest = new();
    private readonly BenchmarkStreamWithBehaviorRequest _streamWithBehaviorRequest = new();

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUnitOfWorkFactory, BenchmarkUnitOfWorkFactory>();
        services.AddEzyMediatr(
            options => options.UseUnitOfWorkFactory(
                serviceProvider => serviceProvider.GetRequiredService<IUnitOfWorkFactory>()),
            typeof(DispatchBenchmarks).Assembly);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        _mediator = _scope.ServiceProvider.GetRequiredService<IMediator>();
        _directHandler = new PlainRequestHandler();
        _directNotificationHandler = new BenchmarkNotificationHandler();
        _directStreamHandler = new BenchmarkStreamHandler();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    [Benchmark]
    public Task<int> DirectHandler() => _directHandler.Handle(_plainRequest);

    [Benchmark]
    public Task<int> Send() => _mediator.Send(_plainRequest);

    [Benchmark]
    public Task<object> SendWithOneBehavior() => _mediator.Send(_requestWithBehavior);

    [Benchmark]
    public Task<object> SendWithValidator() => _mediator.Send(_validatedRequest);

    [Benchmark]
    public Task<object> SendWithProcessors() => _mediator.Send(_processedRequest);

    [Benchmark]
    public Task<object> SendWithTransaction() => _mediator.Send(_transactionalRequest);

    [Benchmark]
    public Task PublishWithOneHandler() => _mediator.Publish(_notification);

    [Benchmark]
    public Task DirectNotificationHandler() => _directNotificationHandler.Handle(_notification);

    [Benchmark]
    public Task PublishWithOneBehavior() => _mediator.Publish(_notificationWithBehavior);

    [Benchmark]
    public async Task<int> StreamOneItem()
    {
        var count = 0;
        await foreach (var _ in _mediator.Stream(_streamRequest))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public object CreateMediatedStream() => _mediator.Stream(_streamRequest);

    [Benchmark]
    public object CreateDirectStream() => _directStreamHandler.Handle(_streamRequest);

    [Benchmark]
    public async Task<int> DirectStreamOneItem()
    {
        var count = 0;
        await foreach (var _ in _directStreamHandler.Handle(_streamRequest))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> StreamWithOneBehavior()
    {
        var count = 0;
        await foreach (var _ in _mediator.Stream(_streamWithBehaviorRequest))
        {
            count++;
        }

        return count;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class RegistrationBenchmarks
{
    [Benchmark]
    public int ExplicitAssembly()
    {
        var services = new ServiceCollection();
        services.AddEzyMediatr(typeof(DispatchBenchmarks).Assembly);
        return services.Count;
    }

    [Benchmark]
    public int GeneratedRegistration()
    {
        var services = new ServiceCollection();
        services.AddEzyMediatr();
        return services.Count;
    }
}

public sealed record PlainRequest : IRequest<int>;

public sealed class PlainRequestHandler : IRequestHandler<PlainRequest, int>
{
    private static readonly Task<int> Response = Task.FromResult(42);

    public Task<int> Handle(PlainRequest request, CancellationToken cancellationToken = default)
        => Response;
}

public sealed record RequestWithBehavior : IRequest<object>;

public sealed class RequestWithBehaviorHandler : IRequestHandler<RequestWithBehavior, object>
{
    private static readonly Task<object> Response = Task.FromResult<object>(new());

    public Task<object> Handle(RequestWithBehavior request, CancellationToken cancellationToken = default)
        => Response;
}

public sealed class PassThroughBehavior : IPipelineBehavior<RequestWithBehavior, object>
{
    public Task<object> Handle(
        RequestWithBehavior request,
        RequestHandlerDelegate<object> next,
        CancellationToken cancellationToken)
        => next();
}

public sealed record ValidatedRequest : IRequest<object>;

public sealed class ValidatedRequestHandler : IRequestHandler<ValidatedRequest, object>
{
    private static readonly Task<object> Response = Task.FromResult<object>(new());

    public Task<object> Handle(ValidatedRequest request, CancellationToken cancellationToken = default)
        => Response;
}

public sealed class ValidatedRequestValidator : AbstractValidator<ValidatedRequest>
{
}

public sealed record ProcessedRequest : IRequest<object>;

public sealed class ProcessedRequestHandler : IRequestHandler<ProcessedRequest, object>
{
    private static readonly Task<object> Response = Task.FromResult<object>(new());

    public Task<object> Handle(ProcessedRequest request, CancellationToken cancellationToken = default)
        => Response;
}

public sealed class BenchmarkPreProcessor : IRequestPreProcessor<ProcessedRequest>
{
    public Task Process(ProcessedRequest request, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class BenchmarkPostProcessor : IRequestPostProcessor<ProcessedRequest, object>
{
    public Task Process(ProcessedRequest request, object response, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed record TransactionalRequest : IRequest<object>, ITransactionalRequest;

public sealed class TransactionalRequestHandler : IRequestHandler<TransactionalRequest, object>
{
    private static readonly Task<object> Response = Task.FromResult<object>(new());

    public Task<object> Handle(TransactionalRequest request, CancellationToken cancellationToken = default)
        => Response;
}

public sealed class BenchmarkUnitOfWorkFactory : IUnitOfWorkFactory
{
    public Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IUnitOfWork>(new BenchmarkUnitOfWork());
}

public sealed class BenchmarkUnitOfWork : IUnitOfWork
{
    public Task<TResponse> ExecuteAsync<TResponse>(
        Func<CancellationToken, Task<TResponse>> operation,
        CancellationToken cancellationToken = default)
        => operation(cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record BenchmarkNotification : INotification;

public sealed class BenchmarkNotificationHandler : INotificationHandler<BenchmarkNotification>
{
    public Task Handle(BenchmarkNotification notification, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed record BenchmarkNotificationWithBehavior : INotification;

public sealed class BenchmarkNotificationWithBehaviorHandler
    : INotificationHandler<BenchmarkNotificationWithBehavior>
{
    public Task Handle(
        BenchmarkNotificationWithBehavior notification,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class BenchmarkNotificationBehavior
    : INotificationPipelineBehavior<BenchmarkNotificationWithBehavior>
{
    public Task Handle(
        BenchmarkNotificationWithBehavior notification,
        NotificationHandlerDelegate next,
        CancellationToken cancellationToken)
        => next();
}

public sealed record BenchmarkStreamRequest : IStreamRequest<object>;

public sealed class BenchmarkStreamHandler : IStreamRequestHandler<BenchmarkStreamRequest, object>
{
    private static readonly object Response = new();

    public async IAsyncEnumerable<object> Handle(
        BenchmarkStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return Response;
        await Task.CompletedTask;
    }
}

public sealed record BenchmarkStreamWithBehaviorRequest : IStreamRequest<object>;

public sealed class BenchmarkStreamWithBehaviorHandler
    : IStreamRequestHandler<BenchmarkStreamWithBehaviorRequest, object>
{
    private static readonly object Response = new();

    public async IAsyncEnumerable<object> Handle(
        BenchmarkStreamWithBehaviorRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return Response;
        await Task.CompletedTask;
    }
}

public sealed class BenchmarkStreamBehavior
    : IStreamPipelineBehavior<BenchmarkStreamWithBehaviorRequest, object>
{
    public IAsyncEnumerable<object> Handle(
        BenchmarkStreamWithBehaviorRequest request,
        StreamHandlerDelegate<object> next,
        CancellationToken cancellationToken)
        => next();
}
