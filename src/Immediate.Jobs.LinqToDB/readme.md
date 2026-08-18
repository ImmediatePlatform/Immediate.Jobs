# Immediate.Jobs.LinqToDB

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.LinqToDB.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.LinqToDB/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/configuring-storage-providers#linqtodb)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

LinqToDB storage for [Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/), validated with PostgreSQL,
SQLite, and SQL Server. The adapter supports durable jobs, recurring schedules, batches, continuations, execution
history, multiple worker processes, and fair queues between tenant groups.

## Installation

Install the core scheduler, this adapter, and the ADO.NET driver for your database:

```console
dotnet add package Immediate.Jobs --prerelease
dotnet add package Immediate.Jobs.LinqToDB --prerelease
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
	CancellationToken.None
);

builder.Services.AddMyAppHandlers();
builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage
		.UseLinqToDB(dataOptions, schema: "background")
		.UseSingleServer());
```

`AddMyAppJobs` is the generated registration method for an assembly named `MyApp`.

`CreateImmediateJobsSchemaAsync` creates the current tables and indexes for a fresh database. It runs only when the
application calls it; worker startup does not create the schema.

Named schemas are supported on PostgreSQL and SQL Server. SQLite has no server schemas and is normally embedded or
file-backed.

## Run one or several application instances

The example above uses `UseSingleServer()` and is valid only when exactly one application instance runs the
Immediate.Jobs worker. Jobs are selected from an in-process queue, while every change is also written to SQL so pending
work can be restored after a restart. Do not run two instances against the same database in this mode.

If the application runs multiple replicas, use `UseDistributed()` instead:

```csharp
builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage
		.UseLinqToDB(dataOptions, schema: "background")
		.UseDistributed());
```

In this mode every replica claims due jobs directly from the database. A claim expires after the configured lease period
(30 seconds by default), allowing another replica to claim and run the job again if the first process stops.

## Fair queues

Supply a reusable tenant or customer ID when enqueueing work, then enable fair queues:

```csharp
await scheduler.EnqueueAsync(payload, groupId: tenantId, cancellationToken);

builder.Services.AddMyAppJobs()
	.UseFairQueues()
	.ConfigureStorage(storage => storage
		.UseLinqToDB(dataOptions, schema: "background")
		.UseDistributed());
```

Fair queues rotate due jobs between tenant or customer groups so a large backlog from one group does not crowd out
quieter groups. They are supported in both single-server and distributed SQL modes. See the
[queues and fairness documentation](https://immediateplatform.dev/docs/Immediate.Jobs/queues-and-fairness) for the
available settings.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Storage-provider configuration](https://immediateplatform.dev/docs/Immediate.Jobs/configuring-storage-providers#linqtodb)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
