# EzyMediatr

EzyMediatr is a small mediator for .NET 11 applications. It dispatches requests, streams, and notifications, with optional FluentValidation and transactional `Send` pipelines for EF Core or Dapper.

The package is `EzyMediatr`; it contains the runtime and DI registration extensions.

## Documentation

- [Public API guide](docs/PUBLIC_API.md) — consumer setup, messages, pipelines, validation, transactions, and the complete public surface.
- [Code and architecture guide](docs/INTERNALS.md) — runtime design, performance strategy, invariants, ownership, and contributor checks.

## Install

```bash
dotnet add package EzyMediatr
```

## Quick start

Register EzyMediatr. The package's incremental source generator emits direct registrations for handlers in the current compilation, so the zero-argument API normally performs no assembly scan at startup.

```csharp
using EzyMediatr.Core.Abstractions;
using EzyMediatr.Core.Handlers;
using EzyMediatr.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddEzyMediatr();

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

Assemblies are optional. Calling `AddEzyMediatr()` without them applies the generated registration table. If source generation is unavailable, it falls back to scanning non-dynamic, already-loaded assemblies that reference `EzyMediatr.Core`:

```csharp
services.AddEzyMediatr(options =>
{
    options.UseDapper(_ => new SqlConnection(connectionString));
    options.WrapEveryRequest();
});
```

The generator covers concrete, closed handlers, behaviors, processors, and public FluentValidation validators. If a relevant type cannot be referenced safely from generated code, diagnostic `EZM001` is emitted and that compilation requests runtime discovery instead. Pass explicit assemblies when a handler project may not be loaded yet, when loading plugins built without the generator, or when the process hosts plugins you do not want registered. Duplicate explicit assemblies are ignored. `EzyMediatrBuilder.UsesGeneratedRegistrations` reports which path the zero-argument call selected.

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

A nested transactional `Send` in the same asynchronous flow joins the active unit of work. Only the outer dispatch creates, commits, and disposes it. Do not run concurrent database operations inside one transaction: `DbContext`, `DbConnection`, and `DbTransaction` implementations are generally not safe for concurrent use.

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

If your application already makes `AppDbContext` resolvable from the current dependency-injection scope, use `.UseEfCore<AppDbContext>()` instead.

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
The connection returned by the configured Dapper factory is owned and disposed by EzyMediatr. Return a new connection for each top-level transactional dispatch, and always parameterize SQL values rather than concatenating untrusted input.

## Extensibility

For a custom transaction implementation, provide an `IUnitOfWorkFactory`:

```csharp
services.AddEzyMediatr(typeof(PingHandler).Assembly)
    .UseUnitOfWorkFactory(serviceProvider => new MyUnitOfWorkFactory(serviceProvider));
```

An `IUnitOfWork` owns the transaction boundary: it executes the pipeline operation, commits on success, rolls back on failure, and is disposed after the dispatch. A custom factory must return a new, non-null unit of work for each top-level transaction; EzyMediatr owns that instance after creation.

## Security boundaries

EzyMediatr dispatches trusted, in-process objects. It is not an authentication, authorization, input-sanitization, or process-isolation boundary.

- Authorize each operation before sensitive handler work. FluentValidation verifies request shape and business rules; it does not establish caller identity or permission.
- Generated registration trusts loaded assemblies that contribute generator registrars. Runtime fallback trusts matching assemblies already loaded into the process. Use explicit assemblies when hosting third-party or otherwise untrusted plugins.
- Parameterize database commands, pass `ISqlUnitOfWork.Transaction` to every Dapper command in a transactional handler, and do not leak scoped services or unit-of-work objects into background work.
- Cancellation is cooperative. Handlers, processors, validators, and behaviors must observe the supplied token, and external calls still need their own timeouts and security controls.

## Build and pack

```bash
dotnet test EzyMediatr.sln
dotnet pack EzyMediatr.DependencyInjection/EzyMediatr.DependencyInjection.csproj \
  -c Release /p:ContinuousIntegrationBuild=true --output ./nupkgs
```

Update the version in both project files before publishing.

Run the allocation and CPU benchmarks after changing dispatch internals:

```bash
dotnet run -c Release --project EzyMediatr.Benchmarks/EzyMediatr.Benchmarks.csproj -- --filter '*'
```

## Performance

The steady-state request path avoids reflection, nested dependency-injection scopes, LINQ pipeline construction, response boxing, and behavior delegates when no custom behaviors are registered. Registration computes a compact feature mask for each request type, so dispatch performs one lookup instead of independently probing the container for every optional feature. Wrapper construction and reflection occur once per request/runtime response-type pair.

For this project, "zero-cost" means that an unused feature adds no managed allocation and as little dispatch work as the runtime API permits. It does not mean zero CPU instructions: `IMediator.Send` must still perform interface dispatch, find the runtime request wrapper, inspect the precomputed feature mask, and resolve the scoped handler. Completed request handlers, completed notification handlers, and completed pre/post-processor pipelines allocate no memory in the included benchmarks.

As an indicative baseline, the included process-isolated short-run benchmark on an Apple M4 Pro with .NET 11 Preview 4 and BenchmarkDotNet 0.16 Preview 1 measured:

| Synthetic operation | Mean | Allocated |
| --- | ---: | ---: |
| Direct completed request handler control | ~1.8 ns | 0 B |
| `Send` with a completed value-type handler | ~27.5 ns | 0 B |
| `Send` with one pass-through behavior | ~64 ns | 120 B |
| `Send` with one empty validator | ~137 ns | 672 B |
| `Send` with completed pre/post-processors | ~74.6 ns | 0 B |
| `Send` with an in-memory transaction | ~124 ns | 440 B |
| Direct completed notification handler control | ~1.2 ns | 0 B |
| `Publish` with one completed handler | ~22.6 ns | 0 B |
| `Publish` with one pass-through behavior | ~55 ns | 104 B |
| Direct one-item stream control | ~28 ns | 104 B |
| One-item `Stream` | ~93.5 ns | 304 B |
| One-item `Stream` with one pass-through behavior | ~187 ns | 608 B |

### MediatR comparison

The repository also contains a side-by-side benchmark against [MediatR 14.2.0](https://www.nuget.org/packages/MediatR). Both mediators execute the same message objects, handlers, completed tasks, notification work, and one-item async streams. The aligned MediatR cases replace its default transient handler registrations with scoped registrations to match EzyMediatr; the plain request benchmark also reports MediatR's documented default transient lifetime separately. Logging and containers are constructed once during benchmark setup, outside the measured operations.

On the same Apple M4 Pro and .NET 11 Preview 4 environment, the process-isolated short run measured:

| Synthetic operation | EzyMediatr | MediatR 14.2.0 | MediatR / EzyMediatr |
| --- | ---: | ---: | ---: |
| Completed `Send`, aligned scoped handler | 28.53 ns / 0 B | 39.12 ns / 104 B | 1.37x time |
| Completed `Send`, MediatR default transient handler | 28.53 ns / 0 B | 30.61 ns / 128 B | 1.07x time |
| `Send` with one pass-through behavior | 63.37 ns / 120 B | 84.91 ns / 288 B | 1.34x time, 2.40x allocation |
| `Send` with completed pre/post-processors | 71.92 ns / 0 B | 145.54 ns / 608 B | 2.02x time |
| `Publish` with one completed handler | 23.64 ns / 0 B | 134.18 ns / 712 B | 5.68x time |
| One-item stream | 101.78 ns / 304 B | 131.79 ns / 472 B | 1.29x time, 1.55x allocation |
| One-item stream with one pass-through behavior | 188.02 ns / 608 B | 231.79 ns / 904 B | 1.23x time, 1.49x allocation |

A validation run with three isolated process launches, five warmups, and ten measured iterations reproduced the representative results: completed `Send` measured 30.12 ns / 0 B for EzyMediatr, 39.47 ns / 104 B for an aligned scoped MediatR handler, and 32.40 ns / 128 B for MediatR's default transient handler; single-handler `Publish` measured 23.32 ns / 0 B and 136.08 ns / 712 B respectively. To isolate the source changes from the .NET upgrade, commit `66e1924` was also rebuilt for the same .NET 11 runtime and BenchmarkDotNet version: its `Send` path measured 79.43 ns / 120 B, compared with the current allocation-free path around 28–30 ns. Absolute timings vary between launches, but the allocation reductions and relative ordering reproduced.

Run only the comparison suite with:

```bash
dotnet run -c Release --project EzyMediatr.Benchmarks/EzyMediatr.Benchmarks.csproj -- \
  --filter '*MediatorComparisonBenchmarks*'
```

This is deliberately a dispatch-overhead comparison, not a claim about complete product equivalence:

- MediatR has a larger ecosystem and compatibility surface, including older target frameworks, a contracts-only package, request exception handlers/actions, configurable notification publishers, and generic handler support. See its [official documentation](https://github.com/LuckyPennySoftware/MediatR#readme).
- EzyMediatr targets .NET 11 and includes opt-in FluentValidation discovery plus transaction and unit-of-work integrations for EF Core and Dapper. MediatR has no built-in equivalent for those integrations, so fabricated user behaviors are not included in the benchmark.
- EzyMediatr includes notification pipeline behaviors. MediatR's built-in notification model has publisher strategies instead, so only plain single-handler publication is compared.
- EzyMediatr is MIT licensed. Before adopting MediatR 14.2, review its [current package license](https://github.com/LuckyPennySoftware/MediatR/blob/main/LICENSE.md) and commercial terms for your use case.
- These handlers complete synchronously and expose framework overhead intentionally. Real database, network, logging, and serialization costs will usually dominate these nanosecond differences.

The remaining optional-path allocations come from contracts that require runtime objects: a behavior needs a captured `next` delegate, FluentValidation builds validation contexts/results, async streams require iterator state, and transactional dispatch retains `AsyncLocal` state so nested sends safely join the active unit of work. EzyMediatr specializes the paths before creating closures or iterator state machines, so unconfigured features do not pay those costs. Synchronously completed processors reuse the handler's task; genuinely asynchronous processors allocate only when they suspend.

The direct controls separate handler and async-enumeration cost from mediator overhead. Optional-path figures include the costs of their underlying abstractions, such as FluentValidation, transaction propagation, and async enumeration. Results vary by runtime, hardware, dependency-injection container, and handler implementation, so run the benchmark in your own target environment rather than treating these figures as a service-level guarantee. For handlers that perform I/O, database and network work will dominate this dispatch overhead.

JIT disassembly on ARM64 shows that calls through `IMediator` use the runtime's interface-dispatch helper. Hand-written assembly would be architecture- and runtime-version-specific while leaving dependency-injection and runtime-type lookup costs intact, so it is not a maintainable optimization boundary. Moving materially closer to direct-call CPU cost would require an optional source-generated, typed dispatch backend that emits each known pipeline at compile time. That is a separate architecture and packaging feature; the current runtime dispatcher remains reflection-free after cache warm-up and allocation-free on its common completed-task paths.

Registration is startup-only. In the same benchmark, the generated zero-argument path took about 0.42 microseconds and allocated 3.33 KB. One explicitly scanned assembly took about 1.73 microseconds and allocated 6.39 KB; the runtime automatic-discovery fallback measured about 11.1 microseconds and 36.7 KB. The generated measurement covers `AddEzyMediatr()` itself; its one-time module-initializer delegate registration occurs earlier during assembly initialization. Explicit assembly registration remains available for plugin and trust-boundary control.

## License

MIT. See [LICENSE](LICENSE).
