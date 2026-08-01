# EzyMediatr

EzyMediatr is a small mediator for .NET 10 applications. It dispatches requests, streams, and notifications, with optional FluentValidation and transactional `Send` pipelines for EF Core or Dapper.

The package is `EzyMediatr`; it contains the runtime and DI registration extensions.

## Install

```bash
dotnet add package EzyMediatr
```

## Quick start

Register the assembly that contains your handlers. Passing assemblies explicitly is faster and more predictable than scanning every assembly loaded by the process.

```csharp
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddEzyMediatr(typeof(PingHandler).Assembly);

using var serviceProvider = services.BuildServiceProvider();
using var scope = serviceProvider.CreateScope();

var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
var message = await mediator.Send(new Ping("hello"));

public sealed record Ping(string Message) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken)
        => Task.FromResult($"Pong: {request.Message}");
}
```

`IMediator` is scoped. In ASP.NET Core, resolve it through the existing request scope. In a worker or console application, create and dispose a scope for each unit of work, as above. EzyMediatr deliberately uses that scope; it does not create a nested scope per dispatch.

Assemblies are optional. Calling `AddEzyMediatr()` without them scans the non-dynamic assemblies already loaded in the process:

```csharp
services.AddEzyMediatr(options =>
{
    options.UseDapper(_ => new SqlConnection(connectionString));
    options.WrapEveryRequest();
});
```

Automatic scanning is convenient for a single-project application. Explicit assemblies remain faster and more predictable when handlers live in separate projects that might not be loaded yet.

## Messages and handlers

| Message | Handler | Dispatch method | Cardinality |
| --- | --- | --- | --- |
| `IRequest<TResponse>` | `IRequestHandler<TRequest, TResponse>` | `Send` | Exactly one handler |
| `IStreamRequest<TResponse>` | `IStreamRequestHandler<TRequest, TResponse>` | `Stream` | Exactly one handler |
| `INotification` | `INotificationHandler<TNotification>` | `Publish` | Zero or more handlers |

Request and stream handlers are required to be unique. Registration throws a clear exception if more than one implementation is found. Notification handlers are invoked sequentially in registration order; if one throws, later handlers are not invoked. Implement `INotificationPipelineBehavior<TNotification>` to wrap publication with logging, tracing, or other cross-cutting behavior.

```csharp
public sealed record OrderCreated(Guid OrderId) : INotification;

public sealed class SendReceipt : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken)
    {
        // Send a receipt.
        return Task.CompletedTask;
    }
}

await mediator.Publish(new OrderCreated(orderId));
```

## Validation and pipelines

FluentValidation validators in the registered assemblies are discovered automatically. Validation runs before the request handler and throws `ValidationException` when any validator fails.

```csharp
using FluentValidation;

public sealed class PingValidator : AbstractValidator<Ping>
{
    public PingValidator()
    {
        RuleFor(x => x.Message).NotEmpty();
    }
}
```

To disable validation:

```csharp
services.AddEzyMediatr(
    options => options.AddValidationBehavior = false,
    typeof(PingHandler).Assembly);
```

Implement `IPipelineBehavior<TRequest, TResponse>` for cross-cutting behavior. `IRequestPreProcessor<TRequest>` and `IRequestPostProcessor<TRequest, TResponse>` run around the handler as the terminal pipeline operation. A post-processor is completed before a transaction commits.

Streams support `IStreamPipelineBehavior<TRequest, TResponse>` and pre-processors. A stream pre-processor runs once when enumeration begins. Validation, transactions, and request post-processors intentionally apply only to `Send`, because a stream may remain open for an unbounded time. Put post-stream cleanup in a stream behavior's `finally` block so it also runs when enumeration fails, is cancelled, or the consumer stops early.

Behaviors execute in registration order: the first registered behavior is the outermost. The built-in request order is:

```text
custom behaviors
  -> validation
    -> transaction (when enabled for the request)
      -> pre-processors
        -> handler
      -> post-processors
```

Stream and notification behaviors follow the same outermost-first rule. Cancellation tokens are forwarded to every behavior, processor, and handler.

## Transactions

Transactions apply to `Send` only. Mark a request with `ITransactionalRequest`, or call `WrapEveryRequest()` to transact every regular request.
If a transactional request is dispatched without configuring a transaction provider, EzyMediatr throws instead of running it without a transaction.

```csharp
public sealed record CreateOrder(string Customer) : IRequest<Guid>, ITransactionalRequest;
```

Configure global wrapping together with the transaction provider in `AddEzyMediatr`:

```csharp
services.AddEzyMediatr(
    options =>
    {
        options.UseDapper(_ => new SqlConnection(connectionString));
        options.WrapEveryRequest();
    },
    typeof(CreateOrderHandler).Assembly);
```

### EF Core

The transaction is opened on the same scoped `DbContext` injected into the handler. EzyMediatr calls `SaveChangesAsync` and commits only after the handler and post-processors succeed.

```csharp
services.AddEzyMediatr(typeof(CreateOrderHandler).Assembly)
    .UseEfCore<AppDbContext>(options =>
        options.UseSqlServer(connectionString));

public sealed class CreateOrderHandler(AppDbContext db) : IRequestHandler<CreateOrder, Guid>
{
    public Task<Guid> Handle(CreateOrder request, CancellationToken cancellationToken)
    {
        var order = new Order { Id = Guid.NewGuid(), Customer = request.Customer };
        db.Orders.Add(order);
        return Task.FromResult(order.Id);
    }
}
```

If your application already registers `IDbContextFactory<AppDbContext>`, use `.UseEfCore<AppDbContext>()` instead.

### Dapper

EzyMediatr does not take a dependency on Dapper itself; install it in the application that uses it. The transaction is available through `ISqlUnitOfWork`. Always pass both its connection and transaction to Dapper commands.

```csharp
services.AddEzyMediatr(typeof(CreateOrderHandler).Assembly)
    .UseDapper((request, serviceProvider) => request switch
    {
        CreateOrder => new SqlConnection(writeConnectionString),
        _ => new SqlConnection(readConnectionString)
    });

public sealed class CreateOrderHandler(ISqlUnitOfWork uow)
    : IRequestHandler<CreateOrder, Guid>
{
    public async Task<Guid> Handle(CreateOrder request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await uow.Connection.ExecuteAsync(
            "insert into Orders (Id, Customer) values (@Id, @Customer)",
            new { Id = id, request.Customer },
            transaction: uow.Transaction);
        return id;
    }
}
```

`ISqlUnitOfWork` and its transaction are available only while a transactional `Send` is executing. Do not capture either for background work or use it after the handler returns.

## Extensibility

For a custom transaction implementation, provide an `IUnitOfWorkFactory`:

```csharp
services.AddEzyMediatr(typeof(PingHandler).Assembly)
    .UseUnitOfWorkFactory(serviceProvider => new MyUnitOfWorkFactory(serviceProvider));
```

An `IUnitOfWork` owns the transaction boundary: it executes the pipeline operation, commits on success, rolls back on failure, and is disposed after the dispatch.

## Build and pack

```bash
dotnet test EzyMediatr.sln
dotnet pack EzyMediatr.DependencyInjection/EzyMediatr.DependencyInjection.csproj \
  -c Release /p:ContinuousIntegrationBuild=true --output ./nupkgs
```

Update the version in both project files before publishing.

Run the allocation and CPU benchmarks after changing dispatch internals:

```bash
dotnet run -c Release --project EzyMediatr.Benchmarks/EzyMediatr.Benchmarks.csproj
```

## Performance

The default request path avoids reflection after the first request type, nested dependency-injection scopes, LINQ pipeline construction, response boxing, and behavior delegates when no custom behaviors are registered. Optional validators, transactions, processors, and behaviors are instantiated and executed only when they apply.

As an indicative baseline, the included short-run benchmark on an Apple M4 Pro with .NET 10 measured:

| Synthetic operation | Mean | Allocated |
| --- | ---: | ---: |
| `Send` with a completed value-type handler | ~78 ns | 120 B |
| `Send` with one pass-through behavior | ~120 ns | 288 B |
| `Send` with one empty validator | ~187 ns | 792 B |
| `Send` with no-op pre/post-processors | ~134 ns | 192 B |
| `Send` with an in-memory transaction | ~138 ns | 440 B |
| `Publish` with one completed handler | ~33 ns | 40 B |
| `Publish` with one pass-through behavior | ~73 ns | 208 B |
| One-item `Stream` | ~165 ns | 512 B |
| One-item `Stream` with one pass-through behavior | ~192 ns | 680 B |

The 120-byte default-path allocation is the `Task<T>` required by the public `Task<TResponse>` API. Optional-path figures include the costs of their underlying abstractions, such as FluentValidation and async enumeration. Results vary by runtime, hardware, dependency-injection container, and handler implementation, so run the benchmark in your own target environment rather than treating these figures as a service-level guarantee. For handlers that perform I/O, database and network work will dominate this dispatch overhead.

## License

MIT. See [LICENSE](LICENSE).
