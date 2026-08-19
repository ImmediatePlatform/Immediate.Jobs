# Storage provider conformance tests

> **Status:** Implemented.
> **Goal:** Publish one executable storage contract in `Immediate.Jobs.Testing` and run that exact
> contract against both built-in and third-party providers.
> **See also:** [`storage-capabilities.md`](storage-capabilities.md),
> [`provider-suitability.md`](provider-suitability.md), and
> [`fair-queues.md`](fair-queues.md).

## 1. Outcome

`Immediate.Jobs.Testing` exposes a test-framework-neutral catalog of storage conformance cases.
A provider author supplies the provider's `StorageCapabilities`, creates an `IServiceProvider` for
an isolated instance of their provider, and adapts the catalog to xUnit, NUnit, MSTest, or another
runner. Immediate.Jobs' own provider matrix consumes the same catalog from the package project.

This makes the package the executable specification for `IJobStorage` and its optional capability
interfaces. A behavior is not considered part of the portable provider contract until it is covered
by a shared conformance case.

The suite must:

- give every invariant a separately discoverable test case;
- select optional cases from caller-supplied `StorageCapabilities`;
- verify that the `IJobStorage` resolved from the supplied service provider implements exactly the
  advertised capabilities;
- exercise concurrent storage operations against one isolated backend;
- use a controllable `TimeProvider` for lease, retention, heartbeat, and recurring tests;
- avoid dependencies on xUnit, NUnit, MSTest, Testcontainers, database drivers, or a particular
  persistence technology;
- throw `JobTestAssertionException` with useful expected/actual diagnostics when an invariant fails;
- leave provider-specific implementation tests in their owning provider test projects.

## 2. Decisions

### 2.1 The conformance catalog is framework-neutral

Do not ship an xUnit base class or attributes from `Immediate.Jobs.Testing`. A base class couples the
package to one runner, makes multiple provider variants awkward, and makes it harder for authors to
combine the suite with their own fixture lifecycle.

Instead, publish test-case objects with an async execution method. Test projects expose those objects
through their runner's normal parameterized-test mechanism. Do not offer only a `RunAllAsync` method:
one aggregate test hides the individual contract name and stops at the first failure.

### 2.2 Capabilities are explicit and verified against typing

`JobStorageConformanceSuite.GetCases` receives a `StorageCapabilities` flag set. It returns the queue
cases plus every optional suite selected by those flags. The flag set is the provider test project's
explicit conformance claim; separate support booleans and per-case skip switches are not supported.

The claim does not replace capability typing. Before a case exercises its behavior, it resolves
`IJobStorage` from the supplied `IServiceProvider`, derives the storage's capabilities from the
interfaces implemented by that instance, and asserts that they exactly match the flags passed to
`GetCases`. A provider therefore cannot select optional cases without implementing their marker
interfaces, or implement a capability while silently omitting its cases.

The existing type/capability relationship becomes:

| Provider type implements | Derived `StorageCapabilities` flag | Shared suite |
| --- | --- | --- |
| `IJobStorage` | `Queue` | Queue lifecycle, monitoring, history, and maintenance |
| `IRecurringJobStorage` | `Recurring` | Recurring schedule lifecycle and materialization |
| `IJobGraphStorage` | `Graph` | Atomic batches, continuations, and graph reads |
| `IFairQueueStorage` | `FairQueues` | Fair acquisition behavior |
| `IJobStorageReplica` | `Replica` | Exact-id acquisition required by single-server mode |
| `IJobGraphStorageReplica` | — | Durable standalone continuation-edge reads required for startup recovery in single-server mode |

`IJobStorageReplica` describes a durable store that can mirror the exact jobs selected by the
authoritative in-memory primary. `InMemoryJobStorage` does not implement it: in-memory mode uses that
provider directly, and using another in-memory instance as single-server backing storage would not
provide durability. The built-in relational providers implement the replica capability.

Add `IFairQueueStorage : IJobStorage` as a marker capability. It means that
`AcquireDueJobsAsync` honors a non-null `JobAcquisitionRequest.FairQueues`. In-memory, EF Core,
LinqToDB, and the single-server wrapper implement it. Redis does not.

Extend `StorageCapabilities` with `FairQueues` and `Replica`. The conformance suite uses the existing
instance-based capability detection after resolving `IJobStorage`, so runtime reporting and test
validation cannot disagree.

Public API:

```csharp
[Flags]
public enum StorageCapabilities
{
    None = 0,
    Queue = 1,
    Recurring = 2,
    Graph = 4,
    FairQueues = 8,
    Replica = 16,
}

public interface IFairQueueStorage : IJobStorage;

public static class JobStorageCapabilities
{
    public static StorageCapabilities GetCapabilities(this IJobStorage storage);
}
```

Provider authors pass the flags they expect the registered storage instance to report. A mismatch is
a conformance failure with the advertised and resolved flag sets in its diagnostics.

### 2.3 The service provider owns backend isolation and storage lifetime

The provider test project creates a fresh service provider for every conformance case. That service
provider represents one empty, isolated backend namespace, such as a unique SQL schema, temporary
SQLite database, Redis key prefix, or dedicated test database. Register the controllable
`FakeTimeProvider` as `TimeProvider`, then configure storage through the public builder:

```csharp
services.AddImmediateJobsCore()
    .ConfigureStorage(storage => storage
        .UseAcmeStorage(options)
        .UseDistributed());
```

Call `ConfigureStorage` exactly once. Use the provider's normal `IImmediateJobsStorageBuilder`
extension rather than registering `IJobStorage` directly. This is the provider's conformance seam.
The suite does not construct a concrete storage type, call an internal factory, or require
`InternalsVisibleTo`.

Topology is part of the fixture. Distributed mode resolves the durable provider itself. Single-server
mode resolves `SingleServerJobStorage`, with the durable provider behind it as a replica. The
capability flags passed to `GetCases` must describe the resolved `IJobStorage`, not the backing
provider.

The registered storage:

- connects to the isolated data;
- uses the registered controllable clock;
- may receive concurrent calls from the conformance case.

`JobStorageConformanceTestCase.RunAsync` does not create or dispose the service provider. The runner
fixture disposes it after the case completes; normal DI disposal then disposes the resolved storage
and removes the isolated database/schema/keyspace or releases its associated resources. The fixture
may reuse an assembly-level container, server, connection multiplexer, or emulator, provided each
case's service provider remains isolated and safe to run in parallel.

Schema provisioning is the provider's responsibility. The resolved storage must be connected to a
usable empty backend, but the suite still calls `InitializeAsync` where it tests that method's
runtime contract. Provider-specific migration generation and upgrade testing remain outside the
portable suite.

## 3. Public API shape

Place the API under `Immediate.Jobs.Testing` and keep the storage-specific files in a `Storage`
directory within that project.

```csharp
public sealed class JobStorageConformanceTestCase
{
    public string Name { get; }

    public StorageCapabilities RequiredCapabilities { get; }

    public ValueTask RunAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default);

    public override string ToString();
}

public static class JobStorageConformanceSuite
{
    public static IReadOnlyList<JobStorageConformanceTestCase> GetCases(
        StorageCapabilities capabilities);

    public static IReadOnlyDictionary<string, JobStorageConformanceTestCase> AllCasesByName { get; }
}
```

`GetCases(capabilities)` returns the queue cases plus every optional suite selected by the supplied
flags. Each returned case also exposes its requirements for reporting and documentation. The full
advertised flag set is retained by each case so `RunAsync` can compare it with the capabilities of
the resolved storage before running the scenario.

`AllCasesByName` contains every published case, keyed case-insensitively by its stable name. The
repository's xUnit serializer stores the name during discovery and uses this map to recover the case
at execution time.

`RunAsync` calls `serviceProvider.GetRequiredService<IJobStorage>()` and uses that resolved instance
for the case. Resolution failure and a capability mismatch are reported as conformance failures
before the behavioral assertions run.

The conformance API deliberately accepts an already-built `IServiceProvider`, rather than an
`IServiceCollection` or provider-specific options. Backend provisioning, configuration, registration
method selection, service-provider validation, and disposal remain under the test fixture's control.

`FakeTimeProvider` is already a public part of `Immediate.Jobs.Testing` through `JobTestHarness`, so
using it here adds no new package dependency. Time-dependent cases resolve `TimeProvider` from the
same service provider, require it to be a `FakeTimeProvider`, and prove behaviorally that storage
operations observe that clock.

### 3.1 Example xUnit adapter

The package documentation should include a complete example equivalent to:

```csharp
public sealed class AcmeStorageConformanceTests
{
    private const StorageCapabilities Capabilities =
        StorageCapabilities.Queue |
        StorageCapabilities.Recurring;

    public static TheoryData<JobStorageConformanceTestCase> Cases =>
        [.. JobStorageConformanceSuite.GetCases(Capabilities)];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Conforms(JobStorageConformanceTestCase testCase)
    {
        await using var services = await AcmeStorageFixture.CreateServiceProviderAsync(
            TestContext.Current.CancellationToken);

        await testCase.RunAsync(
            services,
            TestContext.Current.CancellationToken);
    }
}
```

Also document the small equivalent adapter for NUnit or MSTest to demonstrate that the catalog has
no xUnit-specific lifecycle assumptions. `AcmeStorageFixture.CreateServiceProviderAsync` must call
Acme's public storage registration method; it is test infrastructure for backend isolation and DI
composition, not a privileged storage constructor.

For example, NUnit can expose the same objects without an Immediate.Jobs-specific base class:

```csharp
public static IEnumerable<JobStorageConformanceTestCase> Cases =>
    JobStorageConformanceSuite.GetCases(Capabilities);

[TestCaseSource(nameof(Cases))]
public async Task Conforms(JobStorageConformanceTestCase testCase)
{
    await using var services = await AcmeStorageFixture.CreateServiceProviderAsync();
    await testCase.RunAsync(services);
}
```

## 4. Internal organization

Suggested source layout:

```text
src/Immediate.Jobs.Testing/Storage/
  JobStorageConformanceTestCase.cs
  JobStorageConformanceSuite.cs
  ConformanceAssert.cs
  QueueStorageConformance.cs
  RecurringStorageConformance.cs
  GraphStorageConformance.cs
  FairQueueStorageConformance.cs
  ReplicaStorageConformance.cs
```

Only the test-case and suite types are public. Scenario implementations, record builders,
acquisition-request builders, and assertion helpers remain internal.

Case names should be stable, behavior-oriented identifiers such as:

```text
Queue.Acquisition.ClaimsEachJobOnceUnderContention
Queue.Leases.ReclaimsExpiredLeaseAndRejectsStaleOwner
Recurring.Materialization.DeduplicatesConcurrentOccurrence
Graph.Batches.RollbackInvalidBatchWithoutPartialWrites
FairQueues.Rotation.ServesNewGroupAheadOfServedBacklog
Replica.Acquisition.ClaimsExactlyTheRequestedDueJobs
```

`ToString()` returns the stable name so parameterized test runners display it naturally. Renaming a
case is a documentation change and should be avoided unless its contract changes.

## 5. Conformance coverage

The lists below are the initial contract inventory. During implementation, audit every method and
documented exception on the storage interfaces against this inventory. Related assertions may share
one case when splitting them would multiply expensive backend setup without improving diagnostics.

### 5.1 Queue suite — `IJobStorage`

The queue suite runs for every provider.

#### Initialization and lifecycle

- initialization succeeds against a provisioned empty backend and is idempotent;
- operations through the resolved storage observe committed state consistently;
- repeated and concurrent `DisposeAsync` calls are tolerated as required by the interface contract;
- `IsHealthyAsync` reports a reachable provisioned backend.

#### Enqueue and durable representation

- pending and scheduled jobs round-trip all provider-neutral `JobRecord` fields;
- payload, ambient context, fair-queue group, trace context, timestamps, and opaque identifiers are
  not rewritten;
- duplicate invocation identifiers fail without altering the original record;
- parked or future jobs are not acquired.

#### Acquisition and ownership

- due jobs are ordered deterministically and respect request batch size;
- queue order, queue capacity, and per-job-name capacity are honored together;
- concurrent acquisitions against the resolved storage claim every job at most once;
- acquired records contain active state, worker ownership, lease expiry, and the correct execution
  ordinal;
- lease renewal succeeds only for the current execution owner;
- an expired lease is reclaimable by another worker and begins a new execution attempt;
- reclaim clears latest-attempt telemetry from the job projection while retaining execution history;
- stale execution owners cannot record telemetry, renew, complete, fail, or commit related writes.

#### Completion, failure, and execution history

- telemetry is persisted for the matching active execution;
- successful completion updates the job and its retained execution atomically;
- retryable failure schedules the next attempt and persists its error;
- terminal failure updates the job and retained execution atomically;
- `QueryJobExecutionsAsync` orders, filters, and pages execution records correctly;
- retrying a failed job and fast-forwarding a scheduled job preserve the documented attempt/error
  semantics;
- invalid transitions throw `ImmediateJobException` without partial mutation;
- missing dashboard mutation targets throw `KeyNotFoundException`.

#### Queries and monitoring

- exact-id lookup does not change result semantics when unrelated data exists;
- state, queue, name/search, and time filters compose correctly;
- skip/take pagination visits tied creation times exactly once in documented order;
- job status returns incoming edges only where graph data exists and reports missing jobs as null;
- monitoring counts agree with persisted job states;
- server heartbeats appear while live and disappear after the liveness window.

#### Maintenance

- deletion accepts only terminal jobs and removes their execution history;
- job purge applies separate succeeded and failed/cancelled/skipped retention windows;
- purge does not loop or leave dangling execution records;
- cancellation is observed by representative storage operations without defining backend-specific
  timing guarantees.

### 5.2 Recurring suite — `IRecurringJobStorage`

- dynamic schedules can be upserted, updated, paused, resumed, and removed;
- code-defined schedules cannot be replaced by dynamic schedules;
- obsolete code-defined schedules are removed while active definitions are preserved;
- due scans exclude paused and future schedules and honor batch size;
- materialization atomically creates one occurrence and advances the schedule;
- concurrent materialization calls against the resolved storage create an occurrence exactly once;
- a deduplication hit still leaves the schedule at the correct next occurrence;
- stale due entries cannot materialize a future occurrence or roll a schedule backwards;
- skipped occurrences are persisted with their supplied state;
- schedule removal and retention cleanup remove provider-owned deduplication state as documented;
- missing and invalid recurring dashboard actions use the common exception conventions.

### 5.3 Graph suite — `IJobGraphStorage`

#### Atomic insertion and reads

- continuation insertion commits the child and every edge atomically;
- batch insertion commits the header, members, and edges atomically;
- invalid graphs and duplicate/conflicting identifiers leave no partial records;
- bulk incoming-edge lookup handles empty input, duplicate child ids, validation, and field
  preservation;
- batch status, listing, member paging, and graph projection agree with durable state;
- an empty or fully settled batch projects consistent counts and state.

#### Dependency transitions

- success, failure, and complete triggers evaluate every incoming edge regardless of parent
  completion order;
- fan-in children remain parked until every required edge settles;
- successful branches release and failed conditions skip the correct subtree;
- skipped conditional branches do not incorrectly fail or cancel an otherwise successful batch;
- retrying a parent does not settle an edge twice;
- concurrent parent completion and continuation insertion cannot strand a child;
- batch counters and terminal state change atomically with member transitions.

#### Dynamic additions and ownership

- completing an active execution with buffered continuations commits completion and additions
  atomically;
- a reclaimed/stale execution cannot commit its buffered continuation;
- detached, beside-continuations, and before-continuations options produce the documented graph;
- invalid batch relationships, option values, and trigger values fail before any mutation;
- immediate batch additions validate the current execution owner.

#### Batch maintenance

- cancellation settles every non-terminal member, including unresolved dependency chains;
- standalone continuation deletion removes its incoming edges;
- terminal batch deletion removes its header, members, executions, and related edges atomically;
- batch purge applies succeeded and failed/cancelled retention independently.

### 5.4 Fair-queue suite — `IFairQueueStorage`

- capacity-one acquisitions rotate across backlogged groups;
- one larger acquisition interleaves groups while preserving capacity limits;
- a newly arrived or returning group advances ahead of previously served backlog;
- cursor state resets after a group's backlog clears;
- noisy groups are served after quiet groups according to the supplied policy;
- expired leases do not contribute to noisy-group classification;
- ungrouped jobs retain normal due ordering and do not share one synthetic group;
- disabling group round-robin retains due order while still applying noisy-neighbor classification;
- concurrent acquisitions against the resolved storage claim distinct jobs and keep cursor/job
  mutations consistent;
- a null fair-queue policy follows the ordinary acquisition contract.

Collation-specific group identity behavior is not portable. The shared suite uses identifiers whose
equality is stable across supported collations; SQL collation edge cases remain provider tests.

### 5.5 Replica suite — `IJobStorageReplica`

- exact-id acquisition claims only requested, due, currently available jobs;
- duplicate and missing requested ids do not duplicate results;
- ownership, lease, attempt, and execution-history transitions match ordinary acquisition;
- concurrent exact-id acquisitions cannot acquire the same invocation version;
- a stale replica owner cannot mutate a reclaimed execution;
- graph/recurring fields needed by single-server recovery round-trip unchanged.

Single-server orchestration and recovery ordering remain tests of `SingleServerJobStorage`; the
portable replica suite covers only the provider capability used by that wrapper.

## 6. What remains provider-specific

Do not move tests whose assertions depend on an implementation technique rather than the public
storage contract. Keep, or add, provider-local coverage for:

- EF Core execution strategies, interceptors, model-cache keys, migrations, and generated SQL;
- LinqToDB retry loops, dialect-specific SQL, and sustained adapter regressions;
- cross-adapter schema compatibility between EF Core and LinqToDB;
- PostgreSQL, SQL Server, and SQLite schema creation/cleanup behavior;
- database collation behavior for fair-queue group ids;
- Redis key layout, Lua script behavior, corrupt-record isolation, scan/window optimizations, TTL
  implementation, cluster slotting, and application-owned connection disposal;
- topology selection, health-check registration, advanced DI composition, and provider option
  validation beyond resolving the advertised `IJobStorage`;
- Testcontainers startup and external service lifecycle;
- fault injection that reaches private provider race windows unavailable through the public API.

A provider-local regression may later move into the shared suite when it reveals a portable
invariant. Move the behavioral assertion, not the provider-specific reproduction mechanism.

## 7. Maintainer migration

### 7.1 Relational matrix

EF Core and LinqToDB have separate conformance fixtures. Each adapter has its own PostgreSQL and SQL
Server container collections, while SQLite uses a temporary file. Every case gets a unique schema or
SQLite database, a fake clock, and a new service provider.

Each fixture calls `AddImmediateJobsCore().ConfigureStorage(...)` with its adapter extension. Calling
`UseDistributed()` tests the durable provider directly. Leaving the durable topology unspecified
selects the default single-server wrapper.

Run the packaged cases for each existing matrix entry:

```text
SQLite      × EF Core
SQLite      × LinqToDB
PostgreSQL  × EF Core
PostgreSQL  × LinqToDB
SQL Server  × EF Core
SQL Server  × LinqToDB
```

In distributed mode, both adapters advertise queue, recurring, graph, fair-queue, and replica
capabilities. In single-server mode, the resolved `SingleServerJobStorage` advertises queue,
recurring, graph, and fair queues, but not replica. The backing provider still uses its replica
interfaces for recovery. Provider validity comes from these conformance cases rather than parallel
adapter-specific storage test classes.

### 7.2 Redis

The Redis fixture allocates a unique key prefix while reusing its collection-level Redis container.
It registers `IConnectionMultiplexer`, then calls
`AddImmediateJobsCore().ConfigureStorage(storage => storage.UseRedis().ConfigureRedis(...))`.
`UseRedis()` selects distributed mode. Pass
`StorageCapabilities.Queue | StorageCapabilities.Recurring` to `GetCases`; `RunAsync` verifies that
the registered `RedisJobStorage` reports those capabilities and excludes graph, fair-queue, and
replica cases from the catalog.

The Redis conformance class replaces direct `RedisJobStorage` tests. Capability matching verifies
that Redis does not advertise graph, fair-queue, or replica behavior, while the portable queue and
recurring cases cover its externally observable storage contract.

### 7.3 In-memory and single-server

Run the same catalog against `InMemoryJobStorage`, registered with
`AddImmediateJobsCore().ConfigureStorage(storage => storage.UseInMemory())`. This is important even
though the suite lives in `Immediate.Jobs.Testing`: the in-memory provider is a real implementation
and acts as the fastest contract smoke test.

Run applicable cases against `SingleServerJobStorage` through a service provider backed by every
isolated relational provider in the matrix. The wrapper exercises `IJobStorageReplica` and
`IJobGraphStorageReplica` during acquisition and recovery, so this topology is part of relational
provider conformance rather than a separate direct-storage test class.

### 7.4 Test project dependency

Keep concrete storage conformance tests in `Immediate.Jobs.StorageTests`.
That project currently targets .NET 8, .NET 9, and .NET 10. It compiles the in-memory, Redis, EF Core,
and LinqToDB conformance tests for every target. .NET 11 is temporarily excluded until Npgsql supports
it. `Immediate.Jobs.FunctionalTests` may use storage as a fixture when testing schedulers, generated
jobs, or dashboard behavior, but it must not own storage-provider contract or implementation tests.

Add a project reference from the storage test project to `Immediate.Jobs.Testing`. Conformance tests
must consume the public API exactly as an external package consumer would. Provider projects expose
`IImmediateJobsStorageBuilder` extensions used inside `ConfigureStorage`; do not add
`InternalsVisibleTo`, public test-only constructors, or public conformance factories for conformance
execution.

## 8. Assertion and failure behavior

Use a small internal `ConformanceAssert` rather than importing a runner assertion library. It should
support equality, sequence equality, nullability, type/capability checks, expected exceptions, and
eventual concurrent results. Every failure throws `JobTestAssertionException` and includes:

- the stable conformance case name;
- the invariant being checked;
- relevant job, batch, schedule, worker, and execution identifiers;
- expected and actual values where safe;
- the original exception as `InnerException` when an unexpected provider failure obscures the
  invariant.

Do not catch cancellation requested through the test token. Do not retry ordinary assertions to hide
eventual-consistency bugs unless the public contract explicitly allows an observation window, such
as server-heartbeat expiry. Where a window is allowed, use fake time and a bounded number of reads;
never use wall-clock sleeps.

## 9. Versioning policy

The conformance contract versions with `Immediate.Jobs.Testing` and its matching `Immediate.Jobs`
dependency. Do not add a separate suite-version enum initially.

Adding a conformance case can make a third-party provider's CI fail after a package upgrade. That is
intentional when the case captures an existing documented invariant, but release notes must identify
new cases and any newly clarified behavior. A genuinely new storage requirement follows the normal
runtime compatibility/versioning policy.

Do not add per-case skip switches. Optional behavior is selected only by the flag set supplied to
`GetCases`, and `RunAsync` verifies that set against capability typing. A provider needing time to
adopt a newer contract can temporarily pin its package version rather than claim conformance while
suppressing individual invariants.

## 10. Implementation phases

### Phase 1 — Capability typing and catalog skeleton

1. Add `IFairQueueStorage` and implement it on the providers that honor fair policies.
2. Add `FairQueues` and `Replica` to `StorageCapabilities`.
3. Extend instance-based capability detection for the new marker interfaces.
4. Expose or standardize each built-in provider's `IImmediateJobsStorageBuilder` extension so tests
   can configure it through `AddImmediateJobsCore().ConfigureStorage(...)` without test-only access.
5. Add the public test-case and suite types to `Immediate.Jobs.Testing`.
6. Add infrastructure tests proving case selection follows the supplied flags, `RunAsync` rejects a
   mismatch between those flags and the resolved `IJobStorage`, and Redis cannot accidentally claim
   fair/graph/replica support.

### Phase 2 — Queue contract

1. Implement shared builders and assertions.
2. Port queue, contention, lease, telemetry, query, monitoring, execution-history, and maintenance
   invariants.
3. Run the cases against in-memory, Redis, EF Core, and LinqToDB.
4. Delete superseded copies only after every built-in provider is green.

This phase establishes the minimum useful external suite because every provider implements queue
storage.

### Phase 3 — Recurring contract

1. Port schedule lifecycle and due-scan cases.
2. Port concurrent materialization, deduplication, stale-schedule, and skipped-occurrence cases.
3. Run the same cases against every `IRecurringJobStorage` implementation.
4. Remove duplicated portable provider tests.

### Phase 4 — Graph contract

1. Port atomic insert/rollback and graph query cases.
2. Port trigger, fan-in, cascade, retry, and concurrency cases.
3. Port dynamic-addition ownership and validation cases.
4. Port cancellation, deletion, and purge cases.
5. Run the suite against in-memory, EF Core, LinqToDB, and single-server service-provider fixtures.

### Phase 5 — Fair-queue and replica contracts

1. Port the black-box fairness scenarios shared by in-memory and relational providers.
2. Add replica exact-acquisition and ownership scenarios.
3. Retain provider-specific cursor, collation, SQL, and recovery tests.
4. Confirm Redis is excluded by its advertised flags and that those flags match its implemented
   interfaces.

### Phase 6 — Package and documentation hardening

1. Add xUnit and NUnit/MSTest consumer examples to the testing documentation.
2. Document service-provider isolation and lifecycle requirements for containers and emulators.
3. Pack `Immediate.Jobs.Testing` and validate the examples against the `.nupkg`, not only project
   references.
4. Update the README storage-provider section to make passing the shared suite the expected quality
   bar for third-party providers.
5. List the stable case names in generated or checked-in reference documentation.

## 11. Validation

For each implementation phase:

1. Build every target framework supported by `Immediate.Jobs.Testing`.
2. Run the fast in-memory conformance cases on all supported test target frameworks.
3. Run Redis and the full relational container matrix on the repository's integration-test target.
4. Verify cases remain independently discoverable in test output.
5. Verify isolated service providers can run concurrently without identifier, schema, or key-prefix
   collisions.
6. Pack the testing project and run at least one sample provider test against the produced package.
7. Confirm the package contains no test-runner, Testcontainers, ORM, driver, or Redis dependency.

## 12. Acceptance criteria

The work is complete when:

- `Immediate.Jobs.Testing` publishes a framework-neutral, capability-flagged case catalog;
- `GetCases` selects cases from one caller-supplied `StorageCapabilities` flag set, with no separate
  support booleans or per-case skips;
- fair-queue support is represented by `IFairQueueStorage` and `StorageCapabilities.FairQueues`;
- every case resolves `IJobStorage` from the `IServiceProvider` passed to `RunAsync` and fails when
  its implemented capabilities do not exactly match the flags supplied to `GetCases`;
- each built-in provider runs every catalog case selected by its advertised flags;
- Redis advertises and runs queue and recurring cases, and excludes graph, fair-queue, and replica
  cases;
- every provider fixture builds the supplied service provider through
  `AddImmediateJobsCore().ConfigureStorage(...)` and the provider's public storage-builder extension;
- the relational matrix uses the public package API rather than private shared test helpers;
- conformance execution requires no `InternalsVisibleTo`, test-only storage constructor, or
  provider-specific factory in `Immediate.Jobs.Testing`;
- every removed maintainer test has an equivalent named conformance case;
- provider-specific tests remain in place for implementation and schema behavior;
- concurrency cases make concurrent calls against the resolved storage on one isolated backend;
- time-dependent cases use the service provider's `FakeTimeProvider` and contain no wall-clock
  sleeps;
- a documented external-provider example can consume the packed NuGet package and report each case
  separately through a mainstream .NET test runner.

## 13. Stable case-name reference

The initial published catalog contains these behavior identifiers:

```text
Queue.Acquisition.ClaimsEachJobOnceUnderContention
Queue.Acquisition.ExcludesFutureJobs
Queue.Acquisition.HonorsOrderingAndCapacities
Queue.Cancellation.ObservesPreCancelledOperation
Queue.Enqueue.RoundTripsRecordAndRejectsDuplicate
Queue.Executions.PersistsFailureRetryAndCancellation
Queue.Executions.PersistsTelemetryCompletionAndHistory
Queue.Health.ReportsProvisionedBackendReachable
Queue.Leases.ReclaimsExpiredLeaseAndRejectsStaleOwner
Queue.Lifecycle.InitializesIdempotently
Queue.Lifecycle.ToleratesRepeatedConcurrentDisposal
Queue.Maintenance.DeletesAndPurgesTerminalHistory
Queue.Monitoring.ReportsCountsStatusAndHeartbeatLiveness
Queue.Mutations.RejectsMissingAndInvalidTransitions
Queue.Queries.ComposesFiltersAndPagesDeterministically
Recurring.Capability.ResolvesAdvertisedStorage
Recurring.Definitions.ProtectsCodeDefinedAndRemovesOnlyObsoleteSchedules
Recurring.DueScanning.FiltersOrdersAndBatchesSchedules
Recurring.Exceptions.DistinguishesMissingAndInvalidDashboardActions
Recurring.Lifecycle.UpdatesPausesResumesAndRemovesDynamicSchedule
Recurring.Maintenance.PurgeRemovesOccurrenceDedupeState
Recurring.Materialization.CreatesOccurrenceAndAdvancesScheduleAtomically
Recurring.Materialization.DedupeHitStillAdvancesSchedule
Recurring.Materialization.DeduplicatesConcurrentOccurrence
Recurring.Materialization.PersistsSkippedOccurrence
Recurring.Materialization.RejectsStaleDueEntry
Graph.Batches.CommitsProjectsAndDeletesAtomically
Graph.Batches.RollbackInvalidBatchWithoutPartialWrites
Graph.Capability.ResolvesAdvertisedStorage
Graph.Dependencies.FanInWaitsForEveryParent
Graph.Dependencies.ReleasesAndSkipsConditionalBranches
Graph.Dynamic.SpliceAndOwnershipAreAtomic
Graph.Edges.PreservesAndNormalizesIncomingLookups
Graph.Maintenance.CancelsUnsettledDependencyChains
FairQueues.Capability.ResolvesAdvertisedStorage
FairQueues.Concurrency.ClaimsDistinctJobs
FairQueues.Disabled.PreservesOrdinaryDueOrder
FairQueues.NoisyNeighbors.IgnoresExpiredLeases
FairQueues.NoisyNeighbors.ServesQuietGroupFirst
FairQueues.Rotation.InterleavesGroupsWithinOneAcquisition
FairQueues.Rotation.ServesNewGroupAheadOfServedBacklog
Replica.Acquisition.ClaimsEachInvocationVersionOnceUnderContention
Replica.Acquisition.ClaimsExactlyTheRequestedDueJobs
Replica.Acquisition.IgnoresDuplicateAndMissingRequestedIds
Replica.Acquisition.PersistsOwnershipLeaseAttemptAndHistory
Replica.Capability.ResolvesAdvertisedStorage
Replica.Leases.ReclaimsExpiredExecutionAndFencesStaleOwner
Replica.Projection.PreservesRecurringAndGraphRecoveryFields
```
