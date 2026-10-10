# Storage

## Contract

Two interfaces in `src/Immediate.Jobs.Shared/Storage`:

- **`IJobStorage`**: queueing, acquisition (including fair-queue policies), leases, completion and
  failure, definition catalogues, recurring schedules, wait-for-trigger updates and triggers, queries, monitoring snapshots,
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
and all code-defined schedules. It inserts or updates supplied definitions and removes stored
definitions absent from that list. An empty list clears the catalogue.
Definition deletion never cascades to invocations, running leases, or execution history.

Definition names are case-insensitive identities and retain their readable spelling. In-memory
and single-server use `OrdinalIgnoreCase`; SQL Server inherits database collation; PostgreSQL uses
a nondeterministic ICU collation (`und-u-ks-level2`); SQLite uses `NOCASE` (ASCII-only database
comparison). EF tracking uses an ordinal case-insensitive key comparer. EF applications with custom
provider conventions can pass `definitionNameCollation` to `AddImmediateJobs`.
Definition names must not have leading or trailing whitespace: the job analyzer reports `IJOB0008`,
and storage rejects invalid manually supplied names before modifying the catalogue.

Concurrent startup snapshots commit atomically. In-memory uses its gate; EF Core takes an early write
lock on a singleton catalogue row. LinqToDB uses a serializable transaction and one SQL `MERGE` per
table on SQL Server and PostgreSQL 15 or later. The merge source queries reconcile obsolete records,
canonical names, and recurring progress in the database. Only SQLite reads a snapshot for client-side
reconciliation; it uses transactional upserts and deletes because
it does not support SQL `MERGE`. Serialization conflicts retry the complete LinqToDB snapshot.
Redis compares a version and applies metadata and
schedule changes in one Lua script, recomputing after a competing startup. Single-server delegates
metadata reads and reconciliation to durable storage, then refreshes the primary's code-defined
schedules. Pause and unchanged schedule progress survive startup.

Each application's list is authoritative for the catalogue. Concurrent applications with inconsistent
lists can remove or replace each other's definitions; the last successful reconciliation wins.

Custom providers must implement the catalogue operations. Relational users must add the definition metadata table
through their normal schema update process; EF Core also requires the singleton catalogue table. The
bootstrap helpers do not upgrade existing production databases.

## Providers

| Provider | Location | Concurrency idiom |
| --- | --- | --- |
| In-memory | `Shared/Storage/InMemoryJobStorage.cs` | One `Lock` (`_gate`) around every operation. Also the primary inside single-server mode. |
| Single-server | `Shared/Storage/SingleServerJobStorage.cs` | Mutations go to the durable store first, then to the in-memory primary. Acquisition is decided by the primary and mirrored with `IJobGraphStorage.AcquireJobsAsync`. Startup recovery reloads every job (in every state), its batches, and standalone continuation edges from the durable store into the primary. |
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
`immediate_fair_queue_groups`, and `immediate_job_definition_metadata`. EF Core also uses
`immediate_job_definition_catalog` to serialize catalogue updates.

- **EF Core**: applications call `modelBuilder.AddImmediateJobs()` and own their migrations. A schema
  change is a breaking change for every EF Core user, and needs a migration note in the pull request.
- **LinqToDB**: `CreateImmediateJobsSchemaAsync` creates tables for new databases only; existing
  databases need a manual additive update.

Prefer changes that fit the existing columns. For example, wait-for-trigger reuses `State` and
`Payload` instead of adding columns.

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
| `executions:index:{id}` / `executions:data:{id}` | sorted set / hash | Execution history. |
| `recurring:*` | various | Schedules, due index, names, materialization de-duplication. |
| `server:{worker}` / `servers` | hash / sorted set | Scheduler heartbeats. |
| `definition-metadata` | hash | All definition metadata keyed by stable job name. |
| `definition-catalog-version` | string | Version used to atomically reconcile a complete catalogue. |
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
