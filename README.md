# EzyMediatr

Simple mediator with validation and optional unit-of-work support. Ships as one NuGet (`EzyMediatr`) that contains the core runtime and DI extensions. Costs $0, so you can skip the $150–$400 seats elsewhere.

What you get:
- Request/response, streams, and notifications
- Drop-in DI registration that scans your assemblies automatically
- Optional validation via FluentValidation
- Transactions for Dapper or EF Core (per-request or wrap-all)

## Quick start
1) Install:
```bash
dotnet add package EzyMediatr
```

2) Register (Dapper example):
```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using EzyMediatr.Core.Abstractions;
using EzyMediatr.DependencyInjection;

var services = new ServiceCollection();
services
    .AddEzyMediatr() // scans current AppDomain for handlers/validators
    .UseDapper(_ => new SqlConnection("Server=.;Database=app;Trusted_Connection=True;"));
    // .WrapEveryRequest(); // turn on to wrap all requests in one transaction

var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
```

3) Create a request/handler:
```csharp
public sealed record CreateOrder(string Customer, decimal Amount)
    : IRequest<Guid>, ITransactionalRequest; // mark transactional to enlist in UoW

public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, Guid>
{
    public Task<Guid> Handle(CreateOrder request, CancellationToken ct)
        => Task.FromResult(Guid.NewGuid());
}
```

4) Add validation (optional):
```csharp
using FluentValidation;

public sealed class CreateOrderValidator : AbstractValidator<CreateOrder>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.Customer).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
    }
}
```

5) Use it:
```csharp
var id = await mediator.Send(new CreateOrder("alice", 10m));

await foreach (var forecast in mediator.Stream(new GetWeatherStreamQuery(5)))
{
    Console.WriteLine(forecast);
}

await mediator.Publish(new OrderCreated(id));
```

## Using with Dapper (step by step)
1) Provide a connection factory:
```csharp
services.AddEzyMediatr()
    .UseDapper((request, sp) => new SqlConnection("Server=.;Database=app;Trusted_Connection=True;"));
```
2) Mark any request that needs a transaction with `ITransactionalRequest`.
3) Want everything transactional? Call `.WrapEveryRequest()`.
4) Need multiple databases? Return different connections based on the `request`:
```csharp
services.AddEzyMediatr()
    .UseDapper((request, sp) => request switch
    {
        CreateOrder => new SqlConnection(orderConn),   // write connection
        _ => new SqlConnection(defaultConn)            // default connection
    });
```

## Using with EF Core (step by step)
1) Register your context factory:
```csharp
using Microsoft.EntityFrameworkCore;

services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer("Server=.;Database=app;Trusted_Connection=True;"));
```
2) Plug EzyMediatr into EF Core:
```csharp
services.AddEzyMediatr()
    .UseEfCore<AppDbContext>();
    // .WrapEveryRequest(); // make every request transactional if you like
```
3) Transactions are handled for requests that implement `ITransactionalRequest` (or all of them if you wrapped everything).

## Notifications (publish/subscribe)
- Define a notification:
```csharp
public sealed record OrderCreated(Guid OrderId) : INotification;
```
- Handle it (you can have multiple handlers for the same notification):
```csharp
public sealed class OrderCreatedEmailHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken ct)
    {
        // send email...
        return Task.CompletedTask;
    }
}

public sealed class OrderCreatedMetricsHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken ct)
    {
        // record metrics...
        return Task.CompletedTask;
    }
}
```
- Publish:
```csharp
await mediator.Publish(new OrderCreated(orderId));
```
- Handlers are discovered automatically during `AddEzyMediatr()` assembly scanning.

Quick example (all together):
```csharp
public sealed record OrderCreated(Guid OrderId) : INotification;

public sealed class OrderCreatedEmailHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken ct)
    {
        Console.WriteLine($"Email sent for order {notification.OrderId}");
        return Task.CompletedTask;
    }
}

// Somewhere in your code:
await mediator.Publish(new OrderCreated(orderId));
```
- There is no manual "subscribe" step: implementing `INotificationHandler<T>` is the subscription. When you call `Publish`, every registered handler runs.

## Key interfaces (what to implement)
- `IRequest<TResponse>`: regular request/response.
- `IStreamRequest<TResponse>`: streaming responses (consumed via `mediator.Stream()`).
- `INotification`: fire-and-forget notifications (consumed via `mediator.Publish()`).
- `IRequestHandler<TRequest,TResponse>`: handles `IRequest<TResponse>`.
- `IStreamRequestHandler<TRequest,TResponse>`: handles `IStreamRequest<TResponse>`.
- `INotificationHandler<TNotification>`: handles notifications.
- `ITransactionalRequest`: marker to run the handler inside a unit of work/transaction (Dapper or EF Core). If you call `.WrapEveryRequest()`, you don’t need the marker—everything is transactional.

## Notes and extensibility
- If you omit assemblies in `AddEzyMediatr()`, the current AppDomain is scanned.
- Add logging/telemetry by implementing `IPipelineBehavior<TRequest,TResponse>`,
  `IRequestPreProcessor<TRequest>`, or `IRequestPostProcessor<TRequest,TResponse>`.

## Build / Publish (for maintainers)
- Requires the .NET 10 SDK (net10.0 target).
- Build: `dotnet build EzyMediatr.sln`
- Pack: `dotnet pack EzyMediatr.DependencyInjection/EzyMediatr.DependencyInjection.csproj -c Release /p:ContinuousIntegrationBuild=true --output ./nupkgs`
- Publish: `dotnet nuget push ./nupkgs/*.nupkg -k $NUGET_API_KEY -s https://api.nuget.org/v3/index.json --skip-duplicate`
- Bump `<Version>` in `EzyMediatr.DependencyInjection.csproj` before packing (keep `EzyMediatr.Core.csproj` in sync for assembly metadata).

## License
MIT License. See `LICENSE`.
