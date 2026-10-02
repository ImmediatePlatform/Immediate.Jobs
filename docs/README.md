# Maintaining Immediate.Jobs

These documents describe how the repository works **today**, for people and agents changing it. They
are not user documentation (that lives at [immediateplatform.dev](https://immediateplatform.dev/docs/Immediate.Jobs/introduction))
and they are not design proposals. When the code and a document disagree, the code wins and the
document should be fixed in the same pull request.

## Start here

| If you are changing… | Read |
| --- | --- |
| Anything, for the first time | [architecture](architecture.md), then [conventions](conventions.md) |
| Job states, retries, leases, retention | [job lifecycle](job-lifecycle.md) |
| A storage provider or a storage interface member | [storage](storage.md) |
| Batches, continuations, or wait-for-trigger | [batches and continuations](batches-and-continuations.md) |
| Grouped acquisition | [fair queues](fair-queues.md) |
| Cron schedules, misfires, overlap | [recurring jobs](recurring-jobs.md) |
| Context propagation, traces, metrics, logs, health checks | [observability](observability.md) |
| The dashboard API or Vue client | [dashboard](dashboard.md) |
| Tests | [testing](testing.md) |

## Build and test

```console
dotnet build -c Release
dotnet test --project tests/Immediate.Jobs.Tests -c Release --no-build
dotnet test --project tests/Immediate.Jobs.FunctionalTests -c Release --no-build
dotnet test --project tests/Immediate.Jobs.StorageTests -c Release --no-build -f net10.0   # needs Docker
```

- The SDK is pinned by `global.json`; every library targets `net8.0`, `net10.0`, and `net11.0`.
- Building `Immediate.Jobs.Dashboard` runs `npm ci` and `npm run build` in `DashboardClient`, so Node
  must be on the path. Run `npm run check` there after touching the client.
- CI (`.github/workflows/build.yml`) builds Release with warnings as errors, runs every test project on
  every target framework, and packs.

## Ground rules

- **Mirror the surrounding code.** Each provider has its own transaction and concurrency idiom; a new
  member follows the idiom of its neighbours rather than introducing a new one. See
  [conventions](conventions.md).
- **Every storage behaviour change needs a conformance case**, so all providers are held to it. See
  [testing](testing.md).
- **The library is pre-1.0** (`v0.x`, preview). Breaking changes to public APIs and custom-provider
  contracts are acceptable when they simplify the design; call them out in the pull request.
- **Keep these documents current.** A pull request that changes behaviour described here updates the
  relevant page.
