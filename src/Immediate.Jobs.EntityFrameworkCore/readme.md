# Immediate.Jobs.EntityFrameworkCore

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.EntityFrameworkCore.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.EntityFrameworkCore/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

Entity Framework Core storage for [Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/), validated with
PostgreSQL, SQLite, and SQL Server. The adapter supports durable jobs, recurring schedules, batches, continuations,
execution history, distributed leases, and fair acquisition.

## Installation

Install the core scheduler, this adapter, and the EF Core provider for your database:

```console
dotnet add package Immediate.Jobs
dotnet add package Immediate.Jobs.EntityFrameworkCore
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

Use `Microsoft.EntityFrameworkCore.Sqlite` or `Microsoft.EntityFrameworkCore.SqlServer` instead of Npgsql when
appropriate. This adapter deliberately does not reference a database-specific EF Core provider.

## Configuration

Register an `IDbContextFactory<TContext>`, select the database through its normal EF provider, and include the jobs model
in the application context:

```csharp
builder.Services.AddDbContextFactory<AppDbContext>(db =>
	db.UseNpgsql(connectionString));       // PostgreSQL
// db.UseSqlite(connectionString);       // SQLite
// db.UseSqlServer(connectionString);    // SQL Server

builder.Services.AddMyAppJobs(options =>
	options.UseEntityFrameworkCore<AppDbContext>());

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);
		modelBuilder.AddImmediateJobs(schema: "background"); // omit the schema for SQLite
	}
}
```

`AddMyAppJobs` is the generated registration method for an assembly named `MyApp`. Also register the matching generated
Immediate.Handlers method, `AddMyAppHandlers()`.

The application owns EF migrations. Add and apply a migration after calling `AddImmediateJobs`; use
`EnsureCreatedAsync` only for disposable development or test databases.

## Distributed execution

The provider can back the memory-primary durable single-server topology or distributed acquisition. Enable distributed
mode when multiple scheduler nodes share the same database:

```csharp
builder.Services.AddMyAppJobs(options =>
{
	options.UseEntityFrameworkCore<AppDbContext>();
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
	options.UseEntityFrameworkCore<AppDbContext>();
	options.UseDistributed();
	options.UseFairQueues();
});
```

The EF model includes the nullable `GroupId` column, the `(QueueName, State, GroupId)` index, and the
`immediate_fair_queue_groups` table. Add and deploy the corresponding migration before enabling fair queues on an
existing database. See the
[fair queues guide](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/docs/fair-queues.md) for semantics and
tradeoffs.

## Schema upgrades

Applications retaining execution history require the `immediate_job_executions` table. Apply the application migration
before deploying binaries that use execution history. During a mixed-version rollout, history remains best effort until
all scheduler nodes are upgraded.

Queue-aware dispatch uses `AcquireDueJobsAsync(JobAcquisitionRequest, ...)`. Custom storage wrappers must honor the
request's queue order, queue capacities, and per-job capacities.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Documentation](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
