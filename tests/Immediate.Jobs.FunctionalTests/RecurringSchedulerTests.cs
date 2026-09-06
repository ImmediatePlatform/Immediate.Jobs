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

	[Theory]
	[InlineData(JobState.Pending)]
	[InlineData(JobState.Active)]
	public async Task OverlapSkipDetectsAPresentRun(JobState existingState)
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		await using var harness = CreateHarness("cleanup", "0 * * * *");
		var storage = harness.Storage;

		await storage.EnqueueAsync(
			new()
			{
				JobHandle = JobHandle.FromString(Guid.NewGuid().ToString("N")),
				JobName = "cleanup",
				QueueName = "default",
				Payload = "{}",
				State = existingState,
				DueAt = existingState == JobState.Active ? Start : Start.AddHours(2),
				CreatedAt = Start,
				WorkerId = existingState == JobState.Active ? "worker" : null,
				LeaseExpiresAt = existingState == JobState.Active ? Start.AddHours(1) : null,
			},
			cancellationToken
		);

		await harness.DrainAsync(cancellationToken);
		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromHours(1), cancellationToken);

		var occurrence = Assert.Single(storage.Jobs, job => job.RecurringKey is not null);
		Assert.Equal(JobState.Skipped, occurrence.State);
	}

	[Fact]
	public async Task OverlapSkipOverTwoCronOccurrencesCreatesEachMissingSkippedJobInSequence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		await using var harness = CreateHarness(
			"cleanup",
			"0 * * * *",
			OverlapPolicy.Skip,
			MisfireHandlingMode.EnqueueAll
		);

		var storage = harness.Storage;
		var clock = harness.TimeProvider;

		await storage.EnqueueAsync(
			new()
			{
				JobHandle = JobHandle.FromString("existing-run"),
				JobName = "cleanup",
				QueueName = "default",
				Payload = "{}",
				State = JobState.Active,
				DueAt = Start,
				CreatedAt = Start,
				WorkerId = "worker",
				LeaseExpiresAt = Start.AddHours(4),
			},
			cancellationToken
		);

		await harness.DrainAsync(cancellationToken);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromHours(2), cancellationToken);
		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromHours(1), cancellationToken);

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

		await using var harness = CreateHarness(
			"cleanup",
			"0 * * * *",
			OverlapPolicy.Queue,
			MisfireHandlingMode.EnqueueAll
		);

		var storage = harness.Storage;
		var clock = harness.TimeProvider;

		var existingHandle = JobHandle.FromString("existing-run");

		await storage.EnqueueAsync(
			new()
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
			},
			cancellationToken
		);

		await harness.DrainAsync(cancellationToken);
		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromHours(1), cancellationToken);

		var occurrence = Assert.Single(storage.Jobs, job => job.RecurringKey is not null);
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

		await using var harness = CreateHarness(
			"cleanup",
			"0 * * * *",
			OverlapPolicy.Queue,
			MisfireHandlingMode.EnqueueAll
		);

		var storage = harness.Storage;
		var clock = harness.TimeProvider;

		var existingHandle = JobHandle.FromString("existing-run");

		await storage.EnqueueAsync(
			new()
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
			},
			cancellationToken
		);

		await harness.DrainAsync(cancellationToken);
		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromHours(2), cancellationToken);
		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromHours(1), cancellationToken);

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

	public static TheoryData<bool, bool, MisfireHandlingMode, int> MisfireHandlingCases()
	{
		return
		[
			(false, false, MisfireHandlingMode.EnqueueAll, 3),
			(false, true, MisfireHandlingMode.EnqueueAll, 3),
			(true, false, MisfireHandlingMode.EnqueueAll, 3),
			(true, true, MisfireHandlingMode.EnqueueAll, 3),

			(false, false, MisfireHandlingMode.EnqueueOne, 1),
			(false, true, MisfireHandlingMode.EnqueueOne, 1),
			(true, false, MisfireHandlingMode.EnqueueOne, 1),
			(true, true, MisfireHandlingMode.EnqueueOne, 1),

			(false, false, MisfireHandlingMode.EnqueueNone, 1),
			(false, true, MisfireHandlingMode.EnqueueNone, 0),
			(true, false, MisfireHandlingMode.EnqueueNone, 1),
			(true, true, MisfireHandlingMode.EnqueueNone, 0),
		];
	}

	[Theory]
	[MemberData(nameof(MisfireHandlingCases))]
	public async Task MisfireHandlingControlsMissedOccurrences(
		bool restartScheduler,
		bool delayAfterRestart,
		MisfireHandlingMode mode,
		int expectedMaterializations
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		await using var harness = CreateHarness(
			"cleanup",
			"0 * * * *",
			OverlapPolicy.Concurrent,
			misfireHandlingMode: mode
		);

		var storage = harness.Storage;
		var clock = harness.TimeProvider;

		await harness.DrainAsync(cancellationToken);

		if (restartScheduler)
			harness.ResetScheduler();

		var advanceTime = delayAfterRestart switch
		{
			true => TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(5)),
			false => TimeSpan.FromHours(3),
		};

		await harness.AdvanceTimeAndDrainAsync(advanceTime, cancellationToken);

		Assert.Equal(expectedMaterializations, storage.RecurringMaterializations.Count);
		Assert.Equal(Start.AddHours(4), storage.RecurringSchedules["cleanup"].NextRunAt);

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
		else if (!delayAfterRestart && mode == MisfireHandlingMode.EnqueueNone)
		{
			Assert.Equal(clock.GetUtcNow(), Assert.Single(storage.RecurringMaterializations).Job.DueAt);
		}
		else
		{
			Assert.Empty(storage.RecurringMaterializations);
		}
	}

	[Fact]
	public async Task ChangedCronRecomputesTheNextOccurrence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = new JobTestHarness(Start);
		var storage = harness.Storage;

		var hourly = BuildScheduler(storage, harness.TimeProvider, "shifting", "0 * * * *");
		await hourly.DrainAsync(cancellationToken);
		Assert.Equal(Start.AddHours(1), storage.RecurringSchedules["shifting"].NextRunAt);

		var daily = BuildScheduler(storage, harness.TimeProvider, "shifting", "0 0 * * *");
		await daily.DrainAsync(cancellationToken);

		Assert.Equal(
			new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
			storage.RecurringSchedules["shifting"].NextRunAt
		);
	}

	[Theory]
	[MemberData(nameof(RuntimeCronForms))]
	public async Task RuntimeAcceptsAnalyzerCronForms(string cron, DateTimeOffset expectedNextRunAt)
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		await using var harness = CreateHarness("analyzer-compatible", cron);

		var storage = harness.Storage;
		var clock = harness.TimeProvider;

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(
			expectedNextRunAt,
			storage.RecurringSchedules["analyzer-compatible"].NextRunAt
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
		Assert.Equal(Start, storage.RecurringSchedules["bad-schedule"].NextRunAt);
	}

	private static JobSchedulingService BuildScheduler(
		IJobStorage storage,
		TimeProvider clock,
		string jobName,
		string? cron,
		int maxParallelJobs = 1,
		int maxAttempts = 3,
		OverlapPolicy overlapPolicy = OverlapPolicy.Skip,
		MisfireHandlingMode misfireHandlingMode = MisfireHandlingMode.EnqueueOne
	)
	{
		var invoker = NoOpInvoker.Instance;
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton(clock);
		services.AddImmediateJobsCore()
			.ConfigureWorkers(o => o.WorkerCount = maxParallelJobs)
			.ConfigureStorage(o => o.UseStorage(_ => storage).UseDistributed());

		services.AddSingleton(new JobDefinition
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

	private static JobTestHarness CreateHarness(
		string jobName,
		string? cron,
		OverlapPolicy overlapPolicy = OverlapPolicy.Skip,
		MisfireHandlingMode misfireHandlingMode = MisfireHandlingMode.EnqueueOne
	)
	{
		return new(
			Start,
			services => services.AddSingleton(
				new JobDefinition
				{
					Name = jobName,
					Cron = cron,
					Invoker = NoOpInvoker.Instance,
					JobType = typeof(NoOpInvoker),
					OverlapPolicy = overlapPolicy,
					MisfireHandlingMode = misfireHandlingMode,
				}
			)
		);
	}

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
