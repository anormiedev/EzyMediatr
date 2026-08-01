# EzyMediatr code and architecture guide

This document is for maintainers and contributors. It explains the runtime model, performance strategy, transaction ownership, invariants, and required checks. The consumer contract is in [PUBLIC_API.md](PUBLIC_API.md).

## Design goals

EzyMediatr is built around four priorities:

1. Correct and explicit dispatch semantics.
2. A small implementation that can be understood locally.
3. No managed allocation from unused features on common completed-task paths.
4. Predictable extension points without hidden DI scopes or concurrency.

“Zero-cost abstraction” is a direction, not a literal promise. Runtime dispatch, DI resolution, interface calls, and async iteration have irreducible costs. Optimizations must be measurable, portable, and maintainable.

## Repository layout

| Project | Responsibility |
| --- | --- |
| `EzyMediatr.Core` | Contracts, runtime dispatch, pipelines, results, and unit-of-work implementations |
| `EzyMediatr.DependencyInjection` | Discovery, scanning, registration, and fluent configuration |
| `EzyMediatr.Tests` | Behavioral, registration, pipeline, transaction, cancellation, and allocation tests |
| `EzyMediatr.Benchmarks` | Dispatch, registration, stream, notification, and MediatR comparison benchmarks |

The `EzyMediatr.DependencyInjection` package project includes both its own assembly and `EzyMediatr.Core.dll` in the unified `EzyMediatr` package.

### Key implementation files

| File | Role |
| --- | --- |
| [`Mediator.cs`](../EzyMediatr.Core/Mediator.cs) | Public dispatch entry points and wrapper selection |
| [`RequestHandlerWrapper.cs`](../EzyMediatr.Core/Internal/RequestHandlerWrapper.cs) | Request pipeline specialization and completion fast paths |
| [`StreamRequestHandlerWrapper.cs`](../EzyMediatr.Core/Internal/StreamRequestHandlerWrapper.cs) | Stream enumeration and stream behavior composition |
| [`NotificationHandlerWrapper.cs`](../EzyMediatr.Core/Internal/NotificationHandlerWrapper.cs) | Notification behavior and sequential handler execution |
| [`EzyMediatrOptions.cs`](../EzyMediatr.Core/EzyMediatrOptions.cs) | Configuration, feature masks, and registration registry |
| [`TransactionBehavior.cs`](../EzyMediatr.Core/Pipeline/TransactionBehavior.cs) | Transaction selection, ownership, and nesting |
| [`DapperUnitOfWork.cs`](../EzyMediatr.Core/Transactions/DapperUnitOfWork.cs) | Connection and SQL transaction lifecycle |
| [`EfCoreUnitOfWork.cs`](../EzyMediatr.Core/Transactions/EfCoreUnitOfWork.cs) | EF Core transaction and save lifecycle |
| [`ServiceCollectionExtensions.cs`](../EzyMediatr.DependencyInjection/ServiceCollectionExtensions.cs) | Discovery, scanning, lifetimes, and builder configuration |

## Registration

`AddEzyMediatr` constructs `EzyMediatrBuilder`, which:

1. Uses explicit assemblies or discovers already-loaded, non-dynamic assemblies that directly reference `EzyMediatr.Core`.
2. Registers singleton `EzyMediatrOptions` and scoped `IMediator`/`Mediator`.
3. Scans concrete, closed implementation types for handler and pipeline interfaces.
4. Rejects duplicate request or stream handler services.
5. Registers FluentValidation validators from the selected assemblies.
6. Attaches a lazy `ServiceRegistrationRegistry` to the options.
7. Registers transaction accessors when a unit-of-work provider is configured.

Handler uniqueness includes matching registrations already in `IServiceCollection`. Notifications, processors, and behaviors allow multiple implementations and preserve registration order.

Open generic implementation types are not assembly-scanned. Consumers can register them explicitly; the feature registry recognizes open generic service registrations.

### Feature registry

`ServiceRegistrationRegistry` compacts optional registrations into a byte-sized `PipelineFeatures` mask:

```text
Validator | RequestBehavior | StreamBehavior |
NotificationBehavior | PreProcessor | PostProcessor
```

Closed-service features are stored by request type; open generic registrations contribute a shared mask. Dispatch performs one dictionary lookup and bitwise OR, then avoids resolving absent optional services.

The registry is lazy so services added after `AddEzyMediatr`, but before provider creation and first dispatch, remain visible. After evaluation it is a snapshot. Mutating a service collection after its provider is built is unsupported.

A directly constructed `Mediator` without registered options conservatively reports every feature as possible. That fallback favors correctness; normal DI registration supplies the optimized feature map.

## Dispatch architecture

`Mediator` contains no async state machine. It rejects null messages, selects a typed wrapper, and returns the wrapper's `Task` or `IAsyncEnumerable` directly.

### Requests

`RequestHandlerWrapperFactory<TResponse>` caches wrappers in a `ConcurrentDictionary<Type, RequestHandlerWrapper<TResponse>>`.

- First dispatch constructs `RequestHandlerWrapper<TRequest, TResponse>` using reflection.
- Later dispatches reuse that wrapper.
- The wrapper casts once, reads the feature mask, and stays in generic code.

The feature-free path is:

```text
IMediator.Send
  -> cached typed wrapper
    -> feature lookup
      -> resolve IRequestHandler<TRequest, TResponse>
        -> handler.Handle
```

It does not build behavior delegates, enumerate optional services, box the response, or wrap the handler task.

The full built-in order is:

```text
custom behaviors, first registered outermost
  -> validation
    -> transaction, if required
      -> pre-processors
        -> handler
      -> post-processors
```

Validation and transactions use internal static execution paths instead of container-resolved behaviors. This fixes their order and keeps their disabled cost small. Their concrete behavior types remain public for advanced composition, but registering them again duplicates the stage.

### Processor completion

Processors return `Task`, but often finish synchronously. The wrapper checks `IsCompletedSuccessfully` at each step:

- A fully synchronous pre/handler/post path reuses the handler task.
- An async helper is entered only once a stage actually suspends.
- Synchronous exceptions and cancellations become faulted or cancelled tasks, preserving the task-returning API.

Do not replace this with one unconditional async method or LINQ composition without proving that allocations and latency do not regress.

### Streams

`StreamRequestHandlerWrapperFactory<TResponse>` uses the same cache-per-runtime-request-type design. Stream execution is necessarily an async iterator and therefore allocates iterator state.

The wrapper selects one of three paths before enumeration:

1. Handler only.
2. Pre-processors then handler.
3. Stream behaviors around either terminal path.

Pre-processors run once when enumeration starts. Each layer propagates cancellation with `WithCancellation`. Cleanup belongs in a stream behavior's `finally` because enumerator disposal also occurs after failure, cancellation, or early termination.

Do not add implicit request post-processors or transaction wrapping to streams. A stream can be unbounded or abandoned, so its consumer must not control an invisible transaction lifetime.

### Notifications

`Publish<TNotification>` uses `NotificationHandlerWrapperCache<TNotification>.Instance` when compile-time and runtime notification types match. A derived runtime type uses `NotificationHandlerWrapperFactory` and its concurrent cache.

The wrapper skips behavior resolution when none are registered, has direct zero- and one-handler paths, and awaits multiple handlers sequentially. Sequential execution gives deterministic ordering and failure semantics without task scheduling or coordination allocations.

Parallel, durable, or retrying publication would require a distinct public contract.

## Behavior chains

Request, stream, and notification behavior arrays are folded from last to first, so the first registered behavior is outermost. A dedicated one-behavior path avoids the general folding loop.

Every behavior receives a `next` delegate; closures on behavior-enabled paths are part of that contract. Never move those allocations onto the feature-free path.

Microsoft DI commonly materializes `IEnumerable<T>` registrations as arrays. The runtime retains arrays when available and copies other enumerables for container compatibility. Correctness cannot depend on a specific container returning an array.

## Validation

`ValidationBehavior.Validate` returns `ValueTask`:

- Disabled validation or no validator feature returns `ValueTask.CompletedTask`.
- Validators run sequentially against one `ValidationContext<TRequest>`.
- All failures are collected into one `ValidationException`.
- Validation completes before transaction creation.

Sequential execution avoids assuming validators or scoped dependencies are thread-safe. Changes must preserve error aggregation and the no-validator fast path.

## Transactions

A transaction is required when `WrapEveryRequest()` enabled `WrapAllRequests`, or the concrete request implements `ITransactionalRequest`. Selection happens after validation and before processors.

### Ownership and nesting

`UnitOfWorkAccessor` is scoped and stores the current unit of work in `AsyncLocal<IUnitOfWork?>`. An outer transaction:

1. Creates one unit of work from the configured factory.
2. Pushes it into the current async flow.
3. Calls `IUnitOfWork.ExecuteAsync` around processors, handler, and post-processors.
4. Restores the previous value in `finally`.
5. Disposes the created unit of work with `await using`.

When a unit of work is already active in the same async flow, a nested transactional `Send` runs inside it and does not create, commit, or dispose another transaction.

`AsyncLocal` carries the active unit of work with the execution context across nested asynchronous calls. Child tasks can inherit that same value, so it neither isolates concurrent children nor makes a connection or context thread-safe. Database work inside one transaction should remain sequential.

### Dapper path

`DapperUnitOfWork` owns the connection returned by the application factory. It opens the connection if required, starts a transaction, executes the pipeline, and commits. On operation or commit failure it attempts rollback with `CancellationToken.None` and preserves the original exception if rollback also fails. It then disposes transaction and connection, preferring async `DbConnection`/`DbTransaction` APIs.

`ActiveSqlUnitOfWork` is the scoped facade injected as `ISqlUnitOfWork`. Every property access resolves the transaction currently stored in `UnitOfWorkAccessor`; access outside a transaction throws. This prevents one scoped facade from capturing a transaction across sequential sends.

The runtime cannot force Dapper callers to pass `ISqlUnitOfWork.Transaction`. Tests, samples, and documentation must always demonstrate it.

### EF Core path

Builder configuration resolves `TContext` from the current scope and creates `EfCoreUnitOfWork<TContext>` with `ownsContext: false`. Execution begins an EF transaction, runs the inner pipeline, calls `SaveChangesAsync`, and commits. Failure triggers best-effort rollback with a non-cancelled token, preserving the original exception.

The standalone `EfCoreUnitOfWorkFactory<TContext>` supports an `IDbContextFactory<TContext>` that creates owned contexts, or a resolver whose context remains externally owned. The fluent builder uses scoped-context resolution rather than this public factory.

### Provider configuration

`EzyMediatrOptions` stores one unit-of-work factory delegate. A later `UseDapper`, `UseEfCore`, or `UseUnitOfWorkFactory` call replaces the earlier provider; the final configuration wins. Use request-aware factory logic when routing is required instead of stacking providers.

Factories must create a new, non-null unit of work for each top-level transaction. EzyMediatr owns and disposes it.

## Observable invariants

- Null messages fail before service resolution.
- Missing handlers fail through required-service resolution.
- Duplicate request and stream handlers fail during registration.
- Validation failures occur before transaction creation.
- Processor, handler, or post-processor failure prevents commit.
- Rollback failure is suppressed only to preserve the original operation or commit error.
- Notification handlers stop after the first failure.
- Stream cleanup depends on correct async-enumerator disposal.
- Cancellation is propagated but remains cooperative.

Do not swallow application exceptions, silently retry, aggregate unrelated errors, or translate cancellation into success.

## Caches and lifetimes

Wrapper caches are static and keyed by runtime message type within response-specific generic caches. They intentionally live for the process. This suits normal application message types, but an unbounded sequence of collectible plugin types could be retained. Unloadable-plugin support would need weak caches and dedicated benchmarks.

Options and the feature registry are singleton state and should not be mutated after dispatch begins. Handlers and extension points are scoped. Never retain requests, responses, scoped services, providers, database resources, enumerators, or cancellation sources in static state.

## Security boundaries

EzyMediatr handles trusted in-process objects. It is not authorization, identity validation, sandboxing, serialization, retry, or durable delivery.

Maintain these rules in code and examples:

- Prefer explicit assemblies when third-party plugins can load.
- Authorize before sensitive effects.
- Parameterize SQL and pass the active transaction to every transactional Dapper command.
- Do not let scoped resources escape into background work.
- Give external operations provider-appropriate timeouts.
- Never include secrets or connection strings in exceptions.

## Performance model

Preserve correctness first and prove optimizations with benchmarks. Separate three kinds of work.

### Startup

Assembly enumeration, reflection scanning, validator discovery, and service registration are startup costs. Explicit assemblies reduce discovery work and clarify the trust boundary.

### First dispatch

The first concrete request/response pair or derived notification constructs a closed wrapper using reflection and stores it in a concurrent cache. Reflection must not recur in steady state.

### Steady state

Feature-free completed-task paths should preserve:

- no reflection;
- no nested DI scope;
- no LINQ pipeline construction;
- no response boxing;
- no optional-service enumeration;
- no mediator async state machine;
- no mediator-attributable allocation for `Send` or single-handler `Publish`.

Streams allocate iterator state. Behaviors allocate `next` delegates. FluentValidation creates validation objects. Transactions require async ownership and `AsyncLocal`. Reduce these costs only without weakening their contracts.

Nanosecond results vary by runtime and hardware. Compare distributions and allocations across isolated processes, keep controls semantically aligned, and never treat a short run as a universal guarantee.

## Change checklist

- Does behavior order, handler cardinality, cancellation, or failure propagation change?
- Does an unused feature now resolve services, enumerate collections, build delegates, or allocate state?
- Does first-dispatch reflection remain cached?
- Are both synchronous and genuinely asynchronous completion paths tested?
- Does stream cleanup still run on early disposal and cancellation?
- Do nested transactions join only within the current async flow?
- Is ownership and disposal unambiguous on every exception path?
- Does a new extension point require a feature bit and registry recognition?
- Could a cache retain scoped data or collectible types?
- Are public docs and examples still accurate?

## Verification

Run correctness checks from the repository root:

```bash
dotnet build EzyMediatr.sln --no-restore --warnaserror
dotnet test EzyMediatr.sln --no-build
git diff --check
```

Run all benchmarks after changing dispatch, registration, validation, transaction, notification, or stream internals:

```bash
dotnet run -c Release \
  --project EzyMediatr.Benchmarks/EzyMediatr.Benchmarks.csproj -- \
  --filter '*'
```

Run only the comparison suite with:

```bash
dotnet run -c Release \
  --project EzyMediatr.Benchmarks/EzyMediatr.Benchmarks.csproj -- \
  --filter '*MediatorComparisonBenchmarks*'
```

Use disassembly to investigate an unexplained regression, but prefer clear C# that remains stable across runtime versions. A source-generated typed dispatcher is the plausible future boundary for removing runtime lookup; handwritten architecture-specific assembly is not.

## Documentation ownership

- [PUBLIC_API.md](PUBLIC_API.md) owns consumer behavior and examples.
- This file owns implementation strategy, invariants, and contributor guidance.
- [README.md](../README.md) is the concise overview, quick start, packaging guide, and published benchmark summary.
- Public API changes must update the public guide and README in the same change.
- Runtime changes must update this guide when an invariant, ownership rule, stage, or performance assumption changes.
