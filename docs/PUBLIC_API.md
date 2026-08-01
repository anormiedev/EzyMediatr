# EzyMediatr public API guide

This is the consumer reference for EzyMediatr. It covers registration, dispatch, pipelines, validation, transactions, result values, and the guarantees applications can rely on.

EzyMediatr targets .NET 11. Install the unified package:

```bash
dotnet add package EzyMediatr
```

## Namespaces

| Namespace | Purpose |
| --- | --- |
| `EzyMediatr.Core` | `Mediator` and `EzyMediatrOptions` |
| `EzyMediatr.Core.Abstractions` | Messages and `IMediator` |
| `EzyMediatr.Core.Handlers` | Handler contracts |
| `EzyMediatr.Core.Pipeline` | Behaviors, processors, and delegates |
| `EzyMediatr.Core.Results` | Optional result value types |
| `EzyMediatr.Core.Transactions` | Unit-of-work contracts and implementations |
| `EzyMediatr.DependencyInjection` | Registration extensions and builder |

## Registration

The simplest registration scans already-loaded application assemblies that directly reference `EzyMediatr.Core`:

```csharp
using EzyMediatr.DependencyInjection;

builder.Services.AddEzyMediatr();
```

Automatic discovery does not load assemblies from disk. Pass explicit assemblies when a handler assembly might not be loaded yet, the process hosts plugins, or predictable startup work matters:

```csharp
builder.Services.AddEzyMediatr(
    typeof(CreateOrderHandler).Assembly,
    typeof(OrderCreatedHandler).Assembly);
```

Duplicate assembly arguments are ignored. Null entries are rejected. Scanning ignores abstract types, interfaces, and open generic implementation types.

There are two configuration styles.

Options callback:

```csharp
builder.Services.AddEzyMediatr(
    options =>
    {
        options.AddValidationBehavior = true;
        options.UseDapper(_ => new SqlConnection(connectionString));
        options.WrapEveryRequest();
    },
    typeof(CreateOrderHandler).Assembly);
```

Fluent builder:

```csharp
builder.Services
    .AddEzyMediatr(typeof(CreateOrderHandler).Assembly)
    .UseDapper(() => new SqlConnection(connectionString))
    .WrapEveryRequest();
```

`IMediator`, handlers, processors, behaviors, and transaction accessors are scoped. Resolve the mediator from an existing ASP.NET Core request scope. In a worker or console application, create and dispose a scope for each logical operation. EzyMediatr does not create a nested scope per dispatch.

### Configuration reference

| API | Effect |
| --- | --- |
| `AddEzyMediatr(params Assembly[])` | Registers EzyMediatr and returns `EzyMediatrBuilder` |
| `AddEzyMediatr(Action<EzyMediatrOptions>?, params Assembly[])` | Configures options and returns `IServiceCollection` |
| `EzyMediatrBuilder.Options` | Exposes the builder's options instance |
| `AddValidationBehavior` | Enables built-in `Send` validation; defaults to `true` |
| `WrapEveryRequest()` | Makes every `Send` transactional and sets the read-only `WrapAllRequests` state |
| `UseDapper(...)` | Selects Dapper-style `IDbConnection` transaction ownership |
| `UseEfCore<TContext>(...)` | Selects a scoped EF Core context transaction |
| `UseUnitOfWorkFactory(...)` | Selects a custom transaction provider |

`EzyMediatrOptions` is registered as a singleton. Treat it as immutable after the service provider starts dispatching. Only one unit-of-work provider is active; a later `UseDapper`, `UseEfCore`, or `UseUnitOfWorkFactory` call replaces the earlier provider.

## Messages and handlers

| Message | Handler | Dispatch | Cardinality |
| --- | --- | --- | --- |
| `IRequest<TResponse>` | `IRequestHandler<TRequest, TResponse>` | `Send` | Exactly one |
| `IStreamRequest<TResponse>` | `IStreamRequestHandler<TRequest, TResponse>` | `Stream` | Exactly one |
| `INotification` | `INotificationHandler<TNotification>` | `Publish` | Zero or more |

### Requests

A request produces one response:

```csharp
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;

public sealed record GetOrder(Guid Id) : IRequest<OrderDto?>;

public sealed class GetOrderHandler(AppDbContext db)
    : IRequestHandler<GetOrder, OrderDto?>
{
    public Task<OrderDto?> Handle(GetOrder request, CancellationToken cancellationToken)
        => db.Orders
            .Where(order => order.Id == request.Id)
            .Select(order => new OrderDto(order.Id, order.Customer))
            .SingleOrDefaultAsync(cancellationToken);
}
```

Registering more than one closed request handler for the same request fails during EzyMediatr registration.

### Streams

A stream request produces an asynchronous sequence:

```csharp
using System.Runtime.CompilerServices;

public sealed record CountTo(int End) : IStreamRequest<int>;

public sealed class CountToHandler : IStreamRequestHandler<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(
        CountTo request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var value = 1; value <= request.End; value++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }
}
```

Execution is deferred until the returned sequence is enumerated. Stream handlers must also be unique.

### Notifications

A notification can have zero or more handlers:

```csharp
public sealed record OrderCreated(Guid OrderId) : INotification;

public sealed class SendReceipt : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken)
    {
        // Perform the side effect.
        return Task.CompletedTask;
    }
}
```

Handlers run sequentially in registration order. Publishing with no handlers succeeds. If one handler fails, later handlers are not invoked. Publication is in-process work; it is not a durable message bus and does not persist or retry notifications.

## Dispatch

Inject `IMediator` and call the method matching the message kind:

```csharp
public sealed class OrdersController(IMediator mediator) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderDto>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var order = await mediator.Send(new GetOrder(id), cancellationToken);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpGet("count/{end:int}")]
    public IAsyncEnumerable<int> Count(
        int end,
        CancellationToken cancellationToken)
        => mediator.Stream(new CountTo(end), cancellationToken);
}
```

| Method | Input | Output |
| --- | --- | --- |
| `Send<TResponse>` | `IRequest<TResponse>` | `Task<TResponse>` |
| `Stream<TResponse>` | `IStreamRequest<TResponse>` | `IAsyncEnumerable<TResponse>` |
| `Publish<TNotification>` | `TNotification : INotification` | `Task` |

Null messages throw `ArgumentNullException`. Cancellation tokens are forwarded, but cancellation remains cooperative. `Mediator` is the default public implementation; applications should normally resolve `IMediator` rather than construct it directly.

## Pipelines

### Request behaviors

Implement `IPipelineBehavior<TRequest, TResponse>` to wrap `Send`:

```csharp
public sealed class TimingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next().ConfigureAwait(false);
        }
        finally
        {
            Console.WriteLine(Stopwatch.GetElapsedTime(started));
        }
    }
}
```

Closed implementations in scanned assemblies are registered automatically. Register open generic behaviors explicitly before `AddEzyMediatr`:

```csharp
builder.Services.AddScoped(
    typeof(IPipelineBehavior<,>),
    typeof(TimingBehavior<,>));
builder.Services.AddEzyMediatr(typeof(CreateOrderHandler).Assembly);
```

The first registered behavior is outermost. The built-in `Send` order is:

```text
custom request behaviors
  -> validation
    -> transaction, when required
      -> pre-processors
        -> handler
      -> post-processors
```

All custom request behaviors are outside the built-in validation and transaction stages. A behavior can short-circuit by not calling `next`.

### Processors

`IRequestPreProcessor<TRequest>` runs before the terminal handler. Its request constraint is `IBaseRequest`, so it supports regular and stream requests.

`IRequestPostProcessor<TRequest, TResponse>` runs after a successful regular request handler. It does not run for streams or notifications. In a transaction, post-processors finish before commit. Multiple processors run sequentially in registration order.

### Stream behaviors

Use `IStreamPipelineBehavior<TRequest, TResponse>` to wrap enumeration:

```csharp
public sealed class StreamCleanupBehavior<TRequest, TResponse>
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in next()
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            // Completion, failure, cancellation, or early disposal.
        }
    }
}
```

Stream pre-processors run once when enumeration starts. There is no stream post-processor: put cleanup in a stream behavior's `finally`. Validation, transaction wrapping, and request post-processors apply only to `Send` because a stream may remain open indefinitely.

### Notification behaviors

`INotificationPipelineBehavior<TNotification>` wraps the complete sequential notification-handler chain. Notification and stream behaviors are also outermost-first in registration order.

## Validation

FluentValidation validators from scanned assemblies are discovered automatically. Before `Send` reaches its handler, all `IValidator<TRequest>` implementations run sequentially. Their failures are collected into one `FluentValidation.ValidationException`.

```csharp
public sealed class CreateOrderValidator : AbstractValidator<CreateOrder>
{
    public CreateOrderValidator()
        => RuleFor(request => request.Customer).NotEmpty().MaximumLength(200);
}
```

Disable the built-in stage globally when validation is owned elsewhere:

```csharp
builder.Services.AddEzyMediatr(
    options => options.AddValidationBehavior = false,
    typeof(CreateOrderHandler).Assembly);
```

Validation applies only to `Send` and finishes before a transaction opens.

## Transactions

Transactions apply only to `Send`. Configure one provider, then select requests individually or globally.

### Per-request and global selection

Implement `ITransactionalRequest` for an individual command:

```csharp
public sealed record CreateOrder(string Customer)
    : IRequest<Guid>, ITransactionalRequest;
```

Call `WrapEveryRequest()` to transact every `Send`:

```csharp
builder.Services
    .AddEzyMediatr(typeof(CreateOrderHandler).Assembly)
    .UseDapper(() => new SqlConnection(connectionString))
    .WrapEveryRequest();
```

A transactional request without a provider throws `InvalidOperationException`; it never silently runs without a transaction. Global wrapping does not affect `Stream` or `Publish`.

A nested transactional `Send` in the same async flow joins the active unit of work. Only the outer dispatch creates, commits, and disposes it. Do not run concurrent operations through one transaction: database contexts, connections, and transactions are generally not thread-safe.

### Dapper

EzyMediatr uses `IDbConnection` and does not depend on Dapper. Install Dapper and the database provider in the application:

```bash
dotnet add package Dapper
dotnet add package Microsoft.Data.SqlClient
```

Return a new connection for each top-level transaction:

```csharp
builder.Services
    .AddEzyMediatr(typeof(CreateOrderHandler).Assembly)
    .UseDapper(serviceProvider =>
        new SqlConnection(
            serviceProvider.GetRequiredService<IConfiguration>()
                .GetConnectionString("Orders")
                ?? throw new InvalidOperationException("Missing Orders connection string.")));
```

The request-aware overload can route transactions:

```csharp
.UseDapper((request, serviceProvider) => request switch
{
    IUseReportingDatabase => new SqlConnection(reportingConnectionString),
    _ => new SqlConnection(primaryConnectionString)
});
```

Inject `ISqlUnitOfWork` and pass its transaction to every command:

```csharp
public sealed class CreateOrderHandler(ISqlUnitOfWork unitOfWork)
    : IRequestHandler<CreateOrder, Guid>
{
    public async Task<Guid> Handle(CreateOrder request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var command = new CommandDefinition(
            "insert into Orders (Id, Customer) values (@Id, @Customer)",
            new { Id = id, request.Customer },
            transaction: unitOfWork.Transaction,
            cancellationToken: cancellationToken);

        await unitOfWork.Connection.ExecuteAsync(command);
        return id;
    }
}
```

EzyMediatr opens the connection if needed, begins the transaction, commits after the complete inner request pipeline, and disposes the transaction and connection. It attempts rollback on operation or commit failure while preserving the original exception.

`ISqlUnitOfWork` is usable only during a transactional `Send`. Never capture its resources for background work or use them after dispatch. Always parameterize SQL.

### EF Core

The builder can register and configure the context:

```csharp
builder.Services
    .AddEzyMediatr(typeof(CreateOrderHandler).Assembly)
    .UseEfCore<AppDbContext>(options =>
        options.UseSqlServer(connectionString));
```

If the application already registers the scoped context, use the parameterless overload:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services
    .AddEzyMediatr(typeof(CreateOrderHandler).Assembly)
    .UseEfCore<AppDbContext>();
```

`TContext` must be resolvable from the current DI scope. The handler receives that same instance. EzyMediatr begins a database transaction, runs the handler and post-processors, calls `SaveChangesAsync`, and commits. The application scope owns the context.

The options API also exposes `UseEfCore<TContext>(Func<IBaseRequest, bool> when)`. A transactional request rejected by this advanced guard throws instead of running without a transaction.

### Custom unit of work

Implement `IUnitOfWorkFactory` and `IUnitOfWork` for another transaction system:

```csharp
builder.Services
    .AddEzyMediatr(typeof(CreateOrderHandler).Assembly)
    .UseUnitOfWorkFactory(serviceProvider =>
        serviceProvider.GetRequiredService<MyUnitOfWorkFactory>());
```

The factory must return a new, non-null unit of work for each top-level transaction. `IUnitOfWork.ExecuteAsync` owns commit and rollback semantics. EzyMediatr disposes the returned instance.

The public `DapperUnitOfWork`, `DapperUnitOfWorkFactory`, `EfCoreUnitOfWork<TContext>`, and `EfCoreUnitOfWorkFactory<TContext>` types support advanced composition. Prefer builder APIs for normal setup because they also register scoped accessors.

## Result values

`Result` and `Result<T>` are optional readonly response types. The mediator does not interpret them:

```csharp
Result<OrderDto> result = order is null
    ? Result<OrderDto>.Failure("Order was not found.")
    : Result<OrderDto>.Success(order);
```

| Type | Success | Failure | Value |
| --- | --- | --- | --- |
| `Result` | `Result.Success()` | `Result.Failure(error)` | None |
| `Result<T>` | `Result<T>.Success(value)` | `Result<T>.Failure(error)` | `T? Value` |

Both expose `IsSuccess` and `Error`. A default-initialized result is unsuccessful with no error; use the factories if that state is not meaningful to the application.

## Guarantees and boundaries

- Request and stream handlers are unique; duplicates fail during registration.
- Notification handlers and processors run sequentially in registration order.
- Behaviors wrap the inner pipeline in registration order, with the first registered behavior outermost.
- Validation finishes before transaction creation.
- Request post-processors finish before transaction commit.
- Failure or cancellation stops remaining sequential work and propagates.
- Nested transactional sends in one async flow share the outer unit of work.
- Stream work begins on enumeration and releases resources through enumerator disposal.
- EzyMediatr does not provide durable messaging, retries, distributed transactions, authentication, authorization, or process isolation.

Use explicit assembly registration when loaded code is not fully trusted. Authorize before side effects, parameterize SQL, set timeouts on external work, and never leak scoped resources into background jobs.

## Public API index

| Area | Public types |
| --- | --- |
| Dispatch | `IMediator`, `Mediator` |
| Messages | `IBaseRequest`, `IRequest<TResponse>`, `IStreamRequest<TResponse>`, `INotification`, `ITransactionalRequest` |
| Handlers | `IRequestHandler<TRequest, TResponse>`, `IStreamRequestHandler<TRequest, TResponse>`, `INotificationHandler<TNotification>` |
| Pipeline | `IPipelineBehavior<TRequest, TResponse>`, `IStreamPipelineBehavior<TRequest, TResponse>`, `INotificationPipelineBehavior<TNotification>`, `IRequestPreProcessor<TRequest>`, `IRequestPostProcessor<TRequest, TResponse>` |
| Delegates | `RequestHandlerDelegate<TResponse>`, `StreamHandlerDelegate<TResponse>`, `NotificationHandlerDelegate` |
| Built-ins | `ValidationBehavior<TRequest, TResponse>`, `TransactionBehavior<TRequest, TResponse>` |
| Configuration | `EzyMediatrOptions`, `EzyMediatrBuilder`, `ServiceCollectionExtensions` |
| Results | `IResult`, `Result`, `Result<T>` |
| Transactions | `IUnitOfWork`, `IUnitOfWorkFactory`, `ISqlUnitOfWork`, `DapperUnitOfWork`, `DapperUnitOfWorkFactory`, `EfCoreUnitOfWork<TContext>`, `EfCoreUnitOfWorkFactory<TContext>` |

`ValidationBehavior` and `TransactionBehavior` are public for direct composition and testing, but the runtime already executes their built-in stages. Do not also register them as custom behaviors unless intentionally running a stage twice.
