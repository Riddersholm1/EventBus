# EventBus

[![Build](https://github.com/Riddersholm1/EventBus/actions/workflows/pipeline.yml/badge.svg)](https://github.com/Riddersholm1/EventBus/actions)
[![Coverage](https://codecov.io/gh/Riddersholm1/EventBus/branch/main/graph/badge.svg)](https://codecov.io/gh/Riddersholm1/EventBus)
[![Latest Release](https://img.shields.io/github/v/release/Riddersholm1/EventBus?include_prereleases)](https://github.com/Riddersholm1/EventBus/releases)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Riddersholm.EventBus)](https://www.nuget.org/packages/Riddersholm.EventBus)
[![Stars](https://img.shields.io/github/stars/Riddersholm1/EventBus)](https://github.com/Riddersholm1/EventBus/stargazers)
[![Contributors](https://img.shields.io/github/contributors/Riddersholm1/EventBus)](https://github.com/Riddersholm1/EventBus/graphs/contributors)
[![Last Commit](https://img.shields.io/github/last-commit/Riddersholm1/EventBus)](https://github.com/Riddersholm1/EventBus/commits/main)
[![Commit Activity](https://img.shields.io/github/commit-activity/m/Riddersholm1/EventBus)](https://github.com/Riddersholm1/EventBus/graphs/commit-activity)
[![Issues](https://img.shields.io/github/issues/Riddersholm1/EventBus)](https://github.com/Riddersholm1/EventBus/issues)
[![Release Strategy](https://img.shields.io/badge/release%20strategy-githubflow-orange)](https://githubflow.github.io)

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

## Define an event

Events are plain types — the recommended form is a `sealed record` so they're
immutable, value-compared, and cheap to create:

```csharp
public sealed record CounterIncremented(int NewValue);
public sealed record UserLoggedIn(string UserId, DateTimeOffset At);
```

## Publish

Publishing is always asynchronous. `PublishAsync` invokes both sync and async
handlers — sync handlers execute inline, async handlers are awaited one after
another in subscription order.

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

### In a Blazor component (sync handler)

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

### Async handler

```csharp
_subscription = EventBus.Subscribe<UserLoggedIn>(async (e, ct) =>
{
    await _audit.RecordAsync(e, ct);
    // In Blazor: await InvokeAsync(StateHasChanged);
});
```

The cancellation token comes from the publisher's `PublishAsync` call.

## Publish semantics

`PublishAsync` is the single publish entry point.

| Handler kind | Behavior                                    |
| ------------ | ------------------------------------------- |
| Sync         | Invoked inline on the publishing thread     |
| Async        | Awaited sequentially, in subscription order |

Handlers run **sequentially** within a single publish for deterministic
ordering. If a handler throws, every other handler still runs; the exceptions
are aggregated into an `AggregateException`.

## Disposal & lifetime

- **Subscription**: `IDisposable`. Dispose it to stop receiving events. Safe
  to dispose multiple times.
- **Bus**: disposed automatically by the DI container when its scope ends
  (scoped) or when the application shuts down (singleton). After disposal,
  `Subscribe` and `PublishAsync` throw `ObjectDisposedException`.

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

The token is checked before each handler and flowed to every async handler.

## Why is publish async-only?

There is a single async-first publish API and no synchronous `Publish`. A sync
publish would have to block on (or fire-and-forget) any registered async
handler — the classic sync-over-async footgun that risks deadlocks and swallows
exceptions. `PublishAsync` invokes both sync and async handlers correctly, and
when every handler is synchronous it completes synchronously with no thread hop,
so the async overhead is effectively zero.

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

## License

[MIT](LICENSE) © 2026 Jesper Bruhn Riddersholm