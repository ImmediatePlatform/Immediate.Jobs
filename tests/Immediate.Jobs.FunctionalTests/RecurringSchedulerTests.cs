using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests;

#pragma warning disable CS1591
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
	public async Task OverlapSkipDetectsAnActiveRunHiddenBehindALongerJobName()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(Start);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.InitializeAsync(cancellationToken);

		// "cleanup-archive" is a substring match for "cleanup" and is newer, so it sorts first in the
		// dashboard query. A single-row substring search would return only this record and conclude that
		// "cleanup" is idle.
		await AddActiveJob(storage, "cleanup", Start, clock, cancellationToken);
		await AddActiveJob(storage, "cleanup-archive", Start.AddMinutes(1), clock, cancellationToken);

		var scheduler = BuildScheduler(storage, clock, "cleanup", "0 * * * *");
		await scheduler.DrainAsync(cancellationToken);
		clock.SetUtcNow(Start.AddHours(1));
		await scheduler.DrainAsync(cancellationToken);

		var materialized = await storage.QueryJobsAsync(
			new() { JobName = "cleanup", Take = 100 },
			cancellationToken
		);
		var occurrence = Assert.Single(materialized, job => job.RecurringKey is not null);
		Assert.Equal(JobState.Cancelled, occurrence.State);
	}

	[Fact]
	public async Task RestartKeepsAnOccurrenceThatFellDuringDowntime()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(Start);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.InitializeAsync(cancellationToken);

		var first = BuildScheduler(storage, clock, "hourly", "0 * * * *");
		await first.DrainAsync(cancellationToken);
		var scheduled = await GetSchedule(storage, "hourly", cancellationToken);
		Assert.Equal(Start.AddHours(1), scheduled.NextRunAt);

		// The process is down across the 11:00 occurrence and restarts at 11:05 with fresh scheduler
		// state. Recomputing from "now" here would advance the schedule to 12:00 and lose 11:00.
		clock.SetUtcNow(Start.AddHours(1).AddMinutes(5));
		var restarted = BuildScheduler(storage, clock, "hourly", "0 * * * *");
		await restarted.DrainAsync(cancellationToken);

		var materialized = await storage.QueryJobsAsync(new() { JobName = "hourly", Take = 100 }, cancellationToken);
		var occurrence = Assert.Single(materialized);
		Assert.Equal(Start.AddHours(1), occurrence.DueAt);
	}

	[Fact]
	public async Task ChangedCronRecomputesTheNextOccurrence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(Start);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.InitializeAsync(cancellationToken);

		var hourly = BuildScheduler(storage, clock, "shifting", "0 * * * *");
		await hourly.DrainAsync(cancellationToken);
		Assert.Equal(Start.AddHours(1), (await GetSchedule(storage, "shifting", cancellationToken)).NextRunAt);

		var daily = BuildScheduler(storage, clock, "shifting", "0 0 * * *");
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
		var clock = new FakeTimeProvider(Start);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.InitializeAsync(cancellationToken);
		var scheduler = BuildScheduler(storage, clock, "analyzer-compatible", cron);

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
		var clock = new FakeTimeProvider(Start);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.InitializeAsync(cancellationToken);
		await storage.UpsertRecurringAsync(new()
		{
			Name = "bad-schedule",
			JobName = "ordinary",
			Cron = cron,
			TimeZone = timeZone,
			IsCodeDefined = false,
			NextRunAt = Start,
		}, cancellationToken);
		await storage.EnqueueAsync(new()
		{
			Id = "ordinary-job",
			JobName = "ordinary",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = Start,
			CreatedAt = Start,
		}, cancellationToken);
		var scheduler = BuildScheduler(storage, clock, "ordinary", cron: null);

		await scheduler.DrainAsync(cancellationToken);

		Assert.Equal(
			JobState.Succeeded,
			(await storage.GetJobStatusAsync("ordinary-job", cancellationToken))!.State
		);
		Assert.Equal(Start, (await GetSchedule(storage, "bad-schedule", cancellationToken)).NextRunAt);
	}

	private static async ValueTask<RecurringJobSchedule> GetSchedule(
		InMemoryJobStorage storage,
		string name,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		return snapshot.Recurring.Single(schedule => string.Equals(schedule.Name, name, StringComparison.Ordinal));
	}

	private static ValueTask AddActiveJob(
		InMemoryJobStorage storage,
		string jobName,
		DateTimeOffset createdAt,
		TimeProvider clock,
		CancellationToken cancellationToken
	) => storage.EnqueueAsync(new()
	{
		Id = Guid.NewGuid().ToString("N"),
		JobName = jobName,
		Payload = "{}",
		State = JobState.Active,
		DueAt = createdAt,
		CreatedAt = createdAt,
		WorkerId = "worker",
		LeaseExpiresAt = clock.GetUtcNow().AddMinutes(30),
	}, cancellationToken);

	private static JobSchedulerService BuildScheduler(
		InMemoryJobStorage storage,
		TimeProvider clock,
		string jobName,
		string? cron
	)
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton(clock);
		_ = services.AddImmediateJobsCore(options =>
		{
			_ = options.UseStorage(_ => storage).UseDistributed();
			options.MaxParallelJobs = 1;
		});
		_ = services.AddSingleton(new JobDefinition
		{
			Name = jobName,
			Cron = cron,
			Invoker = NoOpInvoker.Instance,
			JobType = typeof(NoOpInvoker),
		});

		var provider = services.BuildServiceProvider();
		return provider.GetRequiredService<JobSchedulerService>();
	}

	private sealed class NoOpInvoker : IJobInvoker
	{
		public static NoOpInvoker Instance { get; } = new();

		public ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution) =>
			ValueTask.CompletedTask;
	}
}
#pragma warning restore CS1591
