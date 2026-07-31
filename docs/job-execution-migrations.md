# Job-execution history migration

Job-execution history adds `immediate_job_executions`. Deploy this schema change before deploying
Immediate.Jobs binaries that retain executions. During a rolling deployment, older scheduler nodes
still update only the latest-execution columns on `immediate_jobs`, so history is best effort until
every node is upgraded.

EF Core applications should generate and review a normal application migration after
`modelBuilder.AddImmediateJobs(...)` has been updated. LINQ to DB's
`CreateImmediateJobsSchemaAsync` creates the table for a fresh database only; it is not an upgrade
runner. The equivalent additive DDL for existing databases follows. Replace `background` with the
schema passed to the provider.

## SQLite

```sql
CREATE TABLE "immediate_job_executions" (
    "JobId" TEXT NOT NULL,
    "Attempt" INTEGER NOT NULL,
    "State" INTEGER NOT NULL,
    "WorkerId" TEXT NULL,
    "AcquiredAt" INTEGER NULL,
    "ExecutionStartedAt" INTEGER NULL,
    "CompletedAt" INTEGER NULL,
    "ExecutionTraceId" TEXT NULL,
    "ExecutionSpanId" TEXT NULL,
    "Error" TEXT NULL,
    "IsSynthetic" INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT "PK_immediate_job_executions" PRIMARY KEY ("JobId", "Attempt"),
    CONSTRAINT "FK_immediate_job_executions_immediate_jobs_JobId"
        FOREIGN KEY ("JobId") REFERENCES "immediate_jobs" ("Id") ON DELETE CASCADE
);
```

## PostgreSQL

```sql
CREATE TABLE "background"."immediate_job_executions" (
    "JobId" character varying(256) NOT NULL,
    "Attempt" integer NOT NULL,
    "State" smallint NOT NULL,
    "WorkerId" character varying(256) NULL,
    "AcquiredAt" bigint NULL,
    "ExecutionStartedAt" bigint NULL,
    "CompletedAt" bigint NULL,
    "ExecutionTraceId" character varying(32) NULL,
    "ExecutionSpanId" character varying(16) NULL,
    "Error" text NULL,
    "IsSynthetic" boolean NOT NULL DEFAULT FALSE,
    CONSTRAINT "PK_immediate_job_executions" PRIMARY KEY ("JobId", "Attempt"),
    CONSTRAINT "FK_immediate_job_executions_immediate_jobs_JobId"
        FOREIGN KEY ("JobId")
        REFERENCES "background"."immediate_jobs" ("Id") ON DELETE CASCADE
);
```

## SQL Server

```sql
CREATE TABLE [background].[immediate_job_executions] (
    [JobId] nvarchar(256) NOT NULL,
    [Attempt] int NOT NULL,
    [State] smallint NOT NULL,
    [WorkerId] nvarchar(256) NULL,
    [AcquiredAt] bigint NULL,
    [ExecutionStartedAt] bigint NULL,
    [CompletedAt] bigint NULL,
    [ExecutionTraceId] nvarchar(32) NULL,
    [ExecutionSpanId] nvarchar(16) NULL,
    [Error] nvarchar(max) NULL,
    [IsSynthetic] bit NOT NULL
        CONSTRAINT [DF_immediate_job_executions_IsSynthetic] DEFAULT 0,
    CONSTRAINT [PK_immediate_job_executions] PRIMARY KEY ([JobId], [Attempt]),
    CONSTRAINT [FK_immediate_job_executions_immediate_jobs_JobId]
        FOREIGN KEY ([JobId])
        REFERENCES [background].[immediate_jobs] ([Id]) ON DELETE CASCADE
);
```

No data backfill is required. When history is first queried or a legacy job next changes state, the
provider reconstructs at most its latest positive attempt from the compatibility fields and marks
that record `IsSynthetic = true`. Missing earlier executions and unknown timing or worker values are
left absent rather than invented.
