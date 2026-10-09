# Testing

## Where a test belongs

| Project | Covers | Notes |
| --- | --- | --- |
| `tests/Immediate.Jobs.Tests` | Generator output and analyzers | Generator tests compare against Verify snapshots in `GeneratorTests/Snapshots`; review and accept the `*.received.*` files when output intentionally changes. Analyzer and suppressor tests use `Microsoft.CodeAnalysis.Testing`. |
| `tests/Immediate.Jobs.FunctionalTests` | The runtime above storage: schedulers, `JobSchedulingService`, batches built through the public API, context propagation, packages (`Packages/` covers the dashboard via TestHost, NodaTime, and the Testing package) | Uses `JobTestHarness` with jobs declared in the test assembly. |
| `tests/Immediate.Jobs.StorageTests` | Every storage provider | Runs the conformance suite against in-memory, capturing, EF Core and LinqToDB (SQLite, PostgreSQL, SQL Server; distributed and single-server), and Redis. PostgreSQL, SQL Server, and Redis run in Testcontainers, so Docker is required. |
| `DashboardClient/tests` | Vue components | `npm run check`. |

**Guidance for adding tests:**

- **Storage behaviour belongs in the conformance suite**, not in provider-specific tests, so every
  provider is held to it. Write a provider-specific test only for behaviour that exists in that
  provider alone.
- **Functional tests cover only what the layer above storage adds**, for example a scheduler turning
  `TryTriggerAsync`'s `false` into an exception, or argument validation. Don't re-assert storage
  semantics there.
- **Don't test framework or library behaviour**, and don't add a test that duplicates one in another
  project.

## The conformance suite

`src/Immediate.Jobs.Testing/Storage` ships in the `Immediate.Jobs.Testing` package, so custom
providers can run it too.

- **Case files.** `QueueStorageConformance`, `DefinitionCatalogStorageConformance`, `TagStorageConformance`, `RecurringStorageConformance`,
  `FairQueueStorageConformance`, `TriggerStorageConformance`, `GraphStorageConformance`, and
  `ReplicaStorageConformance`.
- **Case shape.** Each case is `new(Name, RequiredCapabilities, Scenario[, PersistedJobState])`. Names
  follow `Area.Topic.Behavior` and must stay unique and stable.
- **Selection.** `JobStorageConformanceSuite.GetCases(capabilities, includeSingleServerReplicaCases)`
  returns every case whose required capabilities are advertised. `Queue` cases run for every provider
  and `Graph` cases for graph storage. Replica cases additionally require the opt-in flag, because
  only EF Core and LinqToDB can back single-server mode.
- **Running a case.** `RunAsync` resolves exactly one `IJobStorage` and a `FakeTimeProvider`,
  initializes the storage, and runs the scenario. Fixtures create a fresh backend (database, schema,
  or key prefix) per case. `PersistedJobState` seeds pre-existing jobs, batches, edges, and schedules.
- **Assertions.** Use `ConformanceAssert`; failures name the case and the invariant that was broken.
- **Adding a case.** Add a name constant, an entry in `Cases`, and the scenario method.
  `StorageConformanceInfrastructureTests` asserts the number of `Queue`-tier cases, so update it when
  you add one there.

## Running

```console
dotnet test --project tests/Immediate.Jobs.StorageTests -c Release -f net10.0 -- --filter-class '*Redis*'
dotnet test --project tests/Immediate.Jobs.FunctionalTests -c Release -f net8.0
```

The test projects use Microsoft.Testing.Platform (xUnit v3); arguments after `--` go to the test
host. Locally, one target framework is usually enough for storage tests; CI runs all three. After
editing tests, rebuild every framework you run, because `--no-build` happily runs stale binaries.
