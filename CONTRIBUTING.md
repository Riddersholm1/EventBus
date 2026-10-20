# Contributing

Thank you for considering a contribution. EventBus carries messages between parts of an application
— in Blazor Server, between the components of one user's circuit — so a bug here can drop an event,
deliver it twice, or deliver it to the wrong user. The bar for correctness is set accordingly.

## Getting set up

You need the **.NET 10 SDK**.

```sh
dotnet build -warnaserror                    # what CI runs; zero warnings expected
dotnet test
dotnet run --project test/EventBus.TestApp   # Blazor Server demo, for checking behaviour by hand
```

The library project sets `AnalysisLevel` to `latest-recommended` and turns on the trimming and NativeAOT
analyzers (`IsAotCompatible`), because the README promises both. CI builds with `-warnaserror`, so
build the same way locally: a warning is a build failure, not a suggestion.

## What a good change looks like

**Every behavioural change needs a test that fails before it.** Not a test that passes afterwards —
one that demonstrably fails first. A test that asserts the wrong behaviour is worse than no test, and
writing the test first is the only reliable defence.

**Keep the guarantees the README makes.** In particular:

- Handlers run *sequentially, in subscription order*. A handler that throws does not stop the others,
  and every exception is surfaced — never swallowed, including when the publish is cancelled.
- Subscribing, publishing and disposing are safe to call concurrently, and a publish iterates a
  snapshot. A change to that code needs a test that exercises the race, and a comment explaining the
  memory ordering it relies on, as `AddSubscription` and `ThrowIfDisposed` have.
- When every handler is synchronous, `PublishAsync` completes synchronously.
- No reflection-based dispatch: the library must stay trimming- and NativeAOT-safe.

**Explain the "why" in comments, not the "what".** The code says what it does. Comments should say why
it does it that way, and especially why an obvious-looking alternative is wrong.

## Things that will be pushed back on

The README's FAQ and *Why is publish async-only?* record the reasoning behind most of these:

- A synchronous `Publish`. It would have to block on, or fire and forget, any async handler.
- Routing to base-type subscribers, or weak references to subscribers.
- Running handlers in parallel. It gives up the deterministic ordering.
- Catching and logging handler exceptions instead of surfacing them to the publisher.
- Adding a dependency beyond `Microsoft.Extensions.DependencyInjection.Abstractions`.

If your change contradicts one of these, that is fine — but argue the decision in the issue or pull
request; do not route around it.

## Public API and versioning

EventBus follows semantic versioning. Two things check it:

- **Package validation.** `EnablePackageValidation` and `PackageValidationBaselineVersion` in
  [`src/EventBus/EventBus.csproj`](src/EventBus/EventBus.csproj) make every `dotnet pack` download that
  published release (today `1.0.0`) and compare the new package with it. Removing or changing a
  published member fails the pack with `CP0002` and similar errors, naming the member. A major release
  is the one place to accept a break: pack once with `-p:ApiCompatGenerateSuppressionFile=true`, which
  writes the accepted differences to a `CompatibilitySuppressions.xml` next to the project, and commit
  that file with the change.
- **You.** Package validation cannot see behaviour. 1.1.0's new `Subscribe` overload was
  binary-compatible, but it changed which overload existing source binds to when recompiled, so the
  README gained an *Upgrading to 1.1.0* section. Do the same for any change existing code can observe.

`IEventBus` is an interface that consumers implement too, typically as test doubles. A new member
breaks every one of them unless it has a default implementation, as `Subscribe(Func<TEvent, Task>)`
does.

## Commit messages

Explain the reasoning, not just the change. A reader six months from now needs to know why, and the
diff already tells them what.

## Releasing

Publishing to NuGet happens one way, and only one way — the same as for Riddersholm.Resultify and
Riddersholm.Money:

1. Set `<Version>` in [`src/EventBus/EventBus.csproj`](src/EventBus/EventBus.csproj) to the version you
   are releasing and merge that to `main`. It is the version local builds get; CI overrides it from the
   tag.
2. Tag the merge commit — `git tag v1.2.0 && git push origin v1.2.0`.
3. **Publish a GitHub Release for that tag.** This is what triggers the publish job in
   [`pipeline.yml`](.github/workflows/pipeline.yml); the tag alone does nothing. There is no CHANGELOG
   file, so the release notes are the record of what changed. Tick *Set as a pre-release* for an
   `-rc`/`-beta` version.
4. Once a stable release is on nuget.org, raise `PackageValidationBaselineVersion` to it.

The release run builds with `-warnaserror`, runs the tests and packs (with package validation) before
the publish job pushes anything, so a release cannot ship something CI would have rejected. The package
version is the tag with the leading `v` stripped, used as-is, so tag plain versions only (`v1.2.0`,
`v1.2.0-rc.1`). The push uses `--skip-duplicate`, so re-running a half-finished release is safe.

There is **no `NUGET_API_KEY` secret**, deliberately. The workflow authenticates with
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): GitHub mints an
OIDC token for the run, and NuGet exchanges it for an API key that expires in an hour. It is already set
up; for reference, the nuget.org policy (*your profile → Trusted Publishing*) is:

| Field | Value |
|---|---|
| Policy name | `EventBus-GitHubActions` |
| Repository owner | `Riddersholm1` |
| Repository | `EventBus` |
| Workflow file | `pipeline.yml` |
| Environment | *(empty)* |

The repository **variable** `NUGET_USER` (Settings → Secrets and variables → Actions → *Variables*)
holds the nuget.org profile name that owns the policy. The publish job checks for it before logging in,
and renaming or moving `pipeline.yml` breaks the match until the policy is updated.

The optional **`CODECOV_TOKEN`** secret enables the coverage upload; the step never fails the build
without it.

## Reporting a security issue

Please do not open a public issue — see [SECURITY.md](SECURITY.md).

## Code of conduct

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
