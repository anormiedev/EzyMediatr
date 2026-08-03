using System.Data;
using System.Diagnostics.CodeAnalysis;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.Core.Pipeline;
using EzyMediatr.Core.Transactions;
using EzyMediatr.DependencyInjection;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EzyMediatr.Tests;

public sealed class MediatorTransactionTests
{
    [Fact]
    public async Task Send_uses_the_callers_scoped_services()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopeMarker>();
        services.AddEzyMediatr(typeof(MediatorTransactionTests).Assembly);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var expected = scope.ServiceProvider.GetRequiredService<ScopeMarker>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var response = await mediator.Send(new ScopedRequest());

        Assert.Same(expected, response);
    }

    [Fact]
    public async Task Dapper_handler_receives_the_connection_and_transaction_started_by_the_pipeline()
    {
        var services = new ServiceCollection();
        services.AddEzyMediatr(typeof(MediatorTransactionTests).Assembly)
            .UseDapper((request, _) =>
            {
                Assert.IsType<DapperRequest>(request);
                return new TestDbConnection();
            });
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new DapperRequest());

        Assert.True(result.UsesPipelineConnection);
        Assert.True(result.HasTransaction);
        Assert.True(result.Connection.LastTransaction!.Committed);
    }

    [Fact]
    public async Task Concurrent_dapper_sends_keep_their_transactions_in_their_own_async_flow()
    {
        var services = new ServiceCollection();
        services.AddEzyMediatr(typeof(MediatorTransactionTests).Assembly)
            .UseDapper((request, _) => new TestDbConnection(((ConcurrentDapperRequest)request).Name));
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var results = await Task.WhenAll(
            mediator.Send(new ConcurrentDapperRequest("first")),
            mediator.Send(new ConcurrentDapperRequest("second")));

        Assert.Equal(new[] { "first", "second" }, results);
    }

    [Fact]
    public async Task EfCore_handler_changes_are_saved_on_the_transaction_context()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<TestDbContext>(options => options
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddEzyMediatr(typeof(MediatorTransactionTests).Assembly)
            .UseEfCore<TestDbContext>();
        using var provider = services.BuildServiceProvider(validateScopes: true);

        await using (var setupContext = await provider.GetRequiredService<IDbContextFactory<TestDbContext>>().CreateDbContextAsync())
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new EfCoreRequest("transactional"));
        }

        await using var verificationContext = await provider.GetRequiredService<IDbContextFactory<TestDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await verificationContext.Records.CountAsync());
    }

    [Fact]
    public async Task Post_processors_run_before_the_transaction_commits()
    {
        var services = new ServiceCollection();
        services.AddScoped<RecordingState>();
        services.AddEzyMediatr(options => options.UseUnitOfWorkFactory(sp => new RecordingUnitOfWorkFactory(sp.GetRequiredService<RecordingState>())), typeof(MediatorTransactionTests).Assembly);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var state = scope.ServiceProvider.GetRequiredService<RecordingState>();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new PostProcessRequest());

        Assert.True(state.PostProcessorRan);
        Assert.True(state.Committed);
    }

    [Fact]
    public async Task Validation_can_be_disabled_through_options()
    {
        var services = new ServiceCollection();
        services.AddEzyMediatr(options => options.AddValidationBehavior = false, typeof(MediatorTransactionTests).Assembly);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ValidationRequest());

        Assert.Equal(42, response);
    }

    [Fact]
    public void Scanned_validators_are_registered_by_interface_and_concrete_type()
    {
        var services = new ServiceCollection();
        services.AddEzyMediatr(typeof(MediatorTransactionTests).Assembly);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IValidator<ValidationRequest>>());
        Assert.NotNull(scope.ServiceProvider.GetService<ValidationRequestValidator>());
    }

    [Fact]
    public async Task Transactional_request_without_a_configured_provider_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddEzyMediatr(typeof(MediatorTransactionTests).Assembly);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IMediator>().Send(new DapperRequest()));

        Assert.Contains("UnitOfWork is not configured", exception.Message);
    }

    [Fact]
    public async Task Validation_failure_does_not_open_a_transaction()
    {
        var connectionFactoryCalls = 0;
        var services = new ServiceCollection();
        services.AddEzyMediatr(typeof(MediatorTransactionTests).Assembly)
            .UseDapper(_ =>
            {
                connectionFactoryCalls++;
                return new TestDbConnection();
            });
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        await Assert.ThrowsAsync<ValidationException>(
            () => scope.ServiceProvider.GetRequiredService<IMediator>().Send(new InvalidTransactionalRequest()));

        Assert.Equal(0, connectionFactoryCalls);
    }

    [Fact]
    public async Task Wrap_every_request_can_be_enabled_in_mediator_configuration()
    {
        TestDbConnection? connection = null;
        var services = new ServiceCollection();
        services.AddEzyMediatr(
            options =>
            {
                options.UseDapper(_ => connection = new TestDbConnection());
                options.WrapEveryRequest();
            });
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<IMediator>()
            .Send(new RegularRequest());

        Assert.Equal(42, response);
        Assert.True(connection!.LastTransaction!.Committed);
    }

    [Fact]
    public async Task Unit_of_work_rejects_a_null_operation_before_opening_resources()
    {
        var connection = new TestDbConnection();
        var unitOfWork = new DapperUnitOfWork(connection);

        await Assert.ThrowsAsync<ArgumentNullException>(() => unitOfWork.ExecuteAsync<int>(null!));

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Nested_transactional_sends_join_the_active_unit_of_work()
    {
        var services = new ServiceCollection();
        services.AddScoped<NestedTransactionState>();
        services.AddEzyMediatr(
            options => options.UseUnitOfWorkFactory(serviceProvider =>
                new NestedUnitOfWorkFactory(serviceProvider.GetRequiredService<NestedTransactionState>())),
            typeof(MediatorTransactionTests).Assembly);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new OuterTransactionalRequest());
        var state = scope.ServiceProvider.GetRequiredService<NestedTransactionState>();

        Assert.Equal(42, result);
        Assert.Equal(1, state.FactoryCalls);
        Assert.Equal(1, state.ExecutionCalls);
    }

    public sealed record ScopedRequest : IRequest<ScopeMarker>;
    public sealed class ScopedRequestHandler(ScopeMarker marker) : IRequestHandler<ScopedRequest, ScopeMarker>
    {
        public Task<ScopeMarker> Handle(ScopedRequest request, CancellationToken cancellationToken) => Task.FromResult(marker);
    }

    public sealed class ScopeMarker;

    public sealed record RegularRequest : IRequest<int>;
    public sealed class RegularRequestHandler : IRequestHandler<RegularRequest, int>
    {
        public Task<int> Handle(RegularRequest request, CancellationToken cancellationToken)
            => Task.FromResult(42);
    }

    public sealed record OuterTransactionalRequest : IRequest<int>, ITransactionalRequest;
    public sealed record InnerTransactionalRequest : IRequest<int>, ITransactionalRequest;

    public sealed class OuterTransactionalRequestHandler(IMediator mediator)
        : IRequestHandler<OuterTransactionalRequest, int>
    {
        public Task<int> Handle(OuterTransactionalRequest request, CancellationToken cancellationToken)
            => mediator.Send(new InnerTransactionalRequest(), cancellationToken);
    }

    public sealed class InnerTransactionalRequestHandler : IRequestHandler<InnerTransactionalRequest, int>
    {
        public Task<int> Handle(InnerTransactionalRequest request, CancellationToken cancellationToken)
            => Task.FromResult(42);
    }

    public sealed class NestedTransactionState
    {
        public int FactoryCalls { get; set; }
        public int ExecutionCalls { get; set; }
    }

    public sealed class NestedUnitOfWorkFactory(NestedTransactionState state) : IUnitOfWorkFactory
    {
        public Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken = default)
        {
            state.FactoryCalls++;
            return Task.FromResult<IUnitOfWork>(new NestedUnitOfWork(state));
        }
    }

    public sealed class NestedUnitOfWork(NestedTransactionState state) : IUnitOfWork
    {
        public Task<TResponse> ExecuteAsync<TResponse>(
            Func<CancellationToken, Task<TResponse>> operation,
            CancellationToken cancellationToken = default)
        {
            state.ExecutionCalls++;
            return operation(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public sealed record ValidationRequest : IRequest<int>;
    public sealed class ValidationRequestHandler : IRequestHandler<ValidationRequest, int>
    {
        public Task<int> Handle(ValidationRequest request, CancellationToken cancellationToken) => Task.FromResult(42);
    }

    public sealed class ValidationRequestValidator : AbstractValidator<ValidationRequest>
    {
        public ValidationRequestValidator()
        {
            RuleFor(_ => _).Must(_ => false);
        }
    }

    public sealed record InvalidTransactionalRequest : IRequest<int>, ITransactionalRequest;
    public sealed class InvalidTransactionalRequestHandler : IRequestHandler<InvalidTransactionalRequest, int>
    {
        public Task<int> Handle(InvalidTransactionalRequest request, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    public sealed class InvalidTransactionalRequestValidator : AbstractValidator<InvalidTransactionalRequest>
    {
        public InvalidTransactionalRequestValidator()
        {
            RuleFor(_ => _).Must(_ => false);
        }
    }

    public sealed record DapperRequest : IRequest<DapperResult>, ITransactionalRequest;
    public sealed record DapperResult(bool UsesPipelineConnection, bool HasTransaction, TestDbConnection Connection);
    public sealed class DapperRequestHandler(ISqlUnitOfWork unitOfWork) : IRequestHandler<DapperRequest, DapperResult>
    {
        public Task<DapperResult> Handle(DapperRequest request, CancellationToken cancellationToken)
        {
            var transaction = unitOfWork.Transaction as TestDbTransaction;
            return Task.FromResult(new DapperResult(
                true,
                transaction is not null,
                (TestDbConnection)unitOfWork.Connection));
        }
    }

    public sealed record ConcurrentDapperRequest(string Name) : IRequest<string>, ITransactionalRequest;
    public sealed class ConcurrentDapperRequestHandler(ISqlUnitOfWork unitOfWork) : IRequestHandler<ConcurrentDapperRequest, string>
    {
        public async Task<string> Handle(ConcurrentDapperRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return ((TestDbConnection)unitOfWork.Connection).Name;
        }
    }

    public sealed record EfCoreRequest(string Value) : IRequest<int>, ITransactionalRequest;
    public sealed class EfCoreRequestHandler(TestDbContext context) : IRequestHandler<EfCoreRequest, int>
    {
        public Task<int> Handle(EfCoreRequest request, CancellationToken cancellationToken)
        {
            context.Records.Add(new TestRecord { Value = request.Value });
            return Task.FromResult(0);
        }
    }

    public sealed record PostProcessRequest : IRequest<int>, ITransactionalRequest;
    public sealed class PostProcessRequestHandler : IRequestHandler<PostProcessRequest, int>
    {
        public Task<int> Handle(PostProcessRequest request, CancellationToken cancellationToken) => Task.FromResult(1);
    }

    public sealed class PostProcessRequestPostProcessor(RecordingState state) : IRequestPostProcessor<PostProcessRequest, int>
    {
        public Task Process(PostProcessRequest request, int response, CancellationToken cancellationToken)
        {
            Assert.False(state.Committed);
            state.PostProcessorRan = true;
            return Task.CompletedTask;
        }
    }

    public sealed class RecordingState
    {
        public bool Committed { get; set; }
        public bool PostProcessorRan { get; set; }
    }

    public sealed class RecordingUnitOfWork(RecordingState state) : IUnitOfWork
    {
        public async Task<TResponse> ExecuteAsync<TResponse>(Func<CancellationToken, Task<TResponse>> operation, CancellationToken cancellationToken = default)
        {
            var response = await operation(cancellationToken);
            state.Committed = true;
            return response;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public sealed class RecordingUnitOfWorkFactory(RecordingState state) : IUnitOfWorkFactory
    {
        public Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IUnitOfWork>(new RecordingUnitOfWork(state));
    }

    public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestRecord> Records => Set<TestRecord>();
    }

    public sealed class TestRecord
    {
        public int Id { get; set; }
        public string Value { get; set; } = string.Empty;
    }

    public sealed class TestDbConnection(string name = "test") : IDbConnection
    {
        [AllowNull]
        public string ConnectionString { get; set; } = string.Empty;
        public int ConnectionTimeout => 0;
        public string Database => name;
        public string Name => name;
        public ConnectionState State { get; private set; } = ConnectionState.Closed;
        public TestDbTransaction? LastTransaction { get; private set; }

        public IDbTransaction BeginTransaction() => LastTransaction = new TestDbTransaction(this);
        public IDbTransaction BeginTransaction(IsolationLevel il) => BeginTransaction();
        public void ChangeDatabase(string databaseName) { }
        public void Close() => State = ConnectionState.Closed;
        public IDbCommand CreateCommand() => throw new NotSupportedException();
        public void Open() => State = ConnectionState.Open;
        public void Dispose() => Close();
    }

    public sealed class TestDbTransaction(TestDbConnection connection) : IDbTransaction
    {
        public bool Committed { get; private set; }
        public IDbConnection Connection => connection;
        public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        public void Commit() => Committed = true;
        public void Rollback() { }
        public void Dispose() { }
    }
}
