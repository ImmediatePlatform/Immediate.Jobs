using LinqToDB;
using LinqToDB.Data;

namespace Immediate.Jobs.LinqToDB;

/// <summary>
/// 	Explicit bootstrap helpers for a fresh Immediate.Jobs schema.
/// </summary>
public static class LinqToDBSchemaExtensions
{
	/// <summary>
	/// 	Creates the Immediate.Jobs tables and indexes when they do not already exist.
	/// </summary>
	/// <remarks>
	/// 	This helper bootstraps fresh storage only; it does not perform production schema upgrades.
	/// </remarks>
	/// <param name="context">
	/// 	A LinqToDB Data Connection.
	/// </param>
	/// <param name="schema">
	/// 	The database schema to create objects in, or <see langword="null"/> for the provider default.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	A task that represents the asynchronous schema creation operation.
	/// </returns>
	public static async Task CreateImmediateJobsSchemaAsync<TContext>(
		this TContext context,
		string? schema = null,
		CancellationToken cancellationToken = default
	) where TContext : DataConnection
	{
		ArgumentNullException.ThrowIfNull(context);
		ValidateSchema(schema);

		await TaskScheduler.Yield();

		var provider = context.DataProvider.Name;
		if (schema is not null && provider.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException("SQLite does not support named schemas.", nameof(schema));

		if (schema is not null)
			await CreateSchemaAsync(context, provider, schema, cancellationToken);

		await CreateDefinitionNameCollationAsync(context, provider, cancellationToken);
		await context.CreateTableAsync<ImmediateJobDefinitionMetadataEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await context.CreateTableAsync<ImmediateJobDefinitionEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await context.CreateTableAsync<ImmediateJobAcquisitionEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await context.CreateTableAsync<ImmediateJobBatchEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await context.CreateTableAsync<ImmediateJobEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await context.CreateTableAsync<ImmediateJobExecutionEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await context.CreateTableAsync<ImmediateFairQueueGroupEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await context.CreateTableAsync<ImmediateJobContinuationEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await context.CreateTableAsync<ImmediateRecurringJobEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await context.CreateTableAsync<ImmediateJobServerEntity>(
			schemaName: schema,
			tableOptions: TableOptions.CreateIfNotExists,
			token: cancellationToken
		);

		await CreateConstraintsAndDefaultsAsync(context, provider, schema, cancellationToken);

		await CreateIndexesAsync(context, provider, schema, cancellationToken);
	}

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

	private static Task CreateDefinitionNameCollationAsync(
		DataConnection connection,
		string provider,
		CancellationToken cancellationToken
	)
	{
		if (provider.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
		{
			return connection.ExecuteAsync("""
				DO $collation$
				BEGIN
					IF NOT EXISTS (SELECT 1 FROM pg_collation WHERE collname = 'immediate_jobs_case_insensitive') THEN
						CREATE COLLATION "immediate_jobs_case_insensitive" (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
					END IF;
				EXCEPTION WHEN duplicate_object THEN NULL;
				END $collation$;
				""", cancellationToken);
		}

		if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
			|| provider.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
			return Task.CompletedTask;

		throw new NotSupportedException($"Immediate.Jobs schema bootstrap does not support provider '{provider}'.");
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

	private static async Task CreateConstraintsAndDefaultsAsync(
		DataConnection connection,
		string provider,
		string? schema,
		CancellationToken cancellationToken
	)
	{
		if (provider.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
			return;

		if (provider.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
		{
			var prefix = schema is null ? string.Empty : $"\"{schema}\".";

			await connection.ExecuteAsync($$"""

				ALTER TABLE {{prefix}}"immediate_job_executions" ALTER COLUMN "IsSynthetic" SET DEFAULT FALSE;
				DO $constraints$
				BEGIN
					IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_immediate_jobs_immediate_job_batches_BatchHandle'
						AND conrelid = '{{prefix}}"immediate_jobs"'::regclass) THEN
						ALTER TABLE {{prefix}}"immediate_jobs" ADD CONSTRAINT "FK_immediate_jobs_immediate_job_batches_BatchHandle"
							FOREIGN KEY ("BatchHandle") REFERENCES {{prefix}}"immediate_job_batches" ("Id") ON DELETE CASCADE;
					END IF;
					IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_immediate_job_continuations_immediate_jobs_ChildJobHandle'
						AND conrelid = '{{prefix}}"immediate_job_continuations"'::regclass) THEN
						ALTER TABLE {{prefix}}"immediate_job_continuations" ADD CONSTRAINT "FK_immediate_job_continuations_immediate_jobs_ChildJobHandle"
						FOREIGN KEY ("ChildJobHandle") REFERENCES {{prefix}}"immediate_jobs" ("Id") ON DELETE CASCADE;
					END IF;
					IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_immediate_job_executions_immediate_jobs_JobHandle'
						AND conrelid = '{{prefix}}"immediate_job_executions"'::regclass) THEN
						ALTER TABLE {{prefix}}"immediate_job_executions" ADD CONSTRAINT "FK_immediate_job_executions_immediate_jobs_JobHandle"
							FOREIGN KEY ("JobHandle") REFERENCES {{prefix}}"immediate_jobs" ("Id") ON DELETE CASCADE;
					END IF;
				END $constraints$;
				""", cancellationToken);

			return;
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

			await connection.ExecuteAsync($$"""
				IF NOT EXISTS (SELECT 1 FROM sys.default_constraints dc
					JOIN sys.columns c ON c.default_object_id = dc.object_id
					WHERE dc.parent_object_id = OBJECT_ID(N'{{qualifiedExecutions}}') AND c.name = N'IsSynthetic')
					ALTER TABLE {{qualifiedExecutions}} ADD DEFAULT 0 FOR [IsSynthetic];
				IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_immediate_jobs_immediate_job_batches_BatchHandle'
					AND parent_object_id = OBJECT_ID(N'{{qualifiedJobs}}'))
					ALTER TABLE {{qualifiedJobs}} ADD CONSTRAINT [FK_immediate_jobs_immediate_job_batches_BatchHandle]
						FOREIGN KEY ([BatchHandle]) REFERENCES {{qualifiedBatches}} ([Id]) ON DELETE CASCADE;
				IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_immediate_job_continuations_immediate_jobs_ChildJobHandle'
					AND parent_object_id = OBJECT_ID(N'{{qualifiedContinuations}}'))
					ALTER TABLE {{qualifiedContinuations}} ADD CONSTRAINT [FK_immediate_job_continuations_immediate_jobs_ChildJobHandle]
						FOREIGN KEY ([ChildJobHandle]) REFERENCES {{qualifiedJobs}} ([Id]) ON DELETE CASCADE;
				IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_immediate_job_executions_immediate_jobs_JobHandle'
					AND parent_object_id = OBJECT_ID(N'{{qualifiedExecutions}}'))
					ALTER TABLE {{qualifiedExecutions}} ADD CONSTRAINT [FK_immediate_job_executions_immediate_jobs_JobHandle]
						FOREIGN KEY ([JobHandle]) REFERENCES {{qualifiedJobs}} ([Id]) ON DELETE CASCADE;
				""", cancellationToken);

			return;
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
			("IX_immediate_job_acquisitions_JobName_AcquiredAt", "immediate_job_acquisitions", "JobName, AcquiredAt", false),
			("IX_immediate_jobs_JobName_State_LeaseExpiresAt", "immediate_jobs", "JobName, State, LeaseExpiresAt", false),
			("IX_immediate_job_batches_State_CompletedAt", "immediate_job_batches", "State, CompletedAt", false),
			("IX_immediate_jobs_RecurringKey", "immediate_jobs", "RecurringKey", true),
			("IX_immediate_jobs_BatchHandle_CreatedAt_Id", "immediate_jobs", "BatchHandle, CreatedAt, Id", false),
			("IX_immediate_jobs_CreatedAt_Id", "immediate_jobs", "CreatedAt DESC, Id", false),
			("IX_immediate_job_batches_CreatedAt_Id", "immediate_job_batches", "CreatedAt DESC, Id", false),
			("IX_immediate_jobs_State_DueAt", "immediate_jobs", "State, DueAt", false),
			("IX_immediate_jobs_State_CreatedAt_Id", "immediate_jobs", "State, CreatedAt DESC, Id", false),
			("IX_immediate_jobs_JobName_State_DueAt_CreatedAt", "immediate_jobs", "JobName, State, DueAt, CreatedAt", false),
			("IX_immediate_jobs_JobName_State_GroupId", "immediate_jobs", "JobName, State, GroupId", false),
			("IX_immediate_job_continuations_ParentKind_ParentId", "immediate_job_continuations", "ParentKind, ParentId", false),
			("IX_immediate_recurring_jobs_IsPaused_NextRunAt", "immediate_recurring_jobs", "IsPaused, NextRunAt", false),
			("IX_immediate_job_servers_ExpiresAt", "immediate_job_servers", "ExpiresAt", false),
		};

		foreach (var (name, table, columns, unique) in definitions)
		{
			var sql = CreateIndexSql(provider, schema, name, table, columns, unique);
			await connection.ExecuteAsync(sql, cancellationToken);
		}

		if (provider.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
		{
			// LinqToDB normalizes DateTimeOffset comparisons through this expression. A plain
			// LeaseExpiresAt key cannot supply the range seek, including for non-UTC offsets.
			await connection.ExecuteAsync("""
				CREATE INDEX IF NOT EXISTS "IX_immediate_jobs_JobName_State_LeaseExpiresAtUtc"
				ON "immediate_jobs" ("JobName", "State", strftime('%Y-%m-%d %H:%M:%f', "LeaseExpiresAt"))
				""", cancellationToken);
			// Metadata names use NOCASE, while the counter primary key is case-sensitive.
			await connection.ExecuteAsync("""
				CREATE INDEX IF NOT EXISTS "IX_immediate_job_definitions_JobName_NoCase"
				ON "immediate_job_definitions" ("JobName" COLLATE NOCASE)
				""", cancellationToken);
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
			return $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'{qualified}')) CREATE {uniqueness}INDEX [{name}] ON {qualified} ({QuoteColumns(columns, '[', ']')}){filter}";
		}

		if (provider.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
		{
			var qualified = schema is null ? $"\"{table}\"" : $"\"{schema}\".\"{table}\"";
			var quotedColumns = QuoteColumns(columns, '"', '"');
			return $"CREATE {uniqueness}INDEX IF NOT EXISTS \"{name}\" ON {qualified} ({quotedColumns})";
		}

		if (provider.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
		{
			var quotedColumns = QuoteColumns(columns, '"', '"');
			return $"CREATE {uniqueness}INDEX IF NOT EXISTS \"{name}\" ON \"{table}\" ({quotedColumns})";
		}

		throw new NotSupportedException($"Immediate.Jobs schema bootstrap does not support provider '{provider}'.");
	}

	private static string QuoteColumns(string columns, char openQuote, char closeQuote) =>
		string.Join(", ", columns.Split(", ").Select(column => column.EndsWith(" DESC", StringComparison.Ordinal)
			? $"{openQuote}{column[..^5]}{closeQuote} DESC"
			: $"{openQuote}{column}{closeQuote}"));
}
