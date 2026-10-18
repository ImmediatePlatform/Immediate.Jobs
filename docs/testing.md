# Testing

## Where a test belongs

| Project | Responsibility |
| --- | --- |
| `tests/Immediate.Jobs.Tests` | Generator output, analyzers, and suppressors. Verify snapshots record generated source; Microsoft.CodeAnalysis.Testing checks diagnostics. |
| `tests/Immediate.Jobs.FunctionalTests` | Behavior above storage: schedulers, runtime coordination, context propagation, and package integration through public APIs. |
| `tests/Immediate.Jobs.StorageTests` | Provider conformance. Durable fixtures cover supported engines and distributed or single-server topology. |
| `src/Immediate.Jobs.Dashboard/DashboardClient/tests` | Client behavior, navigation, query ownership, and mutation feedback. |

Test each invariant at the layer that owns it:

- **Provider-neutral storage contracts** belong in the shared conformance suite. Every applicable
  provider must satisfy the same state, ownership, ordering, and atomicity rules. Express storage
  regressions through these cases rather than standalone provider- or engine-specific tests.
- **Functional tests** cover what the scheduler or package adds above storage. Use valid registrations
  and snapshots produced by the owning layer; avoid requiring downstream defensive validation of
  objects constructed internally.
- **Boundary validation** belongs with configuration, analyzers, generators, or public input handling.
  Cancellation, contention, and recovery remain runtime behaviors worth exercising at their owning
  boundaries.

Avoid testing framework behavior or repeating assertions already covered at another layer. Review
snapshot differences before accepting generated output changes.

## The conformance suite

`src/Immediate.Jobs.Testing/Storage` ships in the Testing package so custom providers can run the same
contracts. The [suite registration](../src/Immediate.Jobs.Testing/Storage/JobStorageConformanceSuite.cs)
is the source of truth for available cases.

Each case has a stable, unique `Area.Topic.Behavior` name, required capabilities, a scenario, and an
optional persisted-state fixture. Queue cases run for every provider; graph cases require graph
support. Replica recovery cases are opt-in because a single-server durable provider needs additional
recovery operations.

Resolve exactly one `IJobStorage` and a `FakeTimeProvider` for a case. Isolate each scenario with fresh
storage, a database/schema, or a Redis prefix. Persisted-state fixtures represent the starting state
of a recovery scenario. Use `ConformanceAssert` so failures identify both the case and its invariant.
When adding cases, update suite registration and the infrastructure checks for capability routing
and case discovery.

## Deterministic runtime tests

Use `JobTestHarness` and its injected clock instead of real delays. Initialize a fresh harness with
`DrainAsync` before scheduling, then advance time and drain explicitly when a scenario requires it.
Use explicit synchronization for contention and worker coordination so tests assert ordering rather
than depending on thread timing.

## Running

```console
dotnet test --project tests/Immediate.Jobs.StorageTests -c Release -f net10.0 -- --filter-class '*Redis*'
dotnet test --project tests/Immediate.Jobs.FunctionalTests -c Release -f net8.0
```

The projects use Microsoft.Testing.Platform with xUnit v3; arguments after `--` go to the test host.
PostgreSQL, SQL Server, and Redis fixtures require Docker. Run the affected cases locally and use CI
for the full framework and provider matrix. Rebuild every framework being executed so results
reflect the current sources. Build prerequisites are in [the maintainer guide](README.md#build-and-test).
