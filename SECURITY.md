# Security policy

## Supported versions

| Version | Supported |
|---|---|
| 1.1.x | ✅ |
| 1.0.x | ❌ — upgrade to 1.1, which is binary-compatible; see [Upgrading to 1.1.0](README.md#upgrading-to-110) |

Fixes ship as a patch release of the latest version.

## Reporting a vulnerability

Please report security issues privately through
[GitHub's private vulnerability reporting](https://github.com/Riddersholm1/EventBus/security/advisories/new)
rather than by opening a public issue.

You can expect an acknowledgement within a few days and an assessment shortly after. If the report is
accepted, you will be credited in the advisory unless you would rather not be.

## What counts as a vulnerability here

EventBus is an in-process library with no network, file, or process access of its own. What it does
is decide which code receives which event — in Blazor Server, which *user's* code — so reports in these
areas are especially welcome:

- **Events crossing a scope.** Registered as `ServiceLifetime.Scoped`, each DI scope gets its own bus,
  which in Blazor Server means one per user's circuit. Any way for an event published in one scope to
  reach a subscriber in another discloses one user's data to another.
- **Delivery after disposal.** A handler invoked by a publish that *started* after its subscription,
  or the bus, was disposed.
- **Lost failures.** A handler exception that is swallowed rather than surfaced in the
  `AggregateException` — or, for a cancelled publish, in the inner `AggregateException`. A failure
  nobody sees is a failed audit write nobody sees.
- **Concurrency defects.** Concurrent `Subscribe`, `PublishAsync` and `Dispose` calls that lose a
  subscription, deliver one event twice, or leave a subscription alive after the bus is disposed.
- **Retention.** The bus keeping a handler reachable after its subscription was disposed, so a
  long-lived bus grows without limit.

## What does not count

These are documented behaviours, not vulnerabilities:

- Events reaching other users in Blazor Server when the bus is registered as `Singleton`. That is what
  a singleton is; register it as `Scoped`.
- Subscribers kept alive because their subscription was never disposed. The bus holds strong
  references by design; dispose your subscriptions.
- A subscription disposed while a publish is already running receiving that one in-flight event.
  A publish iterates the snapshot it took when it started.
- A stack overflow from a handler that publishes its own event type.
- A publish through a base-typed variable reaching only subscribers of the base type. Routing is on
  the compile-time type.
- A second `AddEventBus` call being ignored, including its lifetime.
