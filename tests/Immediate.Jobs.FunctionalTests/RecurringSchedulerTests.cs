using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

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

	[Theory]
	[InlineData(JobState.Pending)]
	[InlineData(JobState.Active)]
	public async Task OverlapSkipDetectsAPresentRun(JobState existingState)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness("cleanup", "0 * * * *");
		var storage = harness.Storage;

		await storage.EnqueueAsync(new()
		{
			JobHandle = JobHandle.FromString(Guid.NewGuid().ToString("N")),
			JobName = "cleanup",
			QueueName = "default",
			Payload = "{}",
			State = existingState,
			DueAt = Start.AddHours(2),
			CreatedAt = Start,
			WorkerId = existingState == JobState.Active ? "worker" : null,
			LeaseExpiresAt = existingState == JobState.Active ? Start.AddHours(2) : null,
		}, cancellationToken);

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
	public async Task OverlapSkipOverTwoCronOccurrencesCreatesEachMissingSkippedJobInSequence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new CapturingJobStorage(new FakeTimeProvider(Start));
		var clock = new FakeTimeProvider(Start);
		var existingHandle = JobHandle.FromString("existing-run");
		await storage.EnqueueAsync(new()
		{
			JobHandle = existingHandle,
			JobName = "cleanup",
			QueueName = "default",
			Payload = "{}",
			State = JobState.Active,
			DueAt = Start,
			CreatedAt = Start,
			WorkerId = "worker",
			LeaseExpiresAt = Start.AddHours(4),
		}, cancellationToken);

		var scheduler = BuildScheduler(
			storage,
			clock,
			"cleanup",
			"0 * * * *",
			overlapPolicy: OverlapPolicy.Skip,
			misfireHandlingMode: MisfireHandlingMode.EnqueueAll
		);

		await scheduler.DrainAsync(cancellationToken);

		clock.Advance(TimeSpan.FromHours(2));
		await scheduler.DrainAsync(cancellationToken);

		clock.Advance(TimeSpan.FromHours(1));
		await scheduler.DrainAsync(cancellationToken);

		Assert.Equal(
			[
				new { DependencyCount = -1, DueAt = Start.AddHours(1), State = JobState.Skipped, },
				new { DependencyCount = -1, DueAt = Start.AddHours(2), State = JobState.Skipped, },
				new { DependencyCount = -1, DueAt = Start.AddHours(3), State = JobState.Skipped, },
			],
			storage.RecurringMaterializations
				.Select(rm => new
				{
					DependencyCount = rm.Dependencies?.Count ?? -1,
					rm.Job.DueAt,
					rm.Job.State,
				})
		);

		Assert.Equal(
			Start.AddHours(3),
			storage.RecurringSchedules["cleanup"].LastRunAt
		);

		Assert.Equal(
			Start.AddHours(4),
			storage.RecurringSchedules["cleanup"].NextRunAt
		);
	}

	[Theory]
	[InlineData(JobState.Pending)]
	[InlineData(JobState.Active)]
	public async Task OverlapQueueCreatesAContinuationFromAnExistingRun(JobState existingState)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new CapturingJobStorage(new FakeTimeProvider(Start));
		var clock = new FakeTimeProvider(Start);
		var existingHandle = JobHandle.FromString("existing-run");
		await storage.EnqueueAsync(new()
		{
			JobHandle = existingHandle,
			JobName = "cleanup",
			QueueName = "default",
			Payload = "{}",
			State = existingState,
			DueAt = Start.AddHours(2),
			CreatedAt = Start,
			WorkerId = existingState == JobState.Active ? "worker" : null,
			LeaseExpiresAt = existingState == JobState.Active ? Start.AddHours(2) : null,
		}, cancellationToken);

		var scheduler = BuildScheduler(
			storage,
			clock,
			"cleanup",
			"0 * * * *",
			overlapPolicy: OverlapPolicy.Queue,
			misfireHandlingMode: MisfireHandlingMode.EnqueueAll
		);
		await scheduler.DrainAsync(cancellationToken);
		clock.Advance(TimeSpan.FromHours(1));
		await scheduler.DrainAsync(cancellationToken);

		var occurrence = Assert.Single(
			await storage.QueryJobsAsync(new() { JobName = "cleanup", Take = 100 }, cancellationToken),
			job => job.RecurringKey is not null
		);
		Assert.Equal(JobState.AwaitingContinuation, occurrence.State);
		Assert.Equal(1, occurrence.RemainingDependencies);
		var edge = Assert.Single(storage.RecurringMaterializations[^1].Dependencies ?? []);
		Assert.Equal(existingHandle, edge.ParentJobHandle);
		Assert.Equal(occurrence.JobHandle, edge.ChildJobHandle);
		Assert.Equal(TimeSpan.Zero, edge.Delay);
	}

	[Fact]
	public async Task OverlapQueueOverTwoCronOccurrencesCreatesEachMissingJobInSequence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new CapturingJobStorage(new FakeTimeProvider(Start));
		var clock = new FakeTimeProvider(Start);
		var existingHandle = JobHandle.FromString("existing-run");
		await storage.EnqueueAsync(new()
		{
			JobHandle = existingHandle,
			JobName = "cleanup",
			QueueName = "default",
			Payload = "{}",
			State = JobState.Active,
			DueAt = Start,
			CreatedAt = Start,
			WorkerId = "worker",
			LeaseExpiresAt = Start.AddHours(4),
		}, cancellationToken);

		var scheduler = BuildScheduler(
			storage,
			clock,
			"cleanup",
			"0 * * * *",
			overlapPolicy: OverlapPolicy.Queue,
			misfireHandlingMode: MisfireHandlingMode.EnqueueAll
		);

		await scheduler.DrainAsync(cancellationToken);

		clock.Advance(TimeSpan.FromHours(2));
		await scheduler.DrainAsync(cancellationToken);

		clock.Advance(TimeSpan.FromHours(1));
		await scheduler.DrainAsync(cancellationToken);

		Assert.Equivalent(
			new[]
			{
				new { Dependencies = new List<JobHandle> { storage.Jobs[0].JobHandle }, DueAt = Start.AddHours(1), State = JobState.AwaitingContinuation, },
				new { Dependencies = new List<JobHandle> { storage.Jobs[1].JobHandle }, DueAt = Start.AddHours(2), State = JobState.AwaitingContinuation, },
				new { Dependencies = new List<JobHandle> { storage.Jobs[2].JobHandle }, DueAt = Start.AddHours(3), State = JobState.AwaitingContinuation, },
			},
			storage.RecurringMaterializations
				.Select(rm => new
				{
					Dependencies = rm.Dependencies?.Select(d => d.ParentJobHandle).ToList() ?? [],
					rm.Job.DueAt,
					rm.Job.State,
				})
		);

		Assert.Equal(
			Start.AddHours(3),
			storage.RecurringSchedules["cleanup"].LastRunAt
		);

		Assert.Equal(
			Start.AddHours(4),
			storage.RecurringSchedules["cleanup"].NextRunAt
		);
	}

	[Fact]
	public async Task RestartImmediatelyEnqueuesOneOccurrenceThatFellDuringDowntime()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = new JobTestHarness(Start);
		var storage = harness.Storage;

		var first = BuildScheduler(storage, harness.TimeProvider, "hourly", "0 * * * *");
		await first.DrainAsync(cancellationToken);
		var scheduled = await GetSchedule(storage, "hourly", cancellationToken);
		Assert.Equal(Start.AddHours(1), scheduled.NextRunAt);

		// The process is down across the 11:00 occurrence and restarts at 11:05 with fresh scheduler
		// state. The default mode coalesces the missed 11:00 occurrence into one invocation due immediately.
		harness.TimeProvider.SetUtcNow(Start.AddHours(1).AddMinutes(5));
		var restarted = BuildScheduler(storage, harness.TimeProvider, "hourly", "0 * * * *");
		await restarted.DrainAsync(cancellationToken);

		var materialized = await storage.QueryJobsAsync(new() { JobName = "hourly", Take = 100 }, cancellationToken);
		var occurrence = Assert.Single(materialized);
		Assert.Equal(Start.AddHours(1).AddMinutes(5), occurrence.DueAt);
	}

	[Theory]
	[InlineData(MisfireHandlingMode.EnqueueAll, 3)]
	[InlineData(MisfireHandlingMode.EnqueueOne, 1)]
	[InlineData(MisfireHandlingMode.EnqueueNone, 0)]
	public async Task MisfireHandlingControlsMissedOccurrences(
		MisfireHandlingMode mode,
		int expectedMaterializations
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new CapturingJobStorage(new FakeTimeProvider(Start));
		var clock = new FakeTimeProvider(Start);
		var logger = new CapturingSchedulerLogger();
		var scheduler = BuildScheduler(
			storage,
			clock,
			"cleanup",
			"0 * * * *",
			misfireHandlingMode: mode,
			logger: logger
		);

		await scheduler.DrainAsync(cancellationToken);
		clock.Advance(TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(5)));
		await scheduler.DrainAsync(cancellationToken);

		Assert.Equal(expectedMaterializations, storage.RecurringMaterializations.Count);
		Assert.Equal(Start.AddHours(4), storage.RecurringSchedules["cleanup"].NextRunAt);
		Assert.Contains(
			logger.Entries,
			entry => entry.Level == LogLevel.Information
				&& entry.Message.Contains("missed 3 occurrences", StringComparison.Ordinal)
				&& entry.Message.Contains(mode.ToString(), StringComparison.Ordinal)
		);

		if (mode == MisfireHandlingMode.EnqueueAll)
		{
			Assert.Equal(
				[Start.AddHours(1), Start.AddHours(2), Start.AddHours(3)],
				storage.RecurringMaterializations.Select(x => x.Job.DueAt)
			);
		}
		else if (mode == MisfireHandlingMode.EnqueueOne)
		{
			Assert.Equal(clock.GetUtcNow(), Assert.Single(storage.RecurringMaterializations).Job.DueAt);
		}
	}

	[Theory]
	[InlineData(MisfireHandlingMode.EnqueueOne)]
	[InlineData(MisfireHandlingMode.EnqueueNone)]
	public async Task OccurrenceExactlyAtNowIsNotAMisfire(MisfireHandlingMode mode)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new CapturingJobStorage(new FakeTimeProvider(Start));
		var clock = new FakeTimeProvider(Start);
		var logger = new CapturingSchedulerLogger();
		var scheduler = BuildScheduler(
			storage,
			clock,
			"cleanup",
			"0 * * * *",
			misfireHandlingMode: mode,
			logger: logger
		);

		await scheduler.DrainAsync(cancellationToken);
		clock.Advance(TimeSpan.FromHours(1));
		await scheduler.DrainAsync(cancellationToken);

		Assert.Equal(Start.AddHours(1), Assert.Single(storage.RecurringMaterializations).Job.DueAt);
		Assert.DoesNotContain(
			logger.Entries,
			entry => entry.Message.Contains("missed", StringComparison.OrdinalIgnoreCase)
		);
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
		int maxAttempts = 3,
		MisfireHandlingMode misfireHandlingMode = MisfireHandlingMode.EnqueueOne,
		ILogger<JobSchedulingService>? logger = null
	)
	{
		invoker ??= NoOpInvoker.Instance;
		var services = new ServiceCollection();
		_ = services.AddLogging();
		if (logger is not null)
			_ = services.AddSingleton(logger);
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
			MisfireHandlingMode = misfireHandlingMode,
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

	private sealed class CapturingSchedulerLogger : ILogger<JobSchedulingService>
	{
		public List<(LogLevel Level, string Message)> Entries { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter
		) => Entries.Add((logLevel, formatter(state, exception)));
	}
}
