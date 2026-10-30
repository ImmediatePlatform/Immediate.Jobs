using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.StorageTests;

[Collection("LinqToDB-SQLite")]
public sealed class LinqToDBIndexTests
{
	[Fact]
	public async Task ActiveLeasePlanUsesNormalizedExpiryAndCounterCollationIndexes()
	{
		var now = new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
		var commands = new List<(string Sql, SqliteParameter[] Parameters)>();
		await using var fixture = await LinqToDBConformanceFixture.CreateAsync(ConformanceDatabase.Sqlite,
			useDistributedTopology: true, container: null, new()
			{
				Jobs = [CreateJob("future", now.AddMinutes(5).ToOffset(TimeSpan.FromHours(2))),
					CreateJob("expired", now.AddMinutes(-5).ToOffset(TimeSpan.FromHours(-5)))],
				Batches = [],
				Edges = [],
				RecurringSchedules = [],
			}, trace =>
			{
				if (trace.TraceInfoStep == TraceInfoStep.BeforeExecute && trace.Command is { } command)
					commands.Add((command.CommandText, [.. command.Parameters.Cast<SqliteParameter>()
						.Select(static parameter => new SqliteParameter(parameter.ParameterName, parameter.SqliteType) { Value = parameter.Value })]));
			});
		var storage = fixture.Services.GetRequiredService<IJobStorage>();
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = "lease-index" }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, TestContext.Current.CancellationToken);
		await storage.PauseJobAsync("lease-index", TestContext.Current.CancellationToken);
		commands.Clear();

		var snapshot = await storage.GetMonitoringSnapshotAsync(TestContext.Current.CancellationToken);

		Assert.Equal(1, Assert.Single(snapshot.DefinitionStatuses).ActiveCount);
		var (sql, parameters) = Assert.Single(commands, static command => command.Sql.Contains("LeaseExpiresAt", StringComparison.Ordinal));
		await using var connection = new SqliteConnection(fixture.ConnectionString);
		await connection.OpenAsync(TestContext.Current.CancellationToken);
		await using var planCommand = connection.CreateCommand();
#pragma warning disable CA2100 // SQL captured from the provider, with its original parameters.
		planCommand.CommandText = "EXPLAIN QUERY PLAN " + sql;
#pragma warning restore CA2100
		planCommand.Parameters.AddRange(parameters);
		await using var reader = await planCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
		var steps = new List<string>();
		while (await reader.ReadAsync(TestContext.Current.CancellationToken))
			steps.Add(reader.GetString(3));
		Assert.Contains(steps, static step => step.Contains("IX_immediate_jobs_JobName_State_LeaseExpiresAtUtc", StringComparison.Ordinal)
			&& step.Contains("<expr>>?", StringComparison.Ordinal));
		Assert.Contains(steps, static step => step.Contains("IX_immediate_job_definitions_JobName_NoCase", StringComparison.Ordinal));

		JobRecord CreateJob(string id, DateTimeOffset leaseExpiresAt) => new()
		{
			JobHandle = JobHandle.FromString(id),
			JobName = "lease-index",
			State = JobState.Active,
			Attempt = 1,
			WorkerId = "worker",
			LeaseExpiresAt = leaseExpiresAt,
			CreatedAt = now,
			DueAt = now,
			Payload = "{}",
		};
	}
}
