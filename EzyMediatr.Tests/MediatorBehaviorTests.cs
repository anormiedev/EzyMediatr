using System.Runtime.CompilerServices;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;
using EzyMediatr.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace EzyMediatr.Tests;

public sealed class MediatorBehaviorTests
{
    [Fact]
    public async Task Request_pipeline_runs_in_registration_order_around_processors_and_handler()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var trace = scope.ServiceProvider.GetRequiredService<BehaviorTrace>();

        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new BehaviorRequest());

        Assert.Equal(
            ["first-before", "second-before", "pre", "handler", "post", "second-after", "first-after"],
            trace.Entries);
    }

    [Fact]
    public async Task Stream_pipeline_wraps_pre_processor_and_lazy_handler_enumeration()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var trace = scope.ServiceProvider.GetRequiredService<BehaviorTrace>();
        var values = new List<int>();

        await foreach (var value in scope.ServiceProvider.GetRequiredService<IMediator>().Stream(new BehaviorStreamRequest()))
        {
            values.Add(value);
        }

        Assert.Equal([1, 2], values);
        Assert.Equal(["stream-before", "stream-pre", "stream-handler", "stream-after"], trace.Entries);
    }

    [Fact]
    public async Task Stream_behavior_cleanup_runs_when_the_consumer_stops_early()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var trace = scope.ServiceProvider.GetRequiredService<BehaviorTrace>();

        await foreach (var _ in scope.ServiceProvider
                           .GetRequiredService<IMediator>()
                           .Stream(new BehaviorStreamRequest()))
        {
            break;
        }

        Assert.Equal(["stream-before", "stream-pre", "stream-handler", "stream-after"], trace.Entries);
    }

    [Fact]
    public async Task Notification_pipeline_wraps_sequential_handlers()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var trace = scope.ServiceProvider.GetRequiredService<BehaviorTrace>();

        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(new BehaviorNotification());

        Assert.Equal(["notification-before", "notification-handler-1", "notification-handler-2", "notification-after"], trace.Entries);
    }

    [Fact]
    public async Task Notification_failure_stops_later_handlers()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var trace = scope.ServiceProvider.GetRequiredService<BehaviorTrace>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IMediator>().Publish(new FailingNotification()));

        Assert.Equal(["failing-handler"], trace.Entries);
    }

    [Fact]
    public async Task Cancellation_is_forwarded_to_pipeline_behaviors()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new CancellationRequest(), cancellation.Token));
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddScoped<BehaviorTrace>();
        services.AddEzyMediatr(typeof(MediatorBehaviorTests).Assembly);
        return services;
    }

    public sealed class BehaviorTrace
    {
        public List<string> Entries { get; } = [];
    }

    public sealed record BehaviorRequest : IRequest<int>;

    public sealed record CancellationRequest : IRequest<int>;

    public sealed class CancellationBehavior : IPipelineBehavior<CancellationRequest, int>
    {
        public Task<int> Handle(CancellationRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return next();
        }
    }

    public sealed class CancellationRequestHandler : IRequestHandler<CancellationRequest, int>
    {
        public Task<int> Handle(CancellationRequest request, CancellationToken cancellationToken)
            => Task.FromResult(1);
    }

    public sealed class FirstRequestBehavior(BehaviorTrace trace) : IPipelineBehavior<BehaviorRequest, int>
    {
        public async Task<int> Handle(BehaviorRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        {
            trace.Entries.Add("first-before");
            var response = await next();
            trace.Entries.Add("first-after");
            return response;
        }
    }

    public sealed class SecondRequestBehavior(BehaviorTrace trace) : IPipelineBehavior<BehaviorRequest, int>
    {
        public async Task<int> Handle(BehaviorRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        {
            trace.Entries.Add("second-before");
            var response = await next();
            trace.Entries.Add("second-after");
            return response;
        }
    }

    public sealed class BehaviorRequestPreProcessor(BehaviorTrace trace) : IRequestPreProcessor<BehaviorRequest>
    {
        public Task Process(BehaviorRequest request, CancellationToken cancellationToken)
        {
            trace.Entries.Add("pre");
            return Task.CompletedTask;
        }
    }

    public sealed class BehaviorRequestHandler(BehaviorTrace trace) : IRequestHandler<BehaviorRequest, int>
    {
        public Task<int> Handle(BehaviorRequest request, CancellationToken cancellationToken)
        {
            trace.Entries.Add("handler");
            return Task.FromResult(1);
        }
    }

    public sealed class BehaviorRequestPostProcessor(BehaviorTrace trace) : IRequestPostProcessor<BehaviorRequest, int>
    {
        public Task Process(BehaviorRequest request, int response, CancellationToken cancellationToken)
        {
            trace.Entries.Add("post");
            return Task.CompletedTask;
        }
    }

    public sealed record BehaviorStreamRequest : IStreamRequest<int>;

    public sealed class BehaviorStreamPipeline(BehaviorTrace trace) : IStreamPipelineBehavior<BehaviorStreamRequest, int>
    {
        public async IAsyncEnumerable<int> Handle(
            BehaviorStreamRequest request,
            StreamHandlerDelegate<int> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            trace.Entries.Add("stream-before");

            try
            {
                await foreach (var item in next().WithCancellation(cancellationToken))
                {
                    yield return item;
                }
            }
            finally
            {
                trace.Entries.Add("stream-after");
            }
        }
    }

    public sealed class BehaviorStreamPreProcessor(BehaviorTrace trace) : IRequestPreProcessor<BehaviorStreamRequest>
    {
        public Task Process(BehaviorStreamRequest request, CancellationToken cancellationToken)
        {
            trace.Entries.Add("stream-pre");
            return Task.CompletedTask;
        }
    }

    public sealed class BehaviorStreamHandler(BehaviorTrace trace) : IStreamRequestHandler<BehaviorStreamRequest, int>
    {
        public async IAsyncEnumerable<int> Handle(
            BehaviorStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            trace.Entries.Add("stream-handler");
            yield return 1;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return 2;
        }
    }

    public sealed record BehaviorNotification : INotification;

    public sealed class BehaviorNotificationPipeline(BehaviorTrace trace) : INotificationPipelineBehavior<BehaviorNotification>
    {
        public async Task Handle(BehaviorNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
        {
            trace.Entries.Add("notification-before");
            await next();
            trace.Entries.Add("notification-after");
        }
    }

    public sealed class FirstBehaviorNotificationHandler(BehaviorTrace trace) : INotificationHandler<BehaviorNotification>
    {
        public Task Handle(BehaviorNotification notification, CancellationToken cancellationToken)
        {
            trace.Entries.Add("notification-handler-1");
            return Task.CompletedTask;
        }
    }

    public sealed class SecondBehaviorNotificationHandler(BehaviorTrace trace) : INotificationHandler<BehaviorNotification>
    {
        public Task Handle(BehaviorNotification notification, CancellationToken cancellationToken)
        {
            trace.Entries.Add("notification-handler-2");
            return Task.CompletedTask;
        }
    }

    public sealed record FailingNotification : INotification;

    public sealed class FailingNotificationHandler(BehaviorTrace trace) : INotificationHandler<FailingNotification>
    {
        public Task Handle(FailingNotification notification, CancellationToken cancellationToken)
        {
            trace.Entries.Add("failing-handler");
            throw new InvalidOperationException("Expected test failure.");
        }
    }

    public sealed class SkippedNotificationHandler(BehaviorTrace trace) : INotificationHandler<FailingNotification>
    {
        public Task Handle(FailingNotification notification, CancellationToken cancellationToken)
        {
            trace.Entries.Add("skipped-handler");
            return Task.CompletedTask;
        }
    }
}
