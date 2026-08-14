# Job monitor API direction

## Decision

`JobMonitor` is the single public-facing, read-only monitoring API. It covers jobs, retained
executions, batches, recurring schedules, scheduler nodes, and aggregate state. Applications should
normally inject the concrete `JobMonitor`; `IJobMonitor` exists as the same contract for consumers
that prefer an interface as a testing seam.

`IJobStorage` remains public because storage providers implement it, but it is a provider and
scheduler SPI rather than the recommended application API. Monitoring code outside the runtime
should not need to resolve it directly.

## API shape

All monitoring reads belong on `JobMonitor` rather than several narrowly divided monitor services.
The unified API includes:

- aggregate snapshots, including detected storage capabilities;
- job queries and definition-enriched status;
- retained execution queries;
- batch queries, status, members, and dependency graphs; and
- recurring schedule and scheduler-node views exposed by the aggregate snapshot.

Batch methods perform the graph capability check when called. The check is inexpensive and keeping it
at the operation boundary is simpler than conditionally registering or exposing a separate batch
monitor. A provider without graph support remains fully usable for non-batch monitoring.

The concrete class intentionally owns this application-facing behavior. It can enrich provider data
with generated job definitions, normalize results across providers, and centralize capability checks.
The interface mirrors the public surface so a consumer can substitute a fake, but documentation and
examples should lead with `JobMonitor`.

## Storage and command boundary

Do not proxy scheduler mechanics through `JobMonitor`. Initialization, enqueue/acquire, telemetry
writes, leases, completion/failure, heartbeat writes, health checks, and retention purge remain
runtime or provider responsibilities on `IJobStorage`.

Cancel, retry, delete, recurring controls, and batch mutations are user-level commands rather than
monitoring. If a general programmatic command API is needed, add a unified `JobManager` separately;
do not weaken the read-only meaning of `JobMonitor`.

The dashboard should consume `JobMonitor` for reads and use storage only for commands or streaming
loops that have not yet gained an application-level abstraction. This makes the shipped dashboard the
reference consumer and prevents persistence details from leaking into new read paths.
