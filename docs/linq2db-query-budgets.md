# LinqToDB command budgets

This review covers every public method on `LinqToDBJobStorage`, including the test-state loader.
Budgets count executed database commands, not network packets. Connection setup, transactions,
failed attempts, diagnostic queries and concurrency retries can add work. They describe the
implementation, not an API guarantee. Bulk inserts can split further at provider statement limits.

## Architectural choices

The in-memory implementation provides useful boundaries: separate ordinary and fair selection,
resolve definitions for a whole input set, traverse continuation frontiers, and finish member
transitions before propagating terminal outcomes. The EF Core improvements demonstrate how to
move those boundaries into SQL without dropping ownership checks.

LinqToDB has no change tracker or `SaveChanges` phase. Updating a detached object does nothing until
an explicit command executes. Consequently, guarded updates, arithmetic counters, existence
subqueries and multi-row inserts are preferable when a method does not need the previous row.
Telemetry normally needs only two updates; recurring materialization needs an update and insert.
Neither needs an initial read.

Snapshots reused during a workflow are explicitly scoped to its transaction. Batch counters are
accumulated in a local dictionary, then written once per changed batch with its original stamp.
Definition state is passed from reservation to refresh; there is no implicit identity map.
Waiting batch members are transitioned before skipped outcomes propagate, so later members cannot
be overwritten using stale snapshots. Cancellation follows the same rule.

Bulk job updates retain an `(id, concurrency stamp)` predicate for every observed row. Recovery,
cancellation and purge roll back if any required row changed. Bulk acquisition reads back only
the stamp assigned by its own update, returning successful claims in caller order. Predicates
are balanced and blocks contain at most 256 jobs. Every claim still takes the definition write
lock shared with pause and rechecks limits before committing ownership and execution history.

## Cost notation

- **H**: one query for sliding-window timestamps when configured, otherwise zero. Definition
  snapshots join metadata, usage counters and active counts in one command. Timestamp predicates
  use each definition's own window, grouping equal periods rather than fetching all history.
- **L**: zero to two queries evaluating one definition's sliding history and concurrency count.
- **E**: zero commands when an attempt never existed; otherwise one latest-execution lookup and
  possibly an insert for missing legacy history. Bulk paths check latest executions once per block.
- **I**: commands inserting missing synthetic executions. These are exceptional legacy repairs;
  individual inserts retain precise unique-key race verification.
- **D**: up to two reads of external job/batch parents and guarded stamp updates for nonterminal
  external parents. Parents already in the submitted graph need no database reads.
- **P**: terminal propagation. Each frontier of up to 256 parents needs an outgoing-child query.
  Each block of up to 256 children needs one job query and one incoming-edge query. Missing batch
  headers are loaded in blocks and reused within this transaction. Edge settlements and changed
  children are explicitly updated; each changed batch is written once for the entire propagation.
- **C**: one post-commit conditional cursor delete when terminal groups exist, otherwise zero.
- **B(rows)**: multi-row insert commands, with a maximum batch size of 256 and provider limits.

## Acquisition and definitions

Ordinary selection performs one joined definition snapshot query, H, and one candidate query per
selection pass. It never reads or writes fairness cursors. Candidate definition-counter bootstrap
uses one set query plus inserts for missing names, outside claim transactions to avoid nested
connection writes under SQLite's write lock.

Consecutive runs of at least three fresh standalone jobs under one unrestricted definition use
eight commands per block: unrestricted preflight, definition lock update/read, metadata recheck,
versioned job update, claimed-id readback, execution insert and definition refresh. Provider limits
may split the execution insert. A limited run falls back after one preflight per block. Limited,
retried, reclaimed and batch-member claims retain individual transactions: definition lock
update/read, metadata read, up to 2L, job update, E, execution insert, optional batch-start update,
optional sliding-history delete/insert and definition refresh. Refresh reuses the reserved state.

Fair selection uses one grouped-head query (including an ungrouped head) projecting per-head
inflight counts and optional cursor sequences, one total-inflight query, and one maximum-sequence
query when round robin is enabled. Group comparisons stay in SQL to preserve provider collations. Ungrouped inflight jobs count toward
the total. If no grouped head exists, ordinary selection reuses the evaluated names. Successful
fair claims re-rank durable state; this path intentionally costs more than ordinary acquisition.
Round-robin claims also read and insert/update their cursor atomically with job ownership.

| Public method | Successful-call boundary |
|---|---|
| `AcquireDueJobsAsync` | Selection passes, bootstrap and claims above. Empty queue capacity needs no commands. |
| `AcquireJobsAsync` | One candidate-set read, bootstrap and claims. Zero for no handles. Preserves caller order. |
| `RenewLeaseAsync` | One ownership-fenced job-table update; no metadata join. |
| `PauseJobAsync` | Metadata lookup and one update, using the claim lock. Missing counters add bootstrap. |
| `ResumeJobAsync` | Metadata lookup, bootstrap existence check, definition lock update/read, L and counter update. |
| `GetJobAcquisitionStateAsync` | One joined definition snapshot plus H. |
| `GetJobDefinitionsAsync` | One metadata query. |
| `MergeJobDefinitionsListAsync` | Provider-specific catalog reconciliation, optional removed-job cancellation, execution preloads, P and cursor cleanup. SQL Server/PostgreSQL retain SQL MERGE; SQLite reconciles two snapshots and writes differences. Removed jobs are all cancelled before propagation. |

## Query APIs

History unions durable executions with an absent latest legacy execution, then filters and pages
in SQL. Synthetic rows remain read-only. Status and graph queries project only the required
columns; job status combines edges with its header, and graph combines nodes with its batch header.
Independent monitoring collections stay separate to avoid Cartesian products.

| Public method | Commands |
|---|---|
| `GetMonitoringDefinitionsAsync` | Two queries: definitions and recurring configuration. |
| `GetMonitoringSnapshotAsync` | Four queries plus H: job counts, schedules, live servers and joined definition state. |
| `QueryJobsAsync` | One filtered/paged query joining current definition metadata. |
| `QueryNonCompletedJobsAsync` | One query. |
| `QueryJobExecutionsAsync` | One union query, including legacy synthesis, attempt filtering and paging. |
| `GetJobStatusAsync` | One job/edge query, excluding payload and context. |
| `GetBatchStatusAsync` | One header query. |
| `QueryBatchesAsync` | One filtered/paged header query. |
| `QueryBatchMembersAsync` | One filtered/paged projection, excluding payload and context. |
| `GetBatchGraphAsync` | One header/node query and one edge query when nodes exist. Missing and empty batches remain distinct. |
| `GetIncomingEdgesAsync` | One set query; zero for no handles. |

## Submission and workflow mutation

| Public method | Successful-call boundary |
|---|---|
| `EnqueueAsync` | One canonical-name query and one insert. |
| `EnqueueContinuationAsync` | One batched definition query, D, B(jobs) and B(edges). |
| `EnqueueBatchAsync` | Same, plus one batch-header insert. No definition query per member. |
| `AddBatchJobAsync` | Active-parent, batch and definition reads; job insert and batch update; optional one waiter query plus edge inserts and waiter updates for splicing. Waiter ids remain a SQL subquery. |
| `SetExecutionTelemetryAsync` | Two ownership-fenced updates normally. Missing legacy history adds job/history verification and a synthetic insert; an inactive existing execution causes rollback/retry. |
| `CompleteAsync` | Delegates to completion with no additions. One ownership/history snapshot, optional I, execution update, job update, P and C. |
| `CompleteWithContinuationsAsync` | Completion budget plus one definition query, optional waiter/batch reads, explicit additions and dependency updates before propagation. |
| `FailAsync` | Ownership/history snapshot, optional I, execution and job updates. Terminal failure adds P and C; scheduled retry does not propagate. |
| `CancelAsync` | One job read, E if active, execution update if active, job update, P and C. |
| `CancelBatchAsync` | Header and nonterminal-member reads. Each block: latest-execution preload if needed, I, execution update if active members exist and one versioned cancellation update. Then P and C. No per-member reads or cancellation updates. |
| `RetryAsync` | One eligible-job read, optional E, job update and one arithmetic batch-counter update for a failed batch member. Invalid-state versus missing diagnostics can add a query. |
| `UpdatePayloadAsync` | One narrow validation read and one stamp-guarded payload update. Retains .NET ordinal-ignore-case name validation. |
| `TryTriggerAsync` | Job read, optional waiting-batch check, at most one incoming-edge query, job update and any P/C for skips. |
| `TryTriggerBatchAsync` | Header read/update and waiting-member read; at most one incoming-edge query per block; explicit member updates followed by a single propagation of skipped members. |
| `PurgeJobsAsync` | One narrow terminal-job/version read, one version claim per block, and three set deletes for edges, executions and jobs when any qualify. Claims protect concurrent retry before dependent rows are removed. |
| `PurgeBatchesAsync` | One terminal-header read, guarded batch claims in id order, and four set deletes. Member ids remain a SQL subquery. |

## Recurring and lifecycle APIs

| Public method | Successful-call boundary |
|---|---|
| `UpsertRecurringAsync` | Existing code-defined schedule: one guarded update. Existing dynamic schedule: canonical-name query and update. New dynamic schedule adds an ownership check and insert. Unique-key races roll back and retry. |
| `RemoveRecurringAsync` | One guarded delete; a miss needs one diagnostic query. |
| `PauseRecurringAsync` | One update. |
| `ResumeRecurringAsync` | One guarded update; only a miss queries cancellation versus absence. |
| `GetDueRecurringAsync` | One filtered/paged query. |
| `MaterializeRecurringAsync` | One guarded schedule update, D, job insert and any dependency inserts. With no dependencies, two commands; stale/paused snapshots need only the update. Dedupe recovery uses an existence subquery in its update. |
| `InitializeAsync(false)` | Zero commands. |
| `InitializeAsync(true)` | Each nonempty block: active-job query, latest-execution preload when attempts exist, I, one execution update and one versioned job update. May finish with an empty read at an exact block boundary. |
| `HeartbeatAsync` | Expired-server delete and update; a new server adds an insert. Details serialize once. |
| `IsHealthyAsync` | One `SELECT 1` probe. |
| `DisposeAsync` | Zero commands. |
| `LoadPersistedJobState` | Test-only bulk inserts of batches, jobs, edges and schedules. No application-data reads. |

The command regression tests run on SQLite, PostgreSQL and SQL Server and cover ordinary claims,
caller ordering, telemetry ownership, recovery and cancellation across the 256-job boundary,
execution paging, recurring resume and competing schedule snapshots. The existing conformance
suite supplies graph, limits, fairness, replica and legacy-history behavior checks.
