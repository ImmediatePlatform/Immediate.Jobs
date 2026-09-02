using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.FunctionalTests;

public sealed class RecurringSchedulerTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
	public static TheoryData<string, DateTimeOffset> RuntimeCronForms => new()
	{
		{ "@yearly", new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) },
		{ "@annually", new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) },
		{ "@monthly", new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) },
		{ "@weekly", new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero) },
		{ "@DAILY", new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero) },
		{ "@midnight", new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero) },
		{ "@hourly", new(2026, 1, 1, 11, 0, 0, TimeSpan.Zero) },
		{ "@every_minute", new(2026, 1, 1, 10, 1, 0, TimeSpan.Zero) },
		{ "@every_second", new(2026, 1, 1, 10, 0, 1, TimeSpan.Zero) },
		{ "0\t*\t*\t*\t*", new(2026, 1, 1, 11, 0, 0, TimeSpan.Zero) },
	};

	[Fact]
	public async Task OverlapSkipDetectsAnActiveRun()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness("cleanup", "0 * * * *");
		var storage = harness.Storage;

		await AddActiveJob(storage, "cleanup", Start, harness.TimeProvider, cancellationToken);

		await harness.DrainAsync(cancellationToken);
		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromHours(1), cancellationToken);

		var materialized = await storage.QueryJobsAsync(
			new() { JobName = "cleanup", Take = 100 },
			cancellationToken
		);
		var occurrence = Assert.Single(materialized, job => job.RecurringKey is not null);
		Assert.Equal(JobState.Skipped, occurrence.State);
	}

	[Fact]
	public async Task OverlapSkipDetectsAPendingOccurrence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = new JobTestHarness(Start);
		var storage = harness.Storage;
		await AddPendingRecurringJob(storage, "cleanup", "cleanup:future", Start.AddHours(2), cancellationToken);

		var scheduler = BuildScheduler(storage, harness.TimeProvider, "cleanup", "0 * * * *");
		await scheduler.DrainAsync(cancellationToken);
		harness.TimeProvider.SetUtcNow(Start.AddHours(1));
		await scheduler.DrainAsync(cancellationToken);

		var jobs = await storage.QueryJobsAsync(
			new() { JobName = "cleanup", Take = 100 },
			cancellationToken
		);
		var occurrence = Assert.Single(jobs, job => job.DueAt == Start.AddHours(1));
		Assert.Equal(JobState.Skipped, occurrence.State);
	}

	[Fact]
	public async Task RestartKeepsAnOccurrenceThatFellDuringDowntime()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = new JobTestHarness(Start);
		var storage = harness.Storage;

		var first = BuildScheduler(storage, harness.TimeProvider, "hourly", "0 * * * *");
		await first.DrainAsync(cancellationToken);
		var scheduled = await GetSchedule(storage, "hourly", cancellationToken);
		Assert.Equal(Start.AddHours(1), scheduled.NextRunAt);

		// The process is down across the 11:00 occurrence and restarts at 11:05 with fresh scheduler
		// state. Recomputing from "now" here would advance the schedule to 12:00 and lose 11:00.
		harness.TimeProvider.SetUtcNow(Start.AddHours(1).AddMinutes(5));
		var restarted = BuildScheduler(storage, harness.TimeProvider, "hourly", "0 * * * *");
		await restarted.DrainAsync(cancellationToken);

		var materialized = await storage.QueryJobsAsync(new() { JobName = "hourly", Take = 100 }, cancellationToken);
		var occurrence = Assert.Single(materialized);
		Assert.Equal(Start.AddHours(1), occurrence.DueAt);
	}

	[Fact]
	public async Task ChangedCronRecomputesTheNextOccurrence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = new JobTestHarness(Start);
		var storage = harness.Storage;

		var hourly = BuildScheduler(storage, harness.TimeProvider, "shifting", "0 * * * *");
		await hourly.DrainAsync(cancellationToken);
		Assert.Equal(Start.AddHours(1), (await GetSchedule(storage, "shifting", cancellationToken)).NextRunAt);

		var daily = BuildScheduler(storage, harness.TimeProvider, "shifting", "0 0 * * *");
		await daily.DrainAsync(cancellationToken);
		Assert.Equal(
			new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
			(await GetSchedule(storage, "shifting", cancellationToken)).NextRunAt
		);
	}

	[Theory]
	[MemberData(nameof(RuntimeCronForms))]
	public async Task RuntimeAcceptsAnalyzerCronForms(string cron, DateTimeOffset expectedNextRunAt)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = new JobTestHarness(Start);
		var storage = harness.Storage;
		var scheduler = BuildScheduler(storage, harness.TimeProvider, "analyzer-compatible", cron);

		await scheduler.DrainAsync(cancellationToken);

		Assert.Equal(
			expectedNextRunAt,
			(await GetSchedule(storage, "analyzer-compatible", cancellationToken)).NextRunAt
		);
	}

	[Theory]
	[InlineData("not-a-cron", "UTC")]
	[InlineData("* * * * *", "Missing/TimeZone")]
	public async Task BadRecurringScheduleDoesNotBlockOrdinaryJobs(string cron, string timeZone)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = new JobTestHarness(Start);
		var storage = harness.Storage;
		await storage.UpsertRecurringAsync(new()
		{
			Name = "bad-schedule",
			JobName = "ordinary",
			QueueName = "default",
			Cron = cron,
			TimeZone = timeZone,
			IsCodeDefined = false,
			NextRunAt = Start,
		}, cancellationToken);
		await storage.EnqueueAsync(new()
		{
			JobHandle = JobHandle.FromString("ordinary-job"),
			JobName = "ordinary",
			QueueName = "default",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = Start,
			CreatedAt = Start,
		}, cancellationToken);
		var scheduler = BuildScheduler(storage, harness.TimeProvider, "ordinary", cron: null);

		await scheduler.DrainAsync(cancellationToken);

		Assert.Equal(
			JobState.Succeeded,
			(await storage.GetJobStatusAsync(JobHandle.FromString("ordinary-job"), cancellationToken))!.State
		);
		Assert.Equal(Start, (await GetSchedule(storage, "bad-schedule", cancellationToken)).NextRunAt);
	}

	private static async ValueTask<RecurringJobSchedule> GetSchedule(
		CapturingJobStorage storage,
		string name,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		return snapshot.Recurring.Single(schedule => string.Equals(schedule.Name, name, StringComparison.Ordinal));
	}

	private static ValueTask AddActiveJob(
		CapturingJobStorage storage,
		string jobName,
		DateTimeOffset createdAt,
		TimeProvider clock,
		CancellationToken cancellationToken
	) => storage.EnqueueAsync(new()
	{
		JobHandle = JobHandle.FromString(Guid.NewGuid().ToString("N")),
		JobName = jobName,
		QueueName = "default",
		Payload = "{}",
		State = JobState.Active,
		DueAt = createdAt,
		CreatedAt = createdAt,
		WorkerId = "worker",
		LeaseExpiresAt = clock.GetUtcNow().AddMinutes(30),
	}, cancellationToken);

	private static ValueTask AddPendingRecurringJob(
		CapturingJobStorage storage,
		string jobName,
		string recurringKey,
		DateTimeOffset dueAt,
		CancellationToken cancellationToken
	) => storage.EnqueueAsync(new()
	{
		JobHandle = JobHandle.FromString(Guid.NewGuid().ToString("N")),
		JobName = jobName,
		QueueName = "default",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = dueAt,
		CreatedAt = dueAt,
		RecurringKey = recurringKey,
	}, cancellationToken);

	private static JobSchedulingService BuildScheduler(
		IJobStorage storage,
		TimeProvider clock,
		string jobName,
		string? cron,
		IJobInvoker? invoker = null,
		OverlapPolicy overlapPolicy = OverlapPolicy.Skip,
		int maxParallelJobs = 1,
		int maxAttempts = 3
	)
	{
		invoker ??= NoOpInvoker.Instance;
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton(clock);
		_ = services.AddImmediateJobsCore()
			.ConfigureWorkers(o => o.WorkerCount = maxParallelJobs)
			.ConfigureStorage(o => o.UseStorage(_ => storage).UseDistributed());

		_ = services.AddSingleton(new JobDefinition
		{
			Name = jobName,
			Cron = cron,
			Invoker = invoker,
			JobType = invoker.GetType(),
			OverlapPolicy = overlapPolicy,
			MaxAttempts = maxAttempts,
		});

		var provider = services.BuildServiceProvider();
		return provider.GetRequiredService<JobSchedulingService>();
	}

	private sealed class NoOpInvoker : IJobInvoker
	{
		public static NoOpInvoker Instance { get; } = new();

		public ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution) =>
			ValueTask.CompletedTask;
	}

	private static JobTestHarness CreateHarness(string jobName, string? cron) =>
		new(
			Start,
			services => services.AddSingleton(
				new JobDefinition
				{
					Name = jobName,
					Cron = cron,
					Invoker = NoOpInvoker.Instance,
					JobType = typeof(NoOpInvoker),
					OverlapPolicy = OverlapPolicy.Skip,
				}
			)
		);

	private sealed class AssertNoOverlapInvoker(IJobStorage storage) : IJobInvoker
	{
		public int Executions { get; private set; }

		public async ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution)
		{
			var active = await storage.QueryJobsAsync(
				new() { State = JobState.Active, JobName = execution.Record.JobName, Take = 100 },
				execution.CancellationToken
			);
			_ = Assert.Single(active);
			Executions++;
		}
	}
}
