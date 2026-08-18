# Immediate.Jobs.LinqToDB

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.LinqToDB.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.LinqToDB/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

LinqToDB storage for [Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/), validated with PostgreSQL,
SQLite, and SQL Server. The adapter supports durable jobs, recurring schedules, batches, continuations, execution
history, distributed leases, and fair acquisition.

## Installation

Install the core scheduler, this adapter, and the ADO.NET driver for your database:

```console
dotnet add package Immediate.Jobs
dotnet add package Immediate.Jobs.LinqToDB
dotnet add package Npgsql
```

Use `Microsoft.Data.Sqlite` or `Microsoft.Data.SqlClient` instead of Npgsql when appropriate. This adapter deliberately
does not carry a database driver.

## Configuration

Configure and reuse an immutable `DataOptions`, then pass it to schema bootstrap and registration:

```csharp
var dataOptions = new DataOptions().UsePostgreSQL(connectionString);
// new DataOptions().UseSQLite(connectionString);
// new DataOptions().UseSqlServer(connectionString);

await dataOptions.CreateImmediateJobsSchemaAsync(
	schema: "background", // must be null for SQLite
	cancellationToken);

builder.Services.AddMyAppJobs(options =>
	options.UseLinqToDB(dataOptions, schema: "background"));
```

`AddMyAppJobs` is the generated registration method for an assembly named `MyApp`. Also register the matching generated
Immediate.Handlers method, `AddMyAppHandlers()`.

`CreateImmediateJobsSchemaAsync` is an explicit, idempotent bootstrap helper for a fresh database. It is never called by
`InitializeAsync` and is not a production migration system. Applications own upgrades to existing schemas.

Named schemas are supported on PostgreSQL and SQL Server. SQLite has no server schemas and is normally embedded or
file-backed.

## Distributed execution

The provider can back the memory-primary durable single-server topology or distributed acquisition. Enable distributed
mode when multiple scheduler nodes share the same database:

```csharp
builder.Services.AddMyAppJobs(options =>
{
	options.UseLinqToDB(dataOptions, schema: "background");
	options.UseDistributed();
});
```

Distributed mode uses provider leases. If a process dies, its lease expires and another node can acquire the
invocation.

## Fair queues

Supply a reusable group ID when enqueueing work, then opt into fair acquisition:

```csharp
await scheduler.EnqueueAsync(payload, groupId: tenantId, cancellationToken);

builder.Services.AddMyAppJobs(options =>
{
	options.UseLinqToDB(dataOptions, schema: "background");
	options.UseDistributed();
	options.UseFairQueues();
});
```

Schema bootstrap creates the nullable `GroupId` column, the `(QueueName, State, GroupId)` index, and the
`immediate_fair_queue_groups` table for new databases. Existing databases need the equivalent additive upgrade before
fair queues are enabled. See the
[fair queues guide](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/docs/fair-queues.md) for semantics and
tradeoffs.

## Schema upgrades

Applications retaining execution history require the `immediate_job_executions` table. Apply the application-owned
upgrade before deploying binaries that use execution history. During a mixed-version rollout, history remains best
effort until all scheduler nodes are upgraded.

Queue-aware dispatch uses `AcquireDueJobsAsync(JobAcquisitionRequest, ...)`. Custom storage wrappers must honor the
request's queue order, queue capacities, and per-job capacities.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Documentation](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
