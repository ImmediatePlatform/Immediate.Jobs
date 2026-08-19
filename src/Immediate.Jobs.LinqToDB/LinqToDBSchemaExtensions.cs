using LinqToDB;
using LinqToDB.Data;

namespace Immediate.Jobs.LinqToDB;

/// <summary>Explicit bootstrap helpers for a fresh Immediate.Jobs schema.</summary>
public static class LinqToDBSchemaExtensions
{
	/// <summary>Creates the Immediate.Jobs tables and indexes when they do not already exist.</summary>
	/// <remarks>This helper bootstraps fresh storage only; it does not perform production schema upgrades.</remarks>
	/// <param name="context">A LinqToDB Data Connection.</param>
	/// <param name="schema">The database schema to create objects in, or <see langword="null"/> for the provider default.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task that represents the asynchronous schema creation operation.</returns>
	public static async Task CreateImmediateJobsSchemaAsync<TContext>(
		this TContext context,
		string? schema = null,
		CancellationToken cancellationToken = default
	) where TContext : DataConnection
	{
		ArgumentNullException.ThrowIfNull(context);
		ValidateSchema(schema);

		var provider = context.DataProvider.Name;
		if (schema is not null && provider.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException("SQLite does not support named schemas.", nameof(schema));

		if (provider.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
		{
			_ = await context.ExecuteAsync(SqliteSchema, cancellationToken).ConfigureAwait(false);
			await CreateIndexesAsync(context, provider, schema, cancellationToken).ConfigureAwait(false);
			return;
		}

		if (schema is not null)
			_ = await CreateSchemaAsync(context, provider, schema, cancellationToken).ConfigureAwait(false);

		const TableOptions CreateIfMissing = TableOptions.CreateIfNotExists;
		_ = await context.CreateTableAsync<ImmediateJobBatchEntity>(
			schemaName: schema,
			tableOptions: CreateIfMissing,
			token: cancellationToken
		).ConfigureAwait(false);

		_ = await context.CreateTableAsync<ImmediateJobEntity>(
			schemaName: schema,
			tableOptions: CreateIfMissing,
			token: cancellationToken
		).ConfigureAwait(false);

		_ = await context.CreateTableAsync<ImmediateJobExecutionEntity>(
			schemaName: schema,
			tableOptions: CreateIfMissing,
			token: cancellationToken
		).ConfigureAwait(false);

		_ = await context.CreateTableAsync<ImmediateFairQueueGroupEntity>(
			schemaName: schema,
			tableOptions: CreateIfMissing,
			token: cancellationToken
		).ConfigureAwait(false);

		_ = await context.CreateTableAsync<ImmediateJobContinuationEntity>(
			schemaName: schema,
			tableOptions: CreateIfMissing,
			token: cancellationToken
		).ConfigureAwait(false);

		_ = await context.CreateTableAsync<ImmediateRecurringJobEntity>(
			schemaName: schema,
			tableOptions: CreateIfMissing,
			token: cancellationToken
		).ConfigureAwait(false);

		_ = await context.CreateTableAsync<ImmediateJobServerEntity>(
			schemaName: schema,
			tableOptions: CreateIfMissing,
			token: cancellationToken
		).ConfigureAwait(false);

		_ = await CreateConstraintsAndDefaultsAsync(context, provider, schema, cancellationToken)
			.ConfigureAwait(false);

		await CreateIndexesAsync(context, provider, schema, cancellationToken).ConfigureAwait(false);
	}

	private const string SqliteSchema = """
		CREATE TABLE IF NOT EXISTS "immediate_job_batches" (
			"Id" TEXT NOT NULL CONSTRAINT "PK_immediate_job_batches" PRIMARY KEY,
			"CreatedAt" INTEGER NOT NULL, "TotalJobs" INTEGER NOT NULL, "PendingCount" INTEGER NOT NULL,
			"SucceededCount" INTEGER NOT NULL, "FailedCount" INTEGER NOT NULL, "CancelledCount" INTEGER NOT NULL,
			"SkippedCount" INTEGER NOT NULL,
			"StartedAt" INTEGER NULL, "CompletedAt" INTEGER NULL, "State" INTEGER NOT NULL,
			"ConcurrencyStamp" TEXT NOT NULL
		);
		CREATE TABLE IF NOT EXISTS "immediate_jobs" (
			"Id" TEXT NOT NULL CONSTRAINT "PK_immediate_jobs" PRIMARY KEY,
			"QueueName" TEXT NOT NULL DEFAULT 'default', "JobName" TEXT NOT NULL, "Payload" TEXT NOT NULL,
			"Context" TEXT NULL, "GroupId" TEXT NULL, "State" INTEGER NOT NULL, "DueAt" INTEGER NOT NULL, "CreatedAt" INTEGER NOT NULL,
			"Attempt" INTEGER NOT NULL, "WorkerId" TEXT NULL, "LeaseExpiresAt" INTEGER NULL, "LastError" TEXT NULL,
			"CompletedAt" INTEGER NULL, "RecurringKey" TEXT NULL, "TraceParent" TEXT NULL, "TraceState" TEXT NULL,
			"ExecutionTraceId" TEXT NULL, "ExecutionSpanId" TEXT NULL, "ExecutionStartedAt" INTEGER NULL,
			"BatchId" TEXT NULL, "RemainingDependencies" INTEGER NOT NULL, "FailedDependencies" INTEGER NOT NULL,
			"ConcurrencyStamp" TEXT NOT NULL,
			CONSTRAINT "FK_immediate_jobs_immediate_job_batches_BatchId" FOREIGN KEY ("BatchId")
				REFERENCES "immediate_job_batches" ("Id") ON DELETE CASCADE
		);
		CREATE TABLE IF NOT EXISTS "immediate_job_executions" (
			"JobId" TEXT NOT NULL, "Attempt" INTEGER NOT NULL, "State" INTEGER NOT NULL,
			"WorkerId" TEXT NULL, "AcquiredAt" INTEGER NULL, "ExecutionStartedAt" INTEGER NULL,
			"CompletedAt" INTEGER NULL, "ExecutionTraceId" TEXT NULL, "ExecutionSpanId" TEXT NULL,
			"Error" TEXT NULL, "IsSynthetic" INTEGER NOT NULL DEFAULT 0,
			CONSTRAINT "PK_immediate_job_executions" PRIMARY KEY ("JobId", "Attempt"),
			CONSTRAINT "FK_immediate_job_executions_immediate_jobs_JobId" FOREIGN KEY ("JobId")
				REFERENCES "immediate_jobs" ("Id") ON DELETE CASCADE
		);
		CREATE TABLE IF NOT EXISTS "immediate_fair_queue_groups" (
			"QueueName" TEXT NOT NULL, "GroupId" TEXT NOT NULL, "LastServedSequence" INTEGER NOT NULL,
			"ConcurrencyStamp" TEXT NOT NULL,
			CONSTRAINT "PK_immediate_fair_queue_groups" PRIMARY KEY ("QueueName", "GroupId")
		);
		CREATE TABLE IF NOT EXISTS "immediate_job_continuations" (
			"ChildJobId" TEXT NOT NULL, "ParentKind" INTEGER NOT NULL, "ParentId" TEXT NOT NULL,
			"Trigger" INTEGER NOT NULL, "ParentOutcome" INTEGER NOT NULL,
			CONSTRAINT "PK_immediate_job_continuations" PRIMARY KEY ("ChildJobId", "ParentKind", "ParentId"),
			CONSTRAINT "FK_immediate_job_continuations_immediate_jobs_ChildJobId" FOREIGN KEY ("ChildJobId")
				REFERENCES "immediate_jobs" ("Id") ON DELETE CASCADE
		);
		CREATE TABLE IF NOT EXISTS "immediate_recurring_jobs" (
			"Name" TEXT NOT NULL CONSTRAINT "PK_immediate_recurring_jobs" PRIMARY KEY,
			"JobName" TEXT NOT NULL, "Cron" TEXT NOT NULL, "TimeZone" TEXT NOT NULL,
			"IsCodeDefined" INTEGER NOT NULL, "IsPaused" INTEGER NOT NULL, "NextRunAt" INTEGER NOT NULL,
			"LastRunAt" INTEGER NULL, "ConcurrencyStamp" TEXT NOT NULL
		);
		CREATE TABLE IF NOT EXISTS "immediate_job_servers" (
			"WorkerId" TEXT NOT NULL CONSTRAINT "PK_immediate_job_servers" PRIMARY KEY,
			"LastHeartbeat" INTEGER NOT NULL, "ActiveWorkers" INTEGER NOT NULL, "MaxWorkers" INTEGER NOT NULL
		);
		""";

	internal static void ValidateSchema(string? schema)
	{
		if (schema is null)
			return;
		if (string.IsNullOrWhiteSpace(schema) || schema.Any(character =>
			!char.IsLetterOrDigit(character) && character != '_'))
		{
			throw new ArgumentException("Schema names may contain only letters, digits, and underscores.", nameof(schema));
		}
	}

	private static Task<int> CreateSchemaAsync(
		DataConnection connection,
		string provider,
		string schema,
		CancellationToken cancellationToken
	)
	{
		if (provider.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
			return connection.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS \"{schema}\"", cancellationToken);
		if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
		{
			return connection.ExecuteAsync(
				$"IF SCHEMA_ID(N'{schema}') IS NULL EXEC(N'CREATE SCHEMA [{schema}]')",
				cancellationToken
			);
		}

		throw new NotSupportedException($"Immediate.Jobs schema bootstrap does not support provider '{provider}'.");
	}

	private static Task<int> CreateConstraintsAndDefaultsAsync(
		DataConnection connection,
		string provider,
		string? schema,
		CancellationToken cancellationToken
	)
	{
		if (provider.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
		{
			var prefix = schema is null ? string.Empty : $"\"{schema}\".";
			return connection.ExecuteAsync($$"""
				ALTER TABLE {{prefix}}"immediate_jobs" ALTER COLUMN "QueueName" SET DEFAULT 'default';
				ALTER TABLE {{prefix}}"immediate_job_executions" ALTER COLUMN "IsSynthetic" SET DEFAULT FALSE;
				DO $constraints$
				BEGIN
					IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_immediate_jobs_immediate_job_batches_BatchId'
						AND conrelid = '{{prefix}}"immediate_jobs"'::regclass) THEN
						ALTER TABLE {{prefix}}"immediate_jobs" ADD CONSTRAINT "FK_immediate_jobs_immediate_job_batches_BatchId"
							FOREIGN KEY ("BatchId") REFERENCES {{prefix}}"immediate_job_batches" ("Id") ON DELETE CASCADE;
					END IF;
					IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_immediate_job_continuations_immediate_jobs_ChildJobId'
						AND conrelid = '{{prefix}}"immediate_job_continuations"'::regclass) THEN
						ALTER TABLE {{prefix}}"immediate_job_continuations" ADD CONSTRAINT "FK_immediate_job_continuations_immediate_jobs_ChildJobId"
						FOREIGN KEY ("ChildJobId") REFERENCES {{prefix}}"immediate_jobs" ("Id") ON DELETE CASCADE;
					END IF;
					IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_immediate_job_executions_immediate_jobs_JobId'
						AND conrelid = '{{prefix}}"immediate_job_executions"'::regclass) THEN
						ALTER TABLE {{prefix}}"immediate_job_executions" ADD CONSTRAINT "FK_immediate_job_executions_immediate_jobs_JobId"
							FOREIGN KEY ("JobId") REFERENCES {{prefix}}"immediate_jobs" ("Id") ON DELETE CASCADE;
					END IF;
				END $constraints$;
				""", cancellationToken);
		}

		if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
		{
			var qualifiedJobs = schema is null ? "[dbo].[immediate_jobs]" : $"[{schema}].[immediate_jobs]";
			var qualifiedBatches = schema is null ? "[dbo].[immediate_job_batches]" : $"[{schema}].[immediate_job_batches]";
			var qualifiedContinuations = schema is null
				? "[dbo].[immediate_job_continuations]"
				: $"[{schema}].[immediate_job_continuations]";
			var qualifiedExecutions = schema is null
				? "[dbo].[immediate_job_executions]"
				: $"[{schema}].[immediate_job_executions]";
			return connection.ExecuteAsync($$"""
				IF NOT EXISTS (SELECT 1 FROM sys.default_constraints dc
					JOIN sys.columns c ON c.default_object_id = dc.object_id
					WHERE dc.parent_object_id = OBJECT_ID(N'{{qualifiedJobs}}') AND c.name = N'QueueName')
					ALTER TABLE {{qualifiedJobs}} ADD DEFAULT N'default' FOR [QueueName];
				IF NOT EXISTS (SELECT 1 FROM sys.default_constraints dc
					JOIN sys.columns c ON c.default_object_id = dc.object_id
					WHERE dc.parent_object_id = OBJECT_ID(N'{{qualifiedExecutions}}') AND c.name = N'IsSynthetic')
					ALTER TABLE {{qualifiedExecutions}} ADD DEFAULT 0 FOR [IsSynthetic];
				IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_immediate_jobs_immediate_job_batches_BatchId'
					AND parent_object_id = OBJECT_ID(N'{{qualifiedJobs}}'))
					ALTER TABLE {{qualifiedJobs}} ADD CONSTRAINT [FK_immediate_jobs_immediate_job_batches_BatchId]
						FOREIGN KEY ([BatchId]) REFERENCES {{qualifiedBatches}} ([Id]) ON DELETE CASCADE;
				IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_immediate_job_continuations_immediate_jobs_ChildJobId'
					AND parent_object_id = OBJECT_ID(N'{{qualifiedContinuations}}'))
					ALTER TABLE {{qualifiedContinuations}} ADD CONSTRAINT [FK_immediate_job_continuations_immediate_jobs_ChildJobId]
						FOREIGN KEY ([ChildJobId]) REFERENCES {{qualifiedJobs}} ([Id]) ON DELETE CASCADE;
				IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_immediate_job_executions_immediate_jobs_JobId'
					AND parent_object_id = OBJECT_ID(N'{{qualifiedExecutions}}'))
					ALTER TABLE {{qualifiedExecutions}} ADD CONSTRAINT [FK_immediate_job_executions_immediate_jobs_JobId]
						FOREIGN KEY ([JobId]) REFERENCES {{qualifiedJobs}} ([Id]) ON DELETE CASCADE;
				""", cancellationToken);
		}

		throw new NotSupportedException($"Immediate.Jobs schema bootstrap does not support provider '{provider}'.");
	}

	private static async Task CreateIndexesAsync(
		DataConnection connection,
		string provider,
		string? schema,
		CancellationToken cancellationToken
	)
	{
		var definitions = new (string Name, string Table, string Columns, bool Unique)[]
		{
			("IX_immediate_job_batches_State_CompletedAt", "immediate_job_batches", "State, CompletedAt", false),
			("IX_immediate_jobs_RecurringKey", "immediate_jobs", "RecurringKey", true),
			("IX_immediate_jobs_BatchId", "immediate_jobs", "BatchId", false),
			("IX_immediate_jobs_State_DueAt", "immediate_jobs", "State, DueAt", false),
			("IX_immediate_jobs_State_CreatedAt", "immediate_jobs", "State, CreatedAt", false),
			("IX_immediate_jobs_QueueName_State_DueAt_CreatedAt", "immediate_jobs", "QueueName, State, DueAt, CreatedAt", false),
			("IX_immediate_jobs_QueueName_State_GroupId", "immediate_jobs", "QueueName, State, GroupId", false),
			("IX_immediate_job_continuations_ParentKind_ParentId", "immediate_job_continuations", "ParentKind, ParentId", false),
			("IX_immediate_recurring_jobs_IsPaused_NextRunAt", "immediate_recurring_jobs", "IsPaused, NextRunAt", false),
			("IX_immediate_job_servers_LastHeartbeat", "immediate_job_servers", "LastHeartbeat", false),
		};

		foreach (var (name, table, columns, unique) in definitions)
		{
			var sql = CreateIndexSql(provider, schema, name, table, columns, unique);
			_ = await connection.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
		}
	}

	private static string CreateIndexSql(
		string provider,
		string? schema,
		string name,
		string table,
		string columns,
		bool unique
	)
	{
		var uniqueness = unique ? "UNIQUE " : string.Empty;
		if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
		{
			var qualified = schema is null ? $"[dbo].[{table}]" : $"[{schema}].[{table}]";
			var filter = string.Equals(name, "IX_immediate_jobs_RecurringKey", StringComparison.Ordinal) ? " WHERE [RecurringKey] IS NOT NULL" : string.Empty;
			return FormattableString.Invariant(
				$"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'{qualified}')) CREATE {uniqueness}INDEX [{name}] ON {qualified} ({string.Join(", ", columns.Split(", ").Select(static column => $"[{column}]"))}){filter}"
			);
		}

		if (provider.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
		{
			var qualified = schema is null ? $"\"{table}\"" : $"\"{schema}\".\"{table}\"";
			var quotedColumns = string.Join(", ", columns.Split(", ").Select(static column => $"\"{column}\""));
			return $"CREATE {uniqueness}INDEX IF NOT EXISTS \"{name}\" ON {qualified} ({quotedColumns})";
		}

		if (provider.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
		{
			var quotedColumns = string.Join(", ", columns.Split(", ").Select(static column => $"\"{column}\""));
			return $"CREATE {uniqueness}INDEX IF NOT EXISTS \"{name}\" ON \"{table}\" ({quotedColumns})";
		}

		throw new NotSupportedException($"Immediate.Jobs schema bootstrap does not support provider '{provider}'.");
	}
}
