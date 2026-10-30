# Maintaining Immediate.Jobs

These documents explain the design decisions, contracts, and maintenance patterns that guide changes
to the repository. User documentation lives at
[immediateplatform.dev](https://immediateplatform.dev/docs/Immediate.Jobs/introduction).
Keep descriptions of the implemented design consistent with the code.

## What belongs here

- Explain ownership, invariants, tradeoffs, and patterns that apply across changes. Include the reason
  for a decision when it helps a maintainer preserve the design.
- Describe observable behavior and persisted contracts precisely. Use source code and API comments
  for exhaustive member lists, private helper names, defaults, and implementation mechanics.
- Keep change history, one-time migration instructions, and the reasoning for an individual patch in
  its pull request or release notes. Document the lasting rule that follows from a change here.
- Use examples to clarify a pattern. Avoid cataloguing individual UI placements or reproducing code
  that maintainers would need to update in two places.

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

- `Directory.Build.props` defines target frameworks and language settings; use a compatible SDK and
  install the runtimes needed by the selected targets. `global.json` selects the test runner.
- Building `Immediate.Jobs.Dashboard` runs `npm ci` and `npm run build` in
  `src/Immediate.Jobs.Dashboard/DashboardClient`, so Node must be on the path. Run `npm run check`
  from that directory after changing client sources.
- CI (`.github/workflows/build.yml`) builds Release with warnings as errors, runs every test project on
  every target framework, and packs.

## Ground rules

- **Mirror the surrounding code.** Each provider has its own transaction and concurrency idiom; a new
  member follows the idiom of its neighbours rather than introducing a new one. See
  [conventions](conventions.md).
- **Provider-neutral storage contracts belong in the conformance suite**, so all providers are held
  to them. See [testing](testing.md).
- **Compatibility follows an explicit policy.** See [conventions](conventions.md#public-api-and-compatibility)
  before changing public, persisted, or wire contracts.
- **Keep these documents current.** A pull request that changes behaviour described here updates the
  relevant page.
