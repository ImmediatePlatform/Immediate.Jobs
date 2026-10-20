# Storage

## Contract

Two interfaces in `src/Immediate.Jobs.Shared/Storage` define the provider boundary:

- **`IJobStorage`** covers queueing, definition configuration and acquisition controls, leases,
  execution outcomes, recurring schedules, triggers, queries, monitoring, heartbeats, and retention.
- **`IJobGraphStorage : IJobStorage`** adds atomic graph operations: batches, continuations, dynamic
  additions, batch triggers and queries, and the recovery reads needed by single-server storage.

Graph support is optional because it requires atomic writes across related records.
`StorageCapabilities` advertises support, and graph APIs check it before performing work. Preserve
capability enum values because monitoring and health-check data serialize them.

### Invariants every provider upholds

- **Atomic transitions and ownership.** Completion, failure, telemetry, and lease renewal require the
  matching worker id and attempt. A stale worker cannot change a reclaimed invocation.
- **Consistent errors.** Missing targets raise `KeyNotFoundException`; invalid state transitions raise
  `ImmediateJobException`. Callers, including the dashboard, rely on that distinction.
- **Deterministic acquisition.** Order eligible jobs within a queue by `DueAt`, `CreatedAt`, then
  `JobHandle`, respecting queue and definition capacities. Providers with expiring ownership
  reclaim expired leases before claiming.
- **Faithful records.** Preserve provider-neutral fields and serialized payloads across writes and
  reads. Provider-specific precision and collation limits are described below.
- **Coherent graphs.** Preserve trigger gates and dependency counters during every transition. Retain
  a batch's header, members, executions, and edges together until batch retention removes them.
  See [batches and continuations](batches-and-continuations.md).

Test these contracts through the shared [conformance suite](testing.md#the-conformance-suite).

## Definition reconciliation

`JobSchedulingService` owns startup registration. It supplies the complete local definition catalogue,
code-defined recurrence timestamps, and normalized server tags to `MergeJobDefinitionsListAsync`.
Providers can rely on consistency established at that boundary; configuration validation belongs
with definition creation and scheduler configuration. See [responsibilities](conventions.md#code-patterns).

Reconciliation is atomic within the server's tag scope. Evaluate scope from stored tags before
applying updates, so retagging can remove a formerly managed definition. Upsert matching supplied
definitions and remove matching stored definitions omitted from the catalogue. Preserve unrelated
scopes; an empty catalogue clears only its scope.

Removing a definition cancels its nonterminal invocations and fences their workers. Retain those
records and their cancellation reason until purged. Dynamic schedules for the removed definition
also retain a cancellation reason and cannot resume or materialize further occurrences. Preserve
terminal invocation history. Code-defined schedules follow the recurring configuration of their
registered definition; preserve pause state and unchanged recurrence progress across startup.

Each server's catalogue must be authoritative within its scope. Overlapping scopes with inconsistent
catalogues can replace or remove one another's definitions: the last committed reconciliation wins.
There is no deployment-version or per-server ownership arbitration.

Use each provider's concurrency mechanism to commit the complete catalogue, schedule changes, and
related cancellations together. Retry a conflicting operation from a fresh snapshot. A partially
applied catalogue must never become visible as a successful reconciliation.

## Submissions and reads

Writes accept `JobSubmission`, which contains invocation data. Reads return `JobRecord`, which also
contains execution state and the definition's current queue. An unknown definition rejects the
complete submission, including batches and buffered continuations. Store the definition's canonical
job name with each invocation.

Existing jobs and recurring schedules follow current queue configuration. An active worker retains
the execution context acquired for its current attempt. Retained history remains queryable when its
definition has been removed; its current queue is then unavailable.

`GetJobDefinitionsAsync` returns the complete catalogue in ordinal, case-insensitive name order.
Monitoring separates configuration (`GetMonitoringDefinitionsAsync`) from live observations
(`GetMonitoringSnapshotAsync`). Both are global, independent of local handlers and server tags.
Evaluate status in bulk rather than issuing an acquisition-state query for each definition.

## Providers

| Provider | Concurrency and authority |
| --- | --- |
| In-memory | One `_gate` lock protects mutable state, including indexes. Support methods document whether callers must hold it and how long returned references or enumerations remain valid. |
| Single-server | Durable storage commits mutations and acquisitions before the in-memory primary mirrors them. Serialize write-through operations so a claim cannot overtake an unfinished mirror update. Recovery rebuilds the primary from durable definitions, schedules, jobs, batches, and edges. |
| EF Core | Contexts and transactions isolate mutations. Concurrency stamps and retry strategies protect updates across scheduler processes; catalogue writes are serialized. |
| LinqToDB | Transactions and compare-and-swap updates protect mutations. Retry a lost race as a complete operation. |
| Redis | Lua scripts make related key updates atomic. Catalogue reconciliation also uses version checks to detect competing snapshots. Graph operations are unsupported. |

The in-memory provider keeps pending and scheduled jobs ordered per definition by due time,
creation time, and ordinal handle. Ordinary acquisition merges the earliest eligible entries across
definitions. Ownership lasts until an explicit state transition; elapsed time cannot reclaim a job
owned by the same process. Active counts, concurrency limits, and fair-queue usage depend on job
state, with counts cached per definition. State and due-time changes update these indexes under
`_gate`; lease timestamps mirrored from durable storage are metadata and do not affect eligibility.
Standalone in-memory acquisitions have no lease deadline, and their scheduler does not run renewal.

Single-server mode has one scheduler process. Its durable provider remains the authority for claims,
definition pauses, and acquisition counters so those controls survive restarts. Durable graph
providers must also implement the recovery reads; the in-memory primary is a reconstructable mirror.
Durable claims and renewals retain their lease semantics in this topology. Runtime reclaim and
restart recovery remain the durable provider's responsibility; the primary mirrors committed claims
and renewal timestamps without independently expiring them. In particular, an unexpired claim
loaded during startup remains recoverable through subsequent durable acquisition.

Fair-queue cursor cleanup must not undo a committed job transition. Relational providers perform that
cleanup after the main transaction commits. Selection rules are in [fair queues](fair-queues.md).

## Persisted representation

### Names and collation

Definition and recurring schedule names are case-insensitive identities whose records retain their
canonical spelling. In-memory matching uses `OrdinalIgnoreCase`; SQL Server uses database collation;
PostgreSQL uses a nondeterministic ICU collation; SQLite uses `NOCASE`.

SQLite's case-insensitive matching is limited to ASCII letters. Preserve the registered case of
non-ASCII characters: `Résumé` and `RéSUMé` match, while `Résumé` and `RÉSUMÉ` do not. EF tracking
uses an ordinal case-insensitive comparer. Applications with custom database conventions must keep
provider name collation consistent with these identity rules.

### Time and serialization

Persist relational durations as invariant `TimeSpan` strings, preserving tick precision. JSON uses
`System.Text.Json`'s `TimeSpan` representation. Attribute durations also use invariant strings.

Timestamp precision follows the backend. SQL Server uses `datetimeoffset`; PostgreSQL uses
`timestamp with time zone` and returns UTC instants. SQLite and Redis store readable ISO8601
values, while Redis sorted-set scores provide numeric indexes. Avoid assuming that every provider
preserves sub-microsecond precision or the original offset.

Serialized invocation payload and context are opaque to storage. Preserve their strings when
updating surrounding records; parsing and reserializing them can change application data.

### Relational schema

Keep invocation state, execution history, graph records, recurring schedules, server heartbeats,
and acquisition controls separate according to their lifetimes. Definition configuration is stored
in columns; operational pause, concurrency, and rate state have separate storage. Tags are serialized
collections whose matching follows the same scope rules across providers.

Applications configure the EF Core model through `AddImmediateJobs`. LinqToDB's schema helper creates
the current tables and indexes. Bootstrap helpers target fresh storage; they do not manage production
schema upgrades. Schema changes follow the repository's [compatibility policy](conventions.md#public-api-and-compatibility).

### Definition acquisition controls

Acquisition, monitoring, pause, and resume use persisted definition limits. Requests supply worker
identity, leases, queue capacities, and an optional fair-queue policy. Validate limit configuration
at its owning boundary through `JobAcquisitionLimits` and the analyzer/generator rules.

Rate capacity belongs to acquisition history. Purging invocation history must not erase sliding
acquisition timestamps or fixed-window counters. Evaluate durable concurrency against active,
unexpired leases and in-memory concurrency against active job state. Reserve capacity and claim
the invocation atomically, rolling back reservations when a claim fails. Every acquisition path,
including fair selection, enforces every enabled limit.

Advisory eligibility reads must not create control rows or take acquisition write locks. Persisted
status is an observation; reevaluate eligibility when claiming work. Pause takes precedence over
rate and concurrency restrictions. A time-based `NextEligibleAt` is the latest blocking window
boundary and is absent when pause or concurrency also blocks work. Report active leases and
concurrency exhaustion independently of the primary restriction, including while paused.

`PauseJobAsync` blocks acquisition while allowing enqueue, trigger release, and recurring
materialization. `ResumeJobAsync` clears pause and reevaluates limits without changing configuration
or resetting counters. Both are idempotent and require an existing definition.

Public provider helpers, including `JobAcquisitionEvaluator`, let custom providers share these rules
without friend-assembly access. Boundary and retry semantics are in [job lifecycle](job-lifecycle.md#definition-pause-and-rate-limits).

### Redis state

Keys for a storage prefix share one cluster hash slot so a script can update related state atomically.
A job combines a serialized record with mutable hash fields; reads overlay those fields on the record.
Preserve opaque payload strings whenever a script or C# update rewrites that record.

Maintain due, state, lease, execution, recurring, and fair-group indexes with every transition that
affects their membership. A claim, renewal, retry, cancellation, purge, or recurring materialization
must leave all relevant indexes coherent. Clean stale index members against authoritative invocation
state before using them for eligibility or counts.

Keep definition configuration, acquisition controls, and catalogue versioning separate from invocation
records. Recurring due indexes must support filtering eligible job names before applying a batch
limit. See [recurring jobs](recurring-jobs.md#materialization) and [fair queues](fair-queues.md#providers)
for selection and index-maintenance rules.

## Adding or changing a storage member

1. Define the operation and exceptions on `IJobStorage` or `IJobGraphStorage`.
2. Implement the contract in every applicable provider, the single-server wrapper, capturing storage,
   and test doubles. Preserve each provider's transaction and retry idiom.
3. Add source-generated logging with stable event ids; see [conventions](conventions.md#logging).
4. Add provider-neutral conformance coverage at the storage boundary.
5. Explain compatibility effects for custom providers, schemas, and persisted data in the pull request.
