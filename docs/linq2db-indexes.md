# LinqToDB indexes and execution plans

Command counts alone do not describe database work. Inspect the SQL emitted by LinqToDB, with its
actual parameter types, and the provider's execution plan. Use representative cardinalities,
timestamp ties, expired leases and skewed execution histories. Refresh statistics after bulk seed
loads before attributing a scan to a missing index.

## Ordered pages

The schema helper supplies these paging keys:

| Read | Index columns, in key order |
| --- | --- |
| Jobs, newest first | `CreatedAt DESC, Id ASC` |
| Jobs filtered by state | `State ASC, CreatedAt DESC, Id ASC` |
| Batch members, oldest first | `BatchHandle ASC, CreatedAt ASC, Id ASC` |
| Batches, newest first | `CreatedAt DESC, Id ASC` |

Include the handle tie-breaker and match both sort directions. Reversing an all-ascending index
also reverses the handle order; it cannot supply `CreatedAt DESC, Id ASC` without additional sorting.
The state and member indexes also serve reads using their leading state or batch key.

Page filtered keys first, then join only those keys to complete records and definition metadata.
Keep both steps in one SQL command and repeat the order on the outer query. This avoids fetching
payloads for every skipped row. SQL Server can otherwise prefer a clustered scan and sort even
when the paging index exists, because looking up full rows before applying the offset costs too much.
Queue and name filters must remain inside the key page; moving them outside changes page membership.

Filters without a matching leading index key can still require residual checks or sorting. Do not
add an index for every filter combination without measuring its selectivity and write cost. Offset
paging still reads the skipped keys; these indexes do not make arbitrary offsets constant-time.

## SQLite expressions and collations

LinqToDB compares SQLite `DateTimeOffset` values using
`strftime('%Y-%m-%d %H:%M:%f', column)`. A plain timestamp key cannot seek this expression's range.
For active-lease counts the helper therefore adds:

```sql
CREATE INDEX "IX_immediate_jobs_JobName_State_LeaseExpiresAtUtc"
ON "immediate_jobs"
   ("JobName", "State", strftime('%Y-%m-%d %H:%M:%f', "LeaseExpiresAt"));
```

The expression matches the generated SQL and normalizes offsets before comparing timestamps.
Replacing the predicate with a raw string comparison would change the meaning of non-UTC values.
Verify that `EXPLAIN QUERY PLAN` shows `(JobName=? AND State=? AND <expr>>?)`, rather than just
`(JobName=? AND State=?)`. The ordinary timestamp index remains available for other query shapes.
PostgreSQL and SQL Server use their existing `(JobName, State, LeaseExpiresAt)` index for these
counts and do not need the expression index.

Metadata names use SQLite `NOCASE`, while the definition counter table's primary key uses the
default collation. The metadata-to-counter join consequently needs
`IX_immediate_job_definitions_JobName_NoCase` on `JobName COLLATE NOCASE`. Its plan should show an
indexed counter lookup, rather than a counter-table scan for each metadata row. Keep the index
nonunique so this optimization does not introduce a new persisted uniqueness constraint.

## Execution history

The primary key `(JobHandle, Attempt)` already supports a job's newest attempts by scanning
backwards. A duplicate descending index does not resolve the expensive global sort introduced by
unioning persisted history with a possible synthetic legacy attempt.

Apply an optional attempt filter to both branches. Limit the ordered persisted branch to
`Skip + Take` rows before the union, then order and page the result. The synthetic branch contributes
at most one row, so discarded persisted rows cannot enter the requested page. This bounds the
outer sort and lets the existing primary key supply the persisted order. Use a nullable scalar
primary-key lookup for the synthetic row's existence check: SQL Server can decorrelate an anti join
into a scan of all executions for that job, even though both key columns appear in the SQL predicate.

Check actual rows and buffers, not just the presence of an index operator. For SQL Server, also
inspect sort warnings and tempdb spills. Small histories can remain dominated by SQL translation,
materialization and allocations; indexes cannot remove those client costs. Bounding the persisted
branch also adds query-composition work when few rows exist, so compare short and long histories
when changing this query.

## Deployment and write costs

`CreateImmediateJobsSchemaAsync` defines fresh schemas and creates indexes idempotently. It does
not manage production upgrades or remove obsolete indexes. Applications retaining an existing
schema must review its physical indexes against the helper and provision the required definitions
through their own deployment process. Fresh schemas use the composite state and batch-member
paging indexes instead of the older `State, CreatedAt` and `BatchHandle` indexes.

Keep acquisition, due-date, purge, recurring and server-expiry indexes: paging indexes serve different
predicates. Additional indexes consume storage and add insert/delete work. State and lease indexes
also add maintenance during transitions and renewal. Avoid including payloads or contexts merely
to obtain a covering index; fetching only the final page's rows keeps these keys narrow.

The distributed provider executes these reads against the database. Single-server mode serves job,
batch and member pages from its in-memory mirror, but reads execution history and durable monitoring
from the database. History and monitoring improvements therefore benefit both modes. Paging indexes
also help single-server startup restore the mirror. Additional durable write costs apply to both modes.
