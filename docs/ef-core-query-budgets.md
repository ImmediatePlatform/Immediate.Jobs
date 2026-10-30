# EF Core query budgets

This is a review of all public methods on `EntityFrameworkCoreJobStorage`, including its test-state
loader. These are implementation budgets for successful calls, not public API guarantees. They
count executed database commands, including immediate updates/deletes, rather than network packets.
Connection setup, transaction begin/commit, failed attempts, race verification and retries add work.
EF Core and the provider determine how many commands a `SaveChangesAsync` flush needs; SQLite often
issues individual row commands where PostgreSQL and SQL Server can batch them.

The goals are to bound reads independently of graph width where possible, avoid materializing unused
data, and preserve ownership fencing, workflow consistency and definition-lock coordination.

## Notation

- **S**: all commands emitted by one `SaveChangesAsync` flush. It can be zero. When an operation has
  multiple flushes, each has its own S. Inserts/deletes of many rows can still require many commands.
- **H**: one timestamp-history query when a sliding-window limit is configured; otherwise zero.
- **L**: zero to two queries for a definition's sliding history and limited active count. A normal
  claim evaluates limits before and after persistence, while holding its definition lock.
- **E**: zero or one latest-execution read, omitted if there is no attempt or the entity is already
  tracked. Bulk operations preload the relevant executions once per block of up to 1,000 jobs.
- **D**: zero to two reads of external job and batch parents during graph insertion.
- **P**: terminal graph propagation. Each frontier of up to 1,000 parents needs one outgoing-edge
  query; each block of up to 1,000 affected children needs one child query and one incoming-edge query.
  Missing batch headers are loaded in blocks, then reused from the identity map. Already loaded
  batches incur no further reads. Skipped descendants and completed batches form subsequent frontiers.
- **C**: one post-commit conditional cursor delete if terminal jobs have group identifiers; otherwise
  zero. It checks that no live jobs remain in the cursor's queue/group in the delete itself.

## Acquisition

Ordinary acquisition never performs fair ranking or round-robin cursor reads/updates. With a fair
policy but no eligible grouped work, the ordinary selector reuses the already evaluated names.

A selection pass needs `1 + H` commands to evaluate all requested definitions together, then one
candidate query. Candidate-definition bootstrap is one read for all names plus S for missing rows.
It runs outside claim transactions so SQLite does not deadlock on a nested connection's write lock.

Consecutive runs of at least three fresh standalone jobs under one unrestricted definition can use a
bulk claim. A block is bounded to 256 candidates, preserving request order and parameter limits. The
block needs six fixed commands plus S: an eligibility preflight, a definition-lock update and read,
a metadata recheck, a version-guarded job update, and a read of the versions actually claimed. The
flush inserts history and any changed definition status. Job ownership and history commit together.
The ownership predicate is balanced to avoid deep SQL expression trees.

Limited definitions, retried/reclaimed jobs and batch members use individual claim transactions.
Each accepted claim needs three fixed commands for locking and reading its definition and metadata,
`2L`, E, an optional batch-header read, an optional expired-history delete, and up to two S flushes.
The refresh reuses the tracked definition instead of querying it again. A failed unrestricted
preflight adds one read before this fallback, once per qualifying run rather than once per job.

Fair selection needs two reads for heads and grouped inflight counts, plus two cursor reads when
round robin is enabled. Those counts include ungrouped active jobs in the total. A round-robin claim
adds one cursor-row read to the individual claim and persists the cursor in the same transaction.
Selection may repeat after successful or contended claims, so fair acquisition is not constant-cost
per batch. It intentionally re-ranks against current durable state.

| Public method | Command budget and boundary |
|---|---|
| `AcquireDueJobsAsync` | Selection passes plus bootstrap and claims described above. No commands for an empty queue request. |
| `AcquireJobsAsync` | One ordered candidate-set read, then bootstrap and claims. Zero for an empty handle set. Preserves the caller's ordering. |
| `RenewLeaseAsync` | One ownership-fenced update, with a locally generated concurrency stamp. |

## Reads and definition controls

Metadata, definition counters and active counts are projected in one query. Sliding timestamps are
projected separately, filtered in SQL using each definition's own window. Grouping equal windows
bounds the number of queries without fetching short-window or unlimited definitions' obsolete
history merely because another definition has a long window.

Related read projections use a single query where that is safe. Independent collections remain
separate to avoid cross products: a graph fetch combines its header with nodes, then reads edges;
monitoring reads counts, schedules and servers separately. History uses a union of persisted rows
and an absent latest legacy execution, applying filtering and paging in SQL. Both sides explicitly
project the same scalar types, including the stored short execution-state representation.

| Public method | Command budget and boundary |
|---|---|
| `GetJobDefinitionsAsync` | One metadata query. |
| `GetJobAcquisitionStateAsync` | `1 + H`; a missing definition is detected by the same metadata query. |
| `GetMonitoringDefinitionsAsync` | Two queries: definitions and recurring configuration. |
| `GetMonitoringSnapshotAsync` | `4 + H`: job counts, recurring status, live servers, joined definition state, and optional timestamp history. |
| `QueryJobsAsync` | One filtered/paged query joining current definition metadata. |
| `QueryNonCompletedJobsAsync` | One query. |
| `QueryJobExecutionsAsync` | One query including synthetic history; missing jobs return no rows. |
| `GetJobStatusAsync` | One narrow job/edge projection; does not fetch payload or context. |
| `GetBatchStatusAsync` | One header query. |
| `QueryBatchesAsync` | One header query. |
| `QueryBatchMembersAsync` | One filtered/paged projection. |
| `GetBatchGraphAsync` | One header/node query; one additional edge query when nodes exist. |
| `GetIncomingEdgesAsync` | One edge-set query, or zero for an empty handle collection. |
| `PauseJobAsync` | Two commands when the counter row exists. Missing counters require bootstrap before retrying the update. The update takes the same row lock used by claims. |
| `ResumeJobAsync` | Four fixed commands, L and S for an existing counter row. Missing counters add bootstrap work. The lock/read/evaluation is retained to preserve persisted limits. |

## Submission, ownership and workflows

All mutation retries use fresh contexts. Execution-history preloads and tracked batch reuse are
scoped to the operation's transaction; there is no cache of eligibility or ownership across calls.
An owned job and its latest execution are fetched together before telemetry or outcome mutation.

| Public method | Command budget and boundary |
|---|---|
| `EnqueueAsync` | One canonical-definition lookup plus S. |
| `EnqueueContinuationAsync` | One batched definition lookup, D and S. |
| `EnqueueBatchAsync` | One batched definition lookup, D and S, independent of submitted job count for reads. |
| `AddBatchJobAsync` | Three reads of active parent, batch and definition; one more waiter query for splicing, then S. The waiter query uses an existence subquery rather than fetching ids first. |
| `SetExecutionTelemetryAsync` | One joined ownership/history read plus S. Missing legacy history is inserted from that same snapshot. |
| `CompleteAsync` | Delegates to completion with no buffered additions: one joined ownership/history read, P, S and C. |
| `CompleteWithContinuationsAsync` | Completion budget plus one definition lookup for additions, optional waiter and batch reads, and an extra S flush before querying the newly inserted graph. |
| `FailAsync` | With a retry time: one joined ownership/history read and S. Terminal failure also needs P and C. |
| `CancelAsync` | One job read, E if active, P, S and C. State validation still distinguishes missing from terminal jobs. |
| `CancelBatchAsync` | Two reads for the header and nonterminal members, batched execution preloads for active members, P, S and C. All members are cancelled before graph propagation starts. |
| `RetryAsync` | One eligible-job read, E, an optional failed-member batch read, and S. Invalid-state versus missing diagnostics can add a read. |
| `UpdatePayloadAsync` | One validation projection and one stamp-guarded update. Retains ordinal-ignore-case job-name validation rather than substituting engine-specific collation semantics. |
| `TryTriggerAsync` | One job read, an optional waiting-batch check, at most one incoming-edge read when dependencies have settled, S and any P/C needed for a skipped continuation. |
| `TryTriggerBatchAsync` | Two reads for header and waiting members, at most one batched incoming-edge read, S and any P/C needed for skips. No dependency read per member. |
| `PurgeJobsAsync` | One terminal-job read, one set-based edge delete when jobs exist, and S. Tracked job stamps protect against concurrent retry and roll back edge deletion on a lost race. |
| `PurgeBatchesAsync` | One terminal-header read, one set-based edge delete when batches exist, and S. Member ids remain a SQL subquery; batch stamps protect concurrent retries. |
| `MergeJobDefinitionsListAsync` | Five fixed commands for existing catalog bootstrap, catalog lock, definitions, schedules and cursor cleanup; an optional removed-job read, batched history preloads, P and S. Catalog creation adds an insert. |

## Recurring schedules and lifecycle

| Public method | Command budget and boundary |
|---|---|
| `UpsertRecurringAsync` | Existing code-defined schedule: one guarded update. Existing dynamic schedule: canonical-name lookup plus update. New dynamic schedule: lookup, attempted update, ownership check and S. Insert races add verification/retry work. |
| `RemoveRecurringAsync` | One guarded delete on success; a miss needs one diagnostic query. |
| `PauseRecurringAsync` | One update on success; a missing schedule is reported from its affected-row count. |
| `ResumeRecurringAsync` | One guarded update on success; a miss needs one query to distinguish cancellation from absence. |
| `GetDueRecurringAsync` | One filtered/paged query. |
| `MaterializeRecurringAsync` | One schedule read, D and S. Rejected schedule snapshots need only the read. Dedupe recovery after a failed insert uses one guarded update containing an existence check. |
| `InitializeAsync(false)` | Zero database commands; validates the EF model locally. |
| `InitializeAsync(true)` | Each nonempty block of up to 1,000 active jobs: four commands plus S only for missing synthetic executions. An extra empty read is possible at a block boundary. History and jobs are updated together, and changed job versions roll the transaction back. |
| `HeartbeatAsync` | Existing server: two commands (expired-server cleanup and update). New server: three, including insertion. |
| `IsHealthyAsync` | A provider connection probe; do not assume it issues an intercepted SQL command. |
| `DisposeAsync` | Zero database commands. |
| `LoadPersistedJobState` | Test-only loader: S for jobs, batches, edges and schedules, with no application-data reads. |

## Deliberately retained costs

Transactions, definition locks and stamp checks are not removed to achieve a smaller command count.
A fresh database read after a contested claim or a retry is necessary to distinguish expected races
from genuine provider errors. Non-fair selection does not require fairness state; post-commit cleanup
can still remove cursors left by earlier fair usage of the same groups. Command batching does not
make graph mutation or execution-history persistence free: changed rows still have to be written.
