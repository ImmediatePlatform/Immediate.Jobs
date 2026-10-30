using DotNet.Testcontainers.Containers;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.StorageTests;

[Collection("LinqToDB-SQLite")]
public sealed class LinqToDBSQLiteCommandTests() : LinqToDBCommandTests(ConformanceDatabase.Sqlite, container: null);

[Collection(LinqToDBPgSQLFixtureGroup.Name)]
public sealed class LinqToDBPgSQLCommandTests(LinqToDBPgSQLContainer container)
	: LinqToDBCommandTests(ConformanceDatabase.PostgreSql, container.PostgreSql);

[Collection(LinqToDBMsSQLFixtureGroup.Name)]
public sealed class LinqToDBMsSQLCommandTests(LinqToDBMsSQLContainer container)
	: LinqToDBCommandTests(ConformanceDatabase.SqlServer, container.SqlServer);

public abstract class LinqToDBCommandTests(ConformanceDatabase database, IDatabaseContainer? container)
{
	private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
	private const string JobName = "command-budget";
	private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

	[Theory]
	[InlineData(4)]
	[InlineData(40)]
	[InlineData(260)]
	public async Task FreshClaimsPreserveRequestOrderAndBatchCommands(int count)
	{
		var commands = new List<string>();
		var jobs = Enumerable.Range(0, count).Select(index => CreateJob(string.Create(provider: null, $"fresh-{index:D4}"))).ToList();
		await using var fixture = await CreateFixtureAsync(commands, jobs);
		var storage = (IJobGraphStorage)fixture.Services.GetRequiredService<IJobStorage>();
		await RegisterAsync(storage);
		var handles = jobs.Select(static job => job.JobHandle).Reverse().ToList();
		commands.Clear();

		var acquired = await storage.AcquireJobsAsync(handles, "worker", TimeSpan.FromMinutes(1), CancellationToken);

		Assert.Equal(handles, acquired.Select(static job => job.JobHandle));
		Assert.All(acquired, static job => Assert.Equal(1, job.Attempt));
		// Candidate read, counter bootstrap read/insert, then eight commands per claim block.
		Assert.InRange(commands.Count, 1, 3 + (8 * ((count + 255) / 256)));
		foreach (var job in new[] { acquired[0], acquired[^1] })
		{
			var execution = Assert.Single(await storage.QueryJobExecutionsAsync(job.JobHandle, new(), CancellationToken));
			Assert.False(execution.IsSynthetic);
			Assert.Equal("worker", execution.WorkerId);
		}
	}

	[Fact]
	public async Task TelemetryUsesOnlyUpdatesAndFencesStaleOwners()
	{
		var commands = new List<string>();
		var job = CreateJob("telemetry");
		await using var fixture = await CreateFixtureAsync(commands, [job]);
		var storage = (IJobGraphStorage)fixture.Services.GetRequiredService<IJobStorage>();
		await RegisterAsync(storage);
		var active = Assert.Single(await storage.AcquireJobsAsync([job.JobHandle], "worker", TimeSpan.FromMinutes(1), CancellationToken));
		commands.Clear();

		await storage.SetExecutionTelemetryAsync(job.JobHandle, active.Attempt, "worker", "trace", "span", Now, CancellationToken);

		Assert.Equal(2, commands.Count);
		Assert.All(commands, static command => Assert.StartsWith("UPDATE", command.TrimStart(), StringComparison.OrdinalIgnoreCase));
		await Assert.ThrowsAsync<ImmediateJobException>(async () =>
			await storage.SetExecutionTelemetryAsync(job.JobHandle, active.Attempt, "other-worker", "wrong", "wrong", Now, CancellationToken));
		var execution = Assert.Single(await storage.QueryJobExecutionsAsync(job.JobHandle, new(), CancellationToken));
		Assert.Equal("trace", execution.ExecutionTraceId);
		Assert.Equal("span", execution.ExecutionSpanId);
	}

	[Theory]
	[InlineData(4)]
	[InlineData(40)]
	[InlineData(260)]
	public async Task RecoveryBatchesReadsAndPreservesLegacyExecutions(int count)
	{
		var commands = new List<string>();
		var jobs = Enumerable.Range(0, count).Select(index => CreateJob(string.Create(provider: null, $"active-{index:D4}")) with
		{
			State = JobState.Active,
			Attempt = 1,
			WorkerId = "old-worker",
			LeaseExpiresAt = Now.AddHours(1),
		}).ToList();
		await using var fixture = await CreateFixtureAsync(commands, jobs);
		var storage = (IJobGraphStorage)fixture.Services.GetRequiredService<IJobStorage>();
		commands.Clear();

		await storage.InitializeAsync(interruptActiveLeases: true, CancellationToken);

		Assert.InRange(commands.Count(IsRead), 1, 2 * ((count + 255) / 256));
		foreach (var job in new[] { jobs[0], jobs[^1] })
		{
			var execution = Assert.Single(await storage.QueryJobExecutionsAsync(job.JobHandle, new(), CancellationToken));
			Assert.True(execution.IsSynthetic);
			Assert.Equal(JobExecutionState.Interrupted, execution.State);
			Assert.Equal(Now, execution.CompletedAt);
			Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(job.JobHandle, CancellationToken))!.State);
		}
	}

	[Theory]
	[InlineData(4)]
	[InlineData(40)]
	[InlineData(260)]
	public async Task BatchCancellationDoesNotReadOrUpdateEachMember(int count)
	{
		var commands = new List<string>();
		var batch = new BatchRecord
		{
			BatchHandle = BatchHandle.FromString("cancel-batch")!,
			CreatedAt = Now,
			State = BatchState.Executing,
			TotalJobs = count,
			PendingCount = count,
		};
		var jobs = Enumerable.Range(0, count).Select(index => CreateJob(string.Create(provider: null, $"member-{index:D4}")) with { BatchHandle = batch.BatchHandle }).ToList();
		await using var fixture = await CreateFixtureAsync(commands, jobs, [batch]);
		var storage = (IJobGraphStorage)fixture.Services.GetRequiredService<IJobStorage>();
		commands.Clear();

		await storage.CancelBatchAsync(batch.BatchHandle, CancellationToken);

		Assert.InRange(commands.Count, 1, 4 + (2 * ((count + 255) / 256)));
		var status = await storage.GetBatchStatusAsync(batch.BatchHandle, CancellationToken);
		Assert.NotNull(status);
		Assert.Equal(0, status.Remaining);
		Assert.Equal(count, status.Cancelled);
		Assert.Equal(BatchState.Cancelled, status.State);
	}

	[Fact]
	public async Task HistoryUnionPagesPersistedAndLegacyAttemptsInOneCommand()
	{
		var commands = new List<string>();
		var job = CreateJob("history");
		await using var fixture = await CreateFixtureAsync(commands, [job]);
		var storage = (IJobGraphStorage)fixture.Services.GetRequiredService<IJobStorage>();
		await RegisterAsync(storage);
		var first = Assert.Single(await storage.AcquireJobsAsync([job.JobHandle], "first", TimeSpan.FromMinutes(1), CancellationToken));
		await storage.FailAsync(job.JobHandle, first.Attempt, "first", "retry", Now, CancellationToken);
		var second = Assert.Single(await storage.AcquireJobsAsync([job.JobHandle], "second", TimeSpan.FromMinutes(1), CancellationToken));
		commands.Clear();

		var page = await storage.QueryJobExecutionsAsync(job.JobHandle, new() { Skip = 1, Take = 1 }, CancellationToken);

		Assert.Single(commands);
		Assert.Equal(first.Attempt, Assert.Single(page).Attempt);
		commands.Clear();
		var filtered = await storage.QueryJobExecutionsAsync(job.JobHandle, new() { Attempt = second.Attempt }, CancellationToken);
		Assert.Single(commands);
		Assert.Equal(second.Attempt, Assert.Single(filtered).Attempt);
	}

	[Fact]
	public async Task RecurringMaterializationAndResumeUseGuardedCommands()
	{
		var commands = new List<string>();
		await using var fixture = await CreateFixtureAsync(commands);
		var storage = (IJobGraphStorage)fixture.Services.GetRequiredService<IJobStorage>();
		await RegisterAsync(storage);
		var schedule = new RecurringJobSchedule
		{
			Name = "recurring-budget",
			JobName = JobName,
			Cron = "* * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = Now,
		};
		await storage.UpsertRecurringAsync(schedule, CancellationToken);
		await storage.PauseRecurringAsync(schedule.Name, CancellationToken);
		commands.Clear();
		await storage.ResumeRecurringAsync(schedule.Name, CancellationToken);
		Assert.Single(commands);
		commands.Clear();

		Assert.True(await storage.MaterializeRecurringAsync(schedule, CreateJob("recurring-job"), Now.AddMinutes(1), cancellationToken: CancellationToken));

		Assert.Equal(2, commands.Count);
		Assert.DoesNotContain(commands, IsRead);
		commands.Clear();
		Assert.False(await storage.MaterializeRecurringAsync(schedule, CreateJob("stale-job"), Now.AddMinutes(1), cancellationToken: CancellationToken));
		Assert.Single(commands);
	}

	[Fact]
	public async Task LegacyHistoryIsSynthesizedWithoutWritingRows()
	{
		var commands = new List<string>();
		var job = CreateJob("legacy-history") with { Attempt = 3, State = JobState.Failed, LastError = "legacy error", CompletedAt = Now };
		await using var fixture = await CreateFixtureAsync(commands, [job]);
		var storage = fixture.Services.GetRequiredService<IJobStorage>();
		commands.Clear();

		var execution = Assert.Single(await storage.QueryJobExecutionsAsync(job.JobHandle, new() { Attempt = 3 }, CancellationToken));

		Assert.Single(commands);
		Assert.All(commands, static command => Assert.True(IsRead(command)));
		Assert.True(execution.IsSynthetic);
		Assert.Equal("legacy error", execution.Error);
		Assert.Equal(JobExecutionState.Failed, execution.State);
		Assert.Empty(await storage.QueryJobExecutionsAsync(job.JobHandle, new() { Skip = 1 }, CancellationToken));
		Assert.Empty(await storage.QueryJobExecutionsAsync(JobHandle.FromString("missing"), new(), CancellationToken));
	}

	[Theory]
	[InlineData(4)]
	[InlineData(40)]
	[InlineData(260)]
	public async Task WideContinuationPropagationBatchesReads(int count)
	{
		var commands = new List<string>();
		var parent = CreateJob("parent");
		var children = Enumerable.Range(0, count).Select(index => CreateJob(string.Create(provider: null, $"child-{index:D4}")) with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		}).ToList();
		var edges = children.Select(child => new JobContinuationEdge
		{
			ChildJobHandle = child.JobHandle,
			ParentJobHandle = parent.JobHandle,
			Delay = TimeSpan.Zero,
		}).ToList();
		await using var fixture = await CreateFixtureAsync(commands, [parent, .. children], edges: edges);
		var storage = fixture.Services.GetRequiredService<IJobStorage>();
		commands.Clear();

		await storage.CancelAsync(parent.JobHandle, CancellationToken);

		Assert.InRange(commands.Count(IsRead), 1, 2 + (3 * ((count + 255) / 256)));
		foreach (var child in new[] { children[0], children[^1] })
			Assert.Equal(JobState.Skipped, (await storage.GetJobStatusAsync(child.JobHandle, CancellationToken))!.State);
	}

	[Fact]
	public async Task BatchTriggerPropagatesSkipsAfterAllMembersTransition()
	{
		var commands = new List<string>();
		var batch = new BatchRecord
		{
			BatchHandle = BatchHandle.FromString("trigger-batch")!,
			CreatedAt = Now,
			State = BatchState.WaitingForTrigger,
			TotalJobs = 2,
			PendingCount = 2,
		};
		var parent = CreateJob("settled-parent") with { State = JobState.Succeeded, CompletedAt = Now };
		var first = CreateJob("first-waiter") with { State = JobState.WaitingForTrigger, BatchHandle = batch.BatchHandle };
		var second = CreateJob("second-waiter") with
		{
			State = JobState.WaitingForTrigger,
			BatchHandle = batch.BatchHandle,
			RemainingDependencies = 1,
		};
		JobContinuationEdge[] edges =
		[
			new() { ChildJobHandle = first.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero, Trigger = ContinuationTrigger.Failure },
			new() { ChildJobHandle = second.JobHandle, ParentJobHandle = first.JobHandle, Delay = TimeSpan.Zero },
		];
		await using var fixture = await CreateFixtureAsync(commands, [parent, first, second], [batch], edges);
		var storage = (IJobGraphStorage)fixture.Services.GetRequiredService<IJobStorage>();

		Assert.True(await storage.TryTriggerBatchAsync(batch.BatchHandle, CancellationToken));

		Assert.Equal(JobState.Skipped, (await storage.GetJobStatusAsync(first.JobHandle, CancellationToken))!.State);
		Assert.Equal(JobState.Skipped, (await storage.GetJobStatusAsync(second.JobHandle, CancellationToken))!.State);
		var status = await storage.GetBatchStatusAsync(batch.BatchHandle, CancellationToken);
		Assert.NotNull(status);
		Assert.Equal(2, status.Skipped);
		Assert.Equal(0, status.Remaining);
	}

	private async ValueTask<LinqToDBConformanceFixture> CreateFixtureAsync(
		List<string> commands, IReadOnlyList<JobRecord>? jobs = null, IReadOnlyList<BatchRecord>? batches = null,
		IReadOnlyList<JobContinuationEdge>? edges = null
	) => await LinqToDBConformanceFixture.CreateAsync(database, useDistributedTopology: true, container,
		new() { Jobs = jobs ?? [], Batches = batches ?? [], Edges = edges ?? [], RecurringSchedules = [] },
		trace =>
		{
			if (trace.TraceInfoStep == TraceInfoStep.BeforeExecute && trace.Command is { } command)
				commands.Add(command.CommandText);
		});

	private static ValueTask RegisterAsync(IJobStorage storage) => storage.MergeJobDefinitionsListAsync(new()
	{
		Definitions = [new() { Name = JobName }],
		RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
	}, CancellationToken);

	private static JobRecord CreateJob(string id) => new JobSubmission
	{
		JobHandle = JobHandle.FromString(id),
		JobName = JobName,
		Payload = "{}",
		State = JobState.Pending,
		DueAt = Now,
		CreatedAt = Now,
	}.ToJobRecord(JobName);

	private static bool IsRead(string command) => command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
}
