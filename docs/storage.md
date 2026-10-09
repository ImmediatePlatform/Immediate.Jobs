# Storage

## Contract

Two interfaces in `src/Immediate.Jobs.Shared/Storage`:

- **`IJobStorage`**: queueing, definition pause/resume and acquisition status, acquisition (including rate limits and fair-queue policies), leases, completion and
  failure, scoped definition catalogues, recurring schedules, wait-for-trigger updates and triggers, queries, monitoring snapshots,
  heartbeats, purging. Every provider implements all of it.
- **`IJobGraphStorage : IJobStorage`**: batches, continuations, dynamic additions from running jobs,
  batch triggers, batch reads and purging, plus the two single-server recovery reads
  (`AcquireJobsAsync`, `GetIncomingEdgesAsync`).

Graph support is the only optional capability, because it needs atomic multi-row writes some
backends can't provide. `StorageCapabilities` (`Queue = 1`, `Graph = 4`) reports it, and
`JobStorageCapabilityGuards.RequireGraph` makes graph APIs fail fast with a "use a SQL provider"
message on non-graph storage. Keep the enum's numeric values stable; they are serialized into
monitoring snapshots and health-check data.

### Invariants every provider upholds

- **State transitions are atomic** and check the current state and owner: completion, failure,
  telemetry, and lease renewal require the matching worker id and attempt.
- **Errors follow one convention.** Missing jobs or batches throw `KeyNotFoundException`; invalid
  transitions throw `ImmediateJobException`. The dashboard maps these to 404 and 409.
- **Acquisition order** within a queue is `DueAt`, then `CreatedAt`, then `JobHandle`, honouring the
  request's queue capacity and per-job-name capacities. Expired leases are reclaimed before claiming.
- **Records round-trip** without rewriting provider-neutral fields; the conformance suite checks this.
- **Graph state** follows [batches and continuations](batches-and-continuations.md): `WaitingForTrigger`
  is preserved through settlement and dynamic additions, and `BatchState.WaitingForTrigger` is
  non-terminal.

## Definition reconciliation

`MergeJobDefinitionsListAsync(JobDefinitionRegistration)` receives the complete local definition list,
all code-defined schedules, and normalized server tags. It upserts supplied matching definitions and
removes stored matching definitions absent from that managed list. Scope is evaluated from the old
stored tags before updates, so retagging a definition outside the old server's scope removes its stale
entry. Definitions outside the scope remain untouched. Empty supplied lists clear only that scope.
Definition deletion never cascades to invocations, running leases, or execution history.

Definition names are case-insensitive identities and retain their readable spelling. In-memory
and single-server use `OrdinalIgnoreCase`; SQL Server inherits database collation; PostgreSQL uses
a nondeterministic ICU collation (`und-u-ks-level2`); SQLite uses `NOCASE` (ASCII-only database
comparison). EF tracking uses an ordinal case-insensitive key comparer. EF applications with custom
provider conventions can pass `definitionNameCollation` to `AddImmediateJobs`.
Definition names must not have leading or trailing whitespace: the job analyzer reports `IJOB0008`,
and storage rejects invalid manually supplied names before modifying the catalogue.

Legacy code-defined schedules without catalogue metadata belong to the `default` scope, preserving
startup cleanup after upgrading from a version without tags. A server without `default` leaves those
schedules alone unless it supplies a matching definition.

Concurrent startup snapshots commit atomically. In-memory uses its gate; relational providers take
an early write lock on a singleton catalogue row; Redis compares a version and applies metadata and
schedule changes in one Lua script, recomputing after a competing startup. Single-server delegates
metadata reads and reconciliation to durable storage, then refreshes the primary's code-defined
schedules. Pause and unchanged schedule progress survive startup.

Each server's list must be authoritative within its tag scope. Servers with overlapping tags and
inconsistent lists can remove or replace each other's definitions: the last successful reconciliation
wins. No per-server ownership or deployment-version arbitration is provided.

`GetJobDefinitionsAsync` returns the complete persisted catalogue in ordinal, case-insensitive name order, independently of server tags.

Custom providers must implement these operations and the eligible-name overload of
`GetDueRecurringAsync`. Relational users must add the two catalogue tables through their normal
schema update process; the bootstrap helpers do not upgrade existing production databases.

## Typed submissions

`JobScheduler<TPayload>` resolves the name and queue from persisted definitions before writing a
job through the existing storage APIs. Unknown names fail before submission. Synchronous batch
builders and buffered continuations resolve their queues when committed; dynamic recurring
schedules use the same persisted queue. Each durable invocation retains the selected queue after
definitions change or disappear.

Queue selection uses a catalogue read before the provider's invocation write. Storage submission
return contracts and raw `JobRecord` routing remain as before. Custom scheduler subclasses must
remove the queue argument from their base constructor; the scheduler's `QueueName` property is removed.

## Providers

| Provider | Location | Concurrency idiom |
| --- | --- | --- |
| In-memory | `Shared/Storage/InMemoryJobStorage.cs` | One `Lock` (`_gate`) around every operation. Also the primary inside single-server mode. |
| Single-server | `Shared/Storage/SingleServerJobStorage.cs` | Mutations go to the durable store first, then to the in-memory primary. A shared semaphore serializes write-through mutations and acquisitions so durable claims cannot overtake an unfinished mirror update. Acquisition reserves definition controls and claims jobs in durable storage, then mirrors committed ownership into the primary, so pauses and counters survive restarts. Startup recovery reloads every job (in every state), its batches, and standalone continuation edges from the durable store into the primary. |
| EF Core | `EntityFrameworkCore/EntityFrameworkCoreJobStorage.cs` | `IDbContextFactory<TContext>`. Each mutation is a `...CoreAsync` method that opens a context and transaction, updates tracked entities with a fresh `ConcurrencyStamp`, and runs through `RetryConcurrencyAsync` (optimistic-concurrency retries) or `ExecuteWithStrategyAsync`. |
| LinqToDB | `LinqToDB/LinqToDBJobStorage.cs` | Explicit compare-and-swap: `UpdateJobAsync(connection, job, oldStamp)` / `UpdateBatchAsync` return `false` on a lost race, and the caller throws `LostRaceException`, which `RetryConcurrencyAsync` retries. |
| Redis | `Redis/RedisJobStorage.cs`, `Redis/RedisScripts.cs` | Every multi-key mutation is one Lua script, so it is atomic. Implements `IJobStorage` only. |

In-memory, single-server, and capturing storage implement the single-server recovery reads by
throwing `NotSupportedException`; only EF Core and LinqToDB can be a single-server durable store.

Fair-queue cursor clean-up runs after the main transaction commits in EF Core and LinqToDB
(`TryRemoveFairQueueCursorAsync`, `CleanupFairQueueGroupsAsync`), so a clean-up failure can't roll
back a job transition.

### Relational schema

Tables: `immediate_jobs`, `immediate_job_executions`, `immediate_job_continuations`,
`immediate_job_batches`, `immediate_recurring_jobs`, `immediate_job_servers`, and
`immediate_fair_queue_groups`, `immediate_job_definition_metadata`, `immediate_job_definition_catalog`,
`immediate_job_definitions`, and `immediate_job_acquisitions`.

- **EF Core**: applications call `modelBuilder.AddImmediateJobs()` to configure the current model.
- **LinqToDB**: `CreateImmediateJobsSchemaAsync` bootstraps the current tables and indexes.

Pre-v1.0 schema changes are applied directly. No migrations, backfills, or compatibility handling for
prior data are provided; users recreate their storage when its schema no longer matches the code.

### Definition acquisition controls

`JobAcquisitionRequest.JobLimits` carries the parsed attribute limits keyed by job name. Definition metadata persists configured limits for monitoring and management on other servers.
Acquisition requests carry the worker's parsed limits; operational counters are stored separately. `immediate_job_definitions` stores `IsPaused`, `AcquisitionStatus`,
`NextEligibleAt`, and fixed-window start/count. Sliding windows use independent acquisition rows
indexed by job name and timestamp; deleting or purging invocation history must not erase rate capacity.
New definition timestamps and sliding acquisition timestamps are stored as UTC ticks to preserve
TimeSpan precision consistently across SQL providers. Concurrency is evaluated against active,
unexpired invocation leases, indexed by job name/state/expiry.

SQL providers take a write lock on the definition row when claiming a job and hold it through
claim/counter commit. Advisory acquisition filters and monitoring queries do not insert or update
definition rows or acquire write locks. In-memory uses its existing gate; Redis evaluates and claims
inside one Lua script, caching each definition status for the invocation and refreshing it after a claim.
All enabled limits must pass, including fair-queue paths. Failed claims roll back reservations.

`PauseJobAsync` prevents acquisition while allowing enqueue, trigger release, and recurring
materialization. `ResumeJobAsync` clears only the pause and reevaluates limits. Both are idempotent.
`GetJobAcquisitionStateAsync` reevaluates current eligibility using the supplied parsed limits; stored
status is a snapshot and must never be the sole authority for acquisition. Pause takes precedence over
rate and concurrency restrictions. Time-based `NextEligibleAt` is the latest blocking window boundary;
it is absent if pause or concurrency also blocks work. `ActiveCount` counts active, unexpired leases
even for definitions without a concurrency limit. `IsConcurrencyLimited` reports exhausted concurrency
independently of the primary restriction, including while paused.

The shared `JobAcquisitionEvaluator`, `JobAcquisitionRequest.LimitsFor`, and
`JobAcquisitionLimits.FixedWindowStart` are public provider helpers. Custom providers have the same
access as built-in providers; `Immediate.Jobs.Shared` grants no friend-assembly access.

### Redis keys

Every key starts with `{prefix}:` (the hash tag keeps a prefix in one cluster slot, so Lua scripts
may build keys from the root):

| Key | Type | Contents |
| --- | --- | --- |
| `job:{id}` | hash | Serialized `record` plus mutable fields (`state`, `due`, `dueScore`, `dueMember`, `attempt`, `worker`, `lease`, `group`, …). Reads overlay the mutable fields onto the record. |
| `jobs` | sorted set | Every job id, scored by creation time. |
| `state:{n}` / `completed:{n}` | set / sorted set | Ids by state; terminal ids by completion time. |
| `due:{queue}` | sorted set | Due-ordered members `dueTicks\|createdTicks\|id`. Stale members are removed lazily. |
| `leases` | sorted set | Active job ids by lease expiry. |
| `definitions:state:{name}` | hash | Definition pause, acquisition status, next eligible time, fixed-window start/count. |
| `definitions:acquisitions:{name}` | sorted set | Sliding acquisition history, ordered lexicographically by exact UTC ticks and a unique execution identity. |
| `definitions:leases:{name}` | sorted set | Active lease candidates by definition; refreshed on renewal and cleaned against invocation state during evaluation. |
| `executions:index:{id}` / `executions:data:{id}` | sorted set / hash | Execution history. |
| `recurring:*` | various | Schedules, due index, names, materialization de-duplication. |
| `server:{worker}` / `servers` | hash / sorted set | Heartbeats, including normalized tags. |
| `definition-metadata` | hash | All definition metadata keyed by stable job name. |
| `definition-catalog-version` | string | Version used to atomically reconcile a complete scoped catalogue. |
| `fair:*` | various | Fair-queue indexes, active counts, and cursors; see [fair queues](fair-queues.md). |

The payload lives only inside `record`. Lua must not re-encode it with `cjson` (key order and escaping
would change), so `UpdatePayloadAsync` rewrites the record in C# and swaps it with a compare-and-set
script.

## Adding or changing a storage member

1. Add the member to `IJobStorage` or `IJobGraphStorage`, with XML docs stating its exceptions.
2. Implement it in **every** provider, using that provider's idiom: in-memory, single-server
   (durable first, then primary), EF Core, LinqToDB, Redis (graph members excluded), plus
   `CapturingJobStorage` (forward to its inner store) and any test fakes such as
   `StorageCapabilityTests.QueueOnlyStorage`.
3. Add a `[LoggerMessage]` "called" method with a new event id in each provider's range (see
   [conventions](conventions.md#logging)).
4. Add conformance cases under `src/Immediate.Jobs.Testing/Storage` (see [testing](testing.md)).
5. Mention the change to custom providers in the pull request; it is a breaking change for them.
