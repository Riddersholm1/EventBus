# EventBus

[![Build](https://github.com/Riddersholm1/EventBus/actions/workflows/pipeline.yml/badge.svg)](https://github.com/Riddersholm1/EventBus/actions)
[![Coverage](https://codecov.io/gh/Riddersholm1/EventBus/branch/main/graph/badge.svg)](https://codecov.io/gh/Riddersholm1/EventBus)
[![Latest Release](https://img.shields.io/github/v/release/Riddersholm1/EventBus?include_prereleases)](https://github.com/Riddersholm1/EventBus/releases)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Riddersholm.EventBus)](https://www.nuget.org/packages/Riddersholm.EventBus)

A lightweight, **in-process** event aggregator for .NET — loosely coupled
publish/subscribe messaging between components without them having to know about
each other.

- Configurable DI lifetime — **scoped** (per Blazor circuit / web request) or **singleton** (app-wide)
- Synchronous **and** asynchronous handlers (single async-first publish API)
- Thread-safe under concurrent publish/subscribe
- Deterministic disposal — dispose a subscription, stop receiving events
- Zero runtime dependencies beyond `Microsoft.Extensions.DependencyInjection.Abstractions`
- AOT- and trim-compatible
- .NET 10
- MIT licensed

Works in **Blazor, .NET MAUI, WPF/WinForms, ASP.NET Core, worker services, and
console apps** — anywhere with `Microsoft.Extensions.DependencyInjection`.

## Installation

```bash
dotnet add package Riddersholm.EventBus
```

## Register the bus

Choose the lifetime that matches the boundary you want events to stay inside:

```csharp
// Program.cs
using EventBus;

// Blazor Server / per-request web apps — one isolated bus per scope:
builder.Services.AddEventBus(ServiceLifetime.Scoped);

// Desktop (MAUI/WPF), worker services, or app-wide messaging — one shared bus:
builder.Services.AddEventBus(ServiceLifetime.Singleton);
```

| Host | Lifetime | Why |
| ---- | -------- | --- |
| **Blazor Server** | `Scoped` | A scope = one circuit = one user. Keeps events isolated per user. Singleton here leaks events between users. |
| **Blazor WebAssembly** | Either | One scope per app; scoped and singleton behave identically. |
| **ASP.NET Core** | `Singleton` (usually) | Scoped = per request. Use singleton for cross-request / background-service messaging. |
| **MAUI / WPF / WinForms** | `Singleton` | One user, app-lifetime view models, no natural scope. |
| **Worker / Console** | `Singleton` | Hosted services are singletons; no scope to attach to. |

`IEventBus` is now resolvable wherever you inject it.

Register the bus **once**. `AddEventBus` is additive-once: a second call is
ignored, *including its lifetime*, so calling it with `Scoped` and later with
`Singleton` silently leaves the bus scoped.

## Define an event

Events are plain types — the recommended form is a `sealed record` so they're
immutable, value-compared, and cheap to create:

```csharp
public sealed record CounterIncremented(int NewValue);
public sealed record UserLoggedIn(string UserId, DateTimeOffset At);
```

## Publish

Publishing is always asynchronous.

```csharp
private readonly IEventBus _bus;
private int _count;

private async Task IncrementAsync()
{
    _count++;
    await _bus.PublishAsync(new CounterIncremented(_count));
}
```

## Subscribe

Subscriptions are `IDisposable`. Keep the token and dispose it when your
subscriber is torn down.

There are three handler shapes; the compiler picks the right one from your lambda:

```csharp
// Synchronous
bus.Subscribe<CounterIncremented>(e => _value = e.NewValue);

// Asynchronous
bus.Subscribe<UserLoggedIn>(async e => await _audit.RecordAsync(e));

// Asynchronous, with the publisher's cancellation token
bus.Subscribe<UserLoggedIn>(async (e, ct) => await _audit.RecordAsync(e, ct));
```

### In a plain service (constructor injection)

```csharp
public sealed class AuditService : IDisposable
{
    private readonly IDisposable _subscription;

    public AuditService(IEventBus bus)
        => _subscription = bus.Subscribe<UserLoggedIn>(OnUserLoggedIn);

    private void OnUserLoggedIn(UserLoggedIn e) { /* ... */ }

    public void Dispose() => _subscription.Dispose();
}
```

### In a Blazor component

```csharp
@implements IDisposable
@inject IEventBus EventBus

private IDisposable? _subscription;
private int _value;

protected override void OnInitialized()
{
    _subscription = EventBus.Subscribe<CounterIncremented>(OnCounterIncremented);
}

private void OnCounterIncremented(CounterIncremented e)
{
    _value = e.NewValue;
    InvokeAsync(StateHasChanged);
}

public void Dispose() => _subscription?.Dispose();
```

## Publish semantics

`PublishAsync` is the single publish entry point.

| Handler kind | Behavior |
| ------------ | -------- |
| `Action<TEvent>` | Invoked inline on the publishing thread |
| `Func<TEvent, Task>` | Awaited before the next handler runs |
| `Func<TEvent, CancellationToken, Task>` | Awaited before the next handler runs, receives the publisher's token |

Handlers run **sequentially**, in subscription order, for deterministic
ordering. If a handler throws, every other handler still runs; the exceptions are
aggregated into an `AggregateException`. A handler that throws stays subscribed.

**Routing is on the compile-time type.** `PublishAsync` dispatches on
`TEvent`, not on `eventData.GetType()`. Publishing through a base-typed variable
reaches only subscribers of that base type:

```csharp
BaseEvent e = new DerivedEvent(...);
await bus.PublishAsync(e);              // TEvent is BaseEvent — DerivedEvent subscribers do NOT fire
await bus.PublishAsync(new DerivedEvent(...));  // TEvent is DerivedEvent — these do
```

Handlers run on the publishing thread's context, so a handler that publishes its
own event type recurses and will exhaust the stack.

## Disposal & lifetime

- **Subscription**: `IDisposable`. Dispose it to stop receiving events. Safe
  to dispose multiple times.
- **Bus**: disposed automatically by the DI container when its scope ends
  (scoped) or when the application shuts down (singleton). After disposal,
  `Subscribe` and `PublishAsync` throw `ObjectDisposedException`. A publish
  already in flight finishes against the snapshot it took.

**Always dispose your subscriptions** — otherwise the bus holds a strong
reference to the subscriber for the lifetime of the bus. With a singleton bus
that means the lifetime of the application, so disposal discipline matters more.

## Concurrency

The bus is safe for concurrent use:

- Subscriptions are stored in a `ConcurrentDictionary<Type, ImmutableList<…>>`.
- `PublishAsync` takes a lock-free snapshot before iterating, so handlers that
  subscribe/unsubscribe during publication don't corrupt the iteration.
- Subscribers added *during* a publish will see subsequent events but may not
  see the publish already in flight — the usual event-aggregator guarantee.

## Cancellation

`PublishAsync` accepts an optional `CancellationToken`:

```csharp
await bus.PublishAsync(new UserLoggedIn(id, DateTimeOffset.UtcNow), ct);
```

The token is checked before each handler and flowed to every handler that asks
for one. When it is signalled, `PublishAsync` throws `OperationCanceledException`
and the remaining handlers do not run.

If handlers had already thrown before the cancellation was observed, those
exceptions are **not** discarded — they are attached as an inner
`AggregateException`:

```csharp
catch (OperationCanceledException ex) when (ex.InnerException is AggregateException failures)
{
    // handlers that failed before the publish was cancelled
}
```

## Why is publish async-only?

There is a single async-first publish API and no synchronous `Publish`. A sync
publish would have to block on (or fire-and-forget) any registered async
handler — the classic sync-over-async footgun that risks deadlocks and swallows
exceptions. `PublishAsync` invokes both sync and async handlers correctly, and
when every handler is synchronous it completes synchronously, without a thread
hop (the returned task is already completed when it comes back).

## FAQ

**Which lifetime should I use?**
See the table under [Register the bus](#register-the-bus). Rule of thumb:
multi-user server-side (Blazor Server) → `Scoped` for isolation; single-user or
app-wide broadcast (desktop, workers, cross-request) → `Singleton`.

**Does it support `ValueTask`?**
Not currently. Handlers return `Task`. If you have a hot-path use case,
open an issue.

**Does it support weak references to avoid leaks from forgotten subscribers?**
No, it uses strong references — matching the standard event-aggregator
pattern. Always dispose your subscriptions.

**Can I subscribe to a base type and receive derived events?**
No, subscription is type-exact. Subscribing to `BaseEvent` does not pick up
`DerivedEvent : BaseEvent`. This keeps routing fast and the semantics obvious.
See [Publish semantics](#publish-semantics) for the publishing side of the same rule.

## Upgrading to 1.1.0

1.1.0 adds a `Subscribe<TEvent>(Func<TEvent, Task>)` overload. The addition is
binary-compatible, but it changes which overload some source binds to when you
**recompile**:

- `Subscribe<T>(async e => …)` previously bound to `Action<T>`, making it an
  `async void` handler — never awaited, and its exceptions unobservable (in
  Blazor Server, enough to tear down the circuit). It now binds to
  `Func<T, Task>` and is awaited properly. This is the point of the change.
- `Subscribe<T>(e => SomeTaskReturningMethod(e))` moves the same way, from
  fire-and-forget to awaited.
- Statement lambdas that return nothing (`e => { _value = e.N; }`) and the
  two-parameter form (`async (e, ct) => …`) are unaffected.

If you were relying on a handler *not* being awaited, start it explicitly inside
a synchronous handler instead.

## License

[MIT](LICENSE) © 2026 Jesper Bruhn Riddersholm
