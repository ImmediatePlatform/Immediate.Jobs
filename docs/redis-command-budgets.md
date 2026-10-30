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
| `AcquireDueJobsAsync` | One claim script with warm definition metadata. A cold/incomplete cache adds one `HMGET`. A stale snapshot adds a rejected script and metadata refresh before retrying. Zero for an empty set of requested job names. The claim script returns the claimed records directly. |
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

Parsed definition metadata and limits are cached per storage instance. Successful local catalogue
reconciliation primes this cache; missing definitions are never cached. Every acquisition/control
script compares the exact serialized metadata before changing state. A peer update therefore causes
a refresh and retry, while pause state, time windows, ownership, and eligibility remain authoritative
in Redis. Enqueue also checks the metadata and falls back to server-side canonicalization on a miss.

Candidate heads and empty indexes are cached only within one atomic claim script. Claims invalidate
the selected head; definition-limit changes are reevaluated before another candidate can be selected.
Active counts are checked against authoritative job state once per definition in the script and
incremented for its new unexpired claims. Unlimited definitions need no per-claim status reevaluation.
Fair ranking still considers current inflight counts, cursor turns, and all group heads after each
claim. No invocation, ownership, eligibility, or fairness cache survives the script.

Sliding-window evaluation prunes expired entries, counts with `ZCARD`, and reads only the blocking
rank when limited. It does not materialize the entire retained log for each claim. Lease reclamation
and fair inflight discovery retain their collection-sized costs; a smaller network budget does not
make that server work constant-time.

## Queries and monitoring

| Public method | Client commands and boundary |
|---|---|
| `GetJobDefinitionsAsync` | One `HVALS`; names are sorted using the shared .NET comparer. |
| `GetMonitoringDefinitionsAsync` | One catalogue snapshot script, including recurring schedules. |
| `GetMonitoringSnapshotAsync` | One script for counts, definitions, schedules, and live servers; one additional script to evaluate definition limits when definitions exist. A catalogue-version mismatch restarts the snapshot. |
| `QueryJobsAsync` | One script for an exact handle or an unfiltered page of up to 1,000 records. Filtered queries use one script per window of up to 256 candidates until enough matches or the end of the index. |
| `QueryNonCompletedJobsAsync` | One script unions nonterminal state indexes, checks current state/name, and returns matching records with current definition queues. |
| `QueryJobExecutionsAsync` | One script checks the job, merges an absent latest legacy attempt into paging, selects attempts, and reads their fields. Synthetic record construction stays in C#. Missing jobs require no extra diagnostic read. |
| `GetJobStatusAsync` | One job/definition read script. The serialized record is fetched, but only its header is deserialized; payload and context strings are skipped. |

Job pages previously needed an index read, one pipelined hash read per job, and a final definition
metadata read. A single script now projects each window and resolves its queues with a script-local
cache. State, exact normalized name, and precise creation-time predicates run before record
projection. Unfiltered pages skip that preliminary hash read, and record projection obtains its
normalized definition name in the same `HMGET` as mutable fields. Acquisitions supply their already
verified queue directly. Remaining string predicates and final matching run in C# to retain ordinal
and ordinal-ignore-case behavior. JSON deserialization consumes Redis UTF-8 bytes directly instead
of creating intermediate UTF-16 JSON strings. Paging counts matches, not candidates; empty windows do not end a
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
| `ResumeJobAsync` | One metadata-guarded state evaluation script with warm configuration; a cold cache adds a metadata read, and a rejected snapshot adds a refresh/retry. |
| `GetJobAcquisitionStateAsync` | The same guarded configuration-cache path without changing pause state. |
| `EnqueueAsync` | One script checks definition metadata and inserts the invocation and indexes. Matching cached metadata permits storing an already canonicalized record without a Lua JSON rewrite; misses use the server-side canonicalizer. |
| `UpdatePayloadAsync` | One script validates identity and waiting state and replaces the opaque payload in the serialized record atomically. |
| `TryTriggerAsync` | One script validates the target and releases waiting work with its due/state/fairness indexes. |

Catalogue snapshots previously read the version, definitions, recurring names, and schedule hashes
in separate stages. Reading them together avoids inconsistent inputs and per-schedule commands.
Reconciliation still scans jobs/schedules when removing definitions and maintains cursors within its
atomic commit script. That work is not bounded by the network command count.

Payload updates normalize the expected name in C# using the existing ordinal-ignore-case key
normalizer, preserving the ASCII/non-ASCII distinction even when invariant uppercase maps a Unicode
character to ASCII. Lua compares that value with the stored normalized job name and checks waiting
state before changing the record. This removes the read/compare-and-swap retry loop without adding a
persisted payload field. Payload and context remain opaque strings, including nested JSON numbers
and escape sequences. Persisted execution-history reads fetch the full job record only when a
synthetic latest execution must actually be returned.

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
