# Redis command budgets

This reviews every public method on `RedisJobStorage`. Budgets describe successful attempts, not
public API guarantees. They count client-issued Redis commands, including `EVAL`, rather than the
`redis.call` operations within a script. Connection establishment, script-cache negotiation, retries,
and transport behavior can add commands or packets. StackExchange.Redis pipelines independent calls,
so command counts and network round trips are different measurements.

All keys for a storage prefix share a cluster hash slot. Related reads and mutations use that slot's
scripts to preserve atomicity. Records remain serialized JSON plus mutable hash fields; these changes
add no keys or persisted fields and require no schema conversion.

## Acquisition and ownership

| Public method | Client commands and boundary |
|---|---|
| `AcquireDueJobsAsync` | Two sequential commands per attempt: one metadata `HMGET`, then one claim script. Zero for an empty set of requested job names. The claim script returns records, current queues, and mutable fields directly. Catalogue contention repeats the attempt. |
| `RenewLeaseAsync` | One ownership-fenced script. |
| `SetExecutionTelemetryAsync` | One ownership-fenced script, including legacy history materialization. |
| `CompleteAsync` | One script for job state, execution history, ownership, and scheduling/fairness indexes. |
| `FailAsync` | One script, including retry scheduling or terminal indexing and execution history. |
| `CancelAsync` | One script; distinguishes missing jobs from terminal jobs. |
| `RetryAsync` | One script; validates eligibility and updates history and indexes together. |

Previously a successful acquisition also issued one hash read per claimed job, pipelined after the
claim. Returning the records from the script removes that entire dependent stage and prevents a
concurrent cancellation or lease reclamation from replacing the returned claim snapshot. Ordinary
acquisition skips the full definition-catalogue read used for fair ranking.

Definition limits still need .NET time calculations and a metadata recheck inside the claim script.
No definition, eligibility, ownership, or fairness cache survives the public call. Candidate selection,
lease reclamation, and fair ranking within the script retain their existing collection-sized costs;
a smaller network budget does not make that server work constant-time.

## Queries and monitoring

| Public method | Client commands and boundary |
|---|---|
| `GetJobDefinitionsAsync` | One `HVALS`; names are sorted using the shared .NET comparer. |
| `GetMonitoringDefinitionsAsync` | One catalogue snapshot script, including recurring schedules. |
| `GetMonitoringSnapshotAsync` | One script for counts, definitions, schedules, and live servers; one additional script to evaluate definition limits when definitions exist. A catalogue-version mismatch restarts the snapshot. |
| `QueryJobsAsync` | One script for an exact handle or an unfiltered page of up to 1,000 records. Filtered queries use one script per window of up to 256 candidates until enough matches or the end of the index. |
| `QueryNonCompletedJobsAsync` | One script unions nonterminal state indexes, checks current state/name, and returns matching records with current definition queues. |
| `QueryJobExecutionsAsync` | One script checks the job, merges an absent latest legacy attempt into paging, selects attempts, and reads their fields. Synthetic record construction stays in C#. Missing jobs require no extra diagnostic read. |
| `GetJobStatusAsync` | One job/definition read script. It currently reads the complete record, including payload/context. |

Job pages previously needed an index read, one pipelined hash read per job, and a final definition
metadata read. A single script now projects each window and resolves its queues with a script-local
cache. State, exact normalized name, and precise creation-time predicates run before record
projection. Remaining string predicates and final matching run in C# to retain ordinal and
ordinal-ignore-case behavior. Paging counts matches, not candidates; empty windows do not end a
selective query.

Filtered queries still scan the global creation index. No new definition-job index is introduced.
Queue/search filters can still materialize unrelated candidates within each bounded window. A highly
selective query over a large history therefore remains proportional to the scanned candidates. The
noncompleted query returns an unbounded result by contract and its script examines the nonterminal
state union; very large live queues can still block Redis while that script executes.

Monitoring previously combined per-state count commands, a schedule-name read and one hash read per
schedule, multiple server-index operations and one hash read per server, plus definition/state reads.
Its first script now captures the related collections together. Stale-server removal and live-server
selection are atomic with respect to heartbeats. Definition status evaluation happens in the second
script and can observe intervening job transitions; the overall monitoring response is not a single
atomic snapshot of job counts and acquisition counters.

## Catalogue and controls

| Public method | Client commands and boundary |
|---|---|
| `MergeJobDefinitionsListAsync` | Two per attempt: a version/definition/schedule snapshot, then the version-guarded reconciliation script. Contention recomputes from a new snapshot. |
| `PauseJobAsync` | One guarded script. |
| `ResumeJobAsync` | Two per attempt: definition metadata and a metadata-guarded state evaluation script. |
| `GetJobAcquisitionStateAsync` | The same two commands without changing pause state. |
| `EnqueueAsync` | One script canonicalizes the supplied definition name and inserts the invocation and indexes. |
| `UpdatePayloadAsync` | Two per attempt, at most five attempts: a validation projection and a record/state-guarded update script. |
| `TryTriggerAsync` | One script validates the target and releases waiting work with its due/state/fairness indexes. |

Catalogue snapshots previously read the version, definitions, recurring names, and schedule hashes
in separate stages. Reading them together avoids inconsistent inputs and per-schedule commands.
Reconciliation still scans jobs/schedules when removing definitions and maintains cursors within its
atomic commit script. That work is not bounded by the network command count.

Payload updates deliberately retain the C# ordinal-ignore-case validation and JSON rewrite, rather
than introducing another persisted payload field or implementing .NET comparison behavior in Lua.
The guarded update prevents a concurrent trigger or parameter edit from being overwritten. Opaque
payload and context strings are preserved by all record projections.

## Recurring schedules and lifecycle

| Public method | Client commands and boundary |
|---|---|
| `UpsertRecurringAsync` | One script, including definition canonicalization and ownership/progress checks. |
| `RemoveRecurringAsync` | One script; distinguishes missing schedules from code-owned schedules. |
| `PauseRecurringAsync` | One script updating pause state and due indexes. |
| `ResumeRecurringAsync` | One script, rejecting cancelled schedules. |
| `GetDueRecurringAsync` | One script, or zero when no job names are eligible. It filters before limiting and returns schedule fields in the same script. |
| `MaterializeRecurringAsync` | One script guards the schedule snapshot, deduplicates the occurrence, inserts the invocation, and advances progress. |
| `PurgeJobsAsync` | One script per terminal state per block of at most 256 selected entries. Four commands when all four terminal indexes have fewer than 256 expired entries. A full block needs another pass, which may be empty. |
| `InitializeAsync(false)` | One `PING`. |
| `InitializeAsync(true)` | One interruption script per block of up to 1,000 active jobs, followed by an empty pass. The script itself proves connectivity, so there is no preceding `PING`. |
| `HeartbeatAsync` | One script updates the server hash, TTL, and liveness index. |
| `IsHealthyAsync` | One `PING`. |
| `DisposeAsync` | Zero; DI owns the multiplexer. |

Recurring selection previously returned identifiers and then read each schedule hash in another
pipeline stage. It now projects schedules before the script finishes, retaining their due ordering
and avoiding races with pause, removal, or rescheduling between selection and projection.

Purge previously selected up to 256 identifiers and then awaited a separate deletion script for each
one. Selection, state checks, and deletion now share a bounded script. It removes invocation,
execution, completion, state, and recurring-deduplication entries together, and removes a dedupe entry
only if it still belongs to the purged invocation. Retention remains strict: entries exactly at the
cutoff are preserved. Stale completion-index entries count toward a block's bounded work.
