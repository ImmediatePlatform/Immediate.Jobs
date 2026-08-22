# Immediate.Jobs.EntityFrameworkCore

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.EntityFrameworkCore.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.EntityFrameworkCore/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/configuring-storage-providers#entity-framework-core)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

Entity Framework Core storage for [Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/), validated with
PostgreSQL, SQLite, and SQL Server. The adapter supports durable jobs, recurring schedules, batches, continuations,
execution history, multiple worker processes, and fair queues between tenant groups.

## Installation

Install the core scheduler, this adapter, and the EF Core provider for your database:

```console
dotnet add package Immediate.Jobs --prerelease
dotnet add package Immediate.Jobs.EntityFrameworkCore --prerelease
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

Use `Microsoft.EntityFrameworkCore.Sqlite` or `Microsoft.EntityFrameworkCore.SqlServer` instead of Npgsql when
appropriate. This adapter deliberately does not reference a database-specific EF Core provider.

## Configuration

Prefer a dedicated, application-owned `JobsDbContext` so the jobs schema stays separate from the application's business
model. The contexts may still use the same physical database:

```csharp
builder.Services.AddDbContext<AppDbContext>(db =>
	db.UseNpgsql(appConnectionString));

builder.Services.AddDbContextFactory<JobsDbContext>(db =>
	db.UseNpgsql(jobsConnectionString));       // PostgreSQL
// db.UseSqlite(jobsConnectionString);       // SQLite
// db.UseSqlServer(jobsConnectionString);    // SQL Server

builder.Services.AddMyAppHandlers();
builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage
		.UseEntityFrameworkCore<JobsDbContext>()
		.UseSingleServer());

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
	public DbSet<Order> Orders => Set<Order>();
}

public sealed class Order
{
	public Guid Id { get; set; }
}

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
{
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);
		modelBuilder.AddImmediateJobs(schema: "background"); // omit the schema for SQLite
	}
}
```

`AddMyAppJobs` is the generated registration method for an assembly named `MyApp`.

`AddImmediateJobs` configures the model but does not ship or apply migrations. Generate an application-owned migration
after adding the model:

```console
dotnet ef migrations add CreateImmediateJobsSchema \
	--context JobsDbContext \
	--output-dir Migrations/ImmediateJobs
dotnet ef database update --context JobsDbContext
```

Run these commands from the startup project, adding `--project` and `--startup-project` when the context lives in a
different project. Apply the migration through the application's normal deployment process. `EnsureCreatedAsync` is
appropriate only for samples and disposable databases. Using an existing business `DbContext` remains supported, but
couples the jobs schema to that model. The generated migration creates the seven Immediate.Jobs tables, indexes, and
constraints.

## Run one or several application instances

The example above uses `UseSingleServer()` and is valid only when exactly one application instance runs the
Immediate.Jobs worker. Jobs are selected from an in-process queue, while every change is also written to SQL so pending
work can be restored after a restart. Do not run two instances against the same database in this mode.

If the application runs multiple replicas, use `UseDistributed()` instead:

```csharp
builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage
		.UseEntityFrameworkCore<JobsDbContext>()
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
		.UseEntityFrameworkCore<JobsDbContext>()
		.UseDistributed());
```

Fair queues rotate due jobs between tenant or customer groups so a large backlog from one group does not crowd out
quieter groups. They are supported in both single-server and distributed SQL modes. See the
[queues and fairness documentation](https://immediateplatform.dev/docs/Immediate.Jobs/queues-and-fairness) for the
available settings.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Storage-provider configuration](https://immediateplatform.dev/docs/Immediate.Jobs/configuring-storage-providers#entity-framework-core)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
