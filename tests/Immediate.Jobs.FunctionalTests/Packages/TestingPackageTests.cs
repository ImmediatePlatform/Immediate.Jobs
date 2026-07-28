using System.Text.Json.Serialization;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.FunctionalTests.Packages;

#pragma warning disable CS1591
public sealed class TestingPackageTests
{
	[Fact]
	public async Task CaptureOnlySchedulerRecordsTypedCalls()
	{
		var scheduler = new CaptureOnlyJobScheduler<TestPayload>();
		var payload = new TestPayload("hello");

		var id = await scheduler.EnqueueAsync(
			payload,
			groupId: "tenant-a",
			cancellationToken: TestContext.Current.CancellationToken
		);

		var capture = Assert.Single(scheduler.Captures);
		Assert.Equal(id.Id, capture.Id);
		Assert.Equal(payload, capture.Payload);
		Assert.Equal("tenant-a", capture.GroupId);
	}

	[Fact]
	public async Task ExistingSchedulerImplementationsKeepCancellationTokenCalls()
	{
		IJobScheduler<TestPayload> scheduler = new LegacyScheduler();

#pragma warning disable xUnit1051 // The default literal is the source-compatibility case under test.
		_ = await scheduler.EnqueueAsync(new("legacy"), default);
#pragma warning restore xUnit1051
		_ = await scheduler.EnqueueAsync(new("ungrouped"), groupId: null, cancellationToken: CancellationToken.None);
		_ = await Assert.ThrowsAsync<NotSupportedException>(
			() => scheduler.EnqueueAsync(
				new("grouped"),
				groupId: "tenant-a",
				cancellationToken: CancellationToken.None
			).AsTask()
		);
	}

	[Fact]
	public async Task SchedulerNormalizesAndValidatesFairQueueGroupIds()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = new JobTestHarness();
		var scheduler = new TestScheduler(
			harness.Storage,
			harness.Services.GetRequiredService<IJobSerializer>(),
			harness.TimeProvider,
			harness.Services.GetRequiredService<IIdGenerator>()
		);

		var grouped = await scheduler.EnqueueAsync(
			new("grouped"),
			groupId: "tenant-a",
			cancellationToken: cancellationToken
		);
		var ungrouped = await scheduler.EnqueueAsync(
			new("ungrouped"),
			groupId: " \t ",
			cancellationToken: cancellationToken
		);

		Assert.Equal("tenant-a", (await harness.GetJobAsync(grouped, cancellationToken)).GroupId);
		Assert.Null((await harness.GetJobAsync(ungrouped, cancellationToken)).GroupId);
		var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
			await scheduler.EnqueueAsync(
				new("too-long"),
				groupId: new string('x', 129),
				cancellationToken: cancellationToken
			)
		);
		Assert.Equal("groupId", exception.ParamName);
	}

	[Fact]
	public void FairQueueOptionsRejectInvalidThresholds()
	{
		var services = new ServiceCollection();

		_ = Assert.Throws<ImmediateJobException>(() => services.AddImmediateJobsCore(options =>
			options.UseFairQueues(fairQueues => fairQueues.ConcurrencyShareThreshold = 0)
		));
		_ = Assert.Throws<ImmediateJobException>(() => services.AddImmediateJobsCore(options =>
			options.UseFairQueues(fairQueues => fairQueues.MinInflightForNoisy = 0)
		));
	}

	[Fact]
	public async Task GroupedJobsLogOneWarningWhenFairQueuesAreDisabled()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var logger = new CapturingSchedulerLogger();
		var counter = new InvocationCounter();
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(counter);
			_ = services.AddSingleton(new JobDefinition
			{
				Name = "test-job",
				JobType = typeof(TestingPackageTests),
				Invoker = new TestInvoker(),
			});
			_ = services.AddSingleton<ILogger<JobSchedulerService>>(logger);
		});
		var scheduler = new TestScheduler(
			harness.Storage,
			harness.Services.GetRequiredService<IJobSerializer>(),
			harness.TimeProvider,
			harness.Services.GetRequiredService<IIdGenerator>()
		);

		_ = await scheduler.EnqueueAsync(
			new("first"),
			groupId: "tenant-a",
			cancellationToken: cancellationToken
		);
		await harness.DrainAsync(cancellationToken);
		_ = await scheduler.EnqueueAsync(
			new("second"),
			groupId: "tenant-b",
			cancellationToken: cancellationToken
		);
		await harness.DrainAsync(cancellationToken);

		Assert.Equal(2, counter.Count);
		Assert.Equal(1, logger.EventIds.Count(static eventId => eventId == 8));
	}

	[Fact]
	public async Task HarnessAdvancesTimeDrainsAndAssertsPayload()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var counter = new InvocationCounter();
		var definition = new JobDefinition
		{
			Name = "test-job",
			JobType = typeof(TestingPackageTests),
			Invoker = new TestInvoker(),
		};
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(counter);
			_ = services.AddSingleton(definition);
		});
		var scheduler = new TestScheduler(
			harness.Storage,
			harness.Services.GetRequiredService<IJobSerializer>(),
			harness.TimeProvider,
			harness.Services.GetRequiredService<IIdGenerator>()
		);

		var id = await scheduler.ScheduleAsync(new("payload"), TimeSpan.FromMinutes(5), cancellationToken);
		var queued = await harness.AssertEnqueuedAsync<TestPayload>(id, JobState.Scheduled, cancellationToken);
		Assert.Equal("payload", queued.Payload.Value);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromMinutes(5), cancellationToken);

		Assert.Equal(1, counter.Count);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(id, cancellationToken)).State);
	}

	[Fact]
	public async Task ContinuationReleaseAssertionRejectsCancellation()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = new JobTestHarness();
		var graphStorage = Assert.IsAssignableFrom<IJobGraphStorage>(harness.Storage);
		var parent = CreateRawJob("parent");
		var child = CreateRawJob("child") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await graphStorage.EnqueueAsync(parent, cancellationToken);
		await graphStorage.EnqueueContinuationAsync(
			child,
			[new() { ChildJobId = child.Id, ParentJobId = parent.Id }],
			cancellationToken
		);
		_ = Assert.Single(await graphStorage.AcquireDueJobsAsync(new()
		{
			WorkerId = "worker",
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = 1,
			Queues =
			[
				new()
				{
					QueueName = JobQueueDefinition.DefaultName,
					Capacity = 1,
					JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [parent.JobName] = 1 },
				},
			],
		}, cancellationToken));
		await graphStorage.FailAsync(parent.Id, "worker", "broken", nextRetryAt: null, cancellationToken);

		var exception = await Assert.ThrowsAsync<JobTestAssertionException>(
			() => harness.AssertContinuationReleasedAfterAsync(new(parent.Id), new(child.Id), cancellationToken).AsTask()
		);
		Assert.Contains("cancelled", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	public sealed record TestPayload(string Value);

	private sealed class InvocationCounter
	{
		public int Count { get; set; }
	}

	private static JobRecord CreateRawJob(string id) => new()
	{
		Id = id,
		JobName = "raw-job",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = DateTimeOffset.UnixEpoch,
		CreatedAt = DateTimeOffset.UnixEpoch,
	};

	private sealed class TestScheduler(
		IJobStorage storage,
		IJobSerializer serializer,
		TimeProvider timeProvider,
		IIdGenerator idGenerator
	)
		: JobScheduler<TestPayload>(
			storage,
			serializer,
			timeProvider,
			idGenerator,
			"test-job",
			JobQueueDefinition.DefaultName,
			static options => new TestingJsonContext(options).TestPayload
		);

	private sealed class LegacyScheduler : IJobScheduler<TestPayload>
	{
		public ValueTask<JobHandle> EnqueueAsync(
			TestPayload payload,
			CancellationToken cancellationToken = default
		) => ValueTask.FromResult(new JobHandle("legacy"));

		public ValueTask<JobHandle> ScheduleAsync(
			TestPayload payload,
			TimeSpan delay,
			CancellationToken cancellationToken = default
		) => ValueTask.FromResult(new JobHandle("legacy"));

		public ValueTask<JobHandle> ScheduleAtAsync(
			TestPayload payload,
			DateTimeOffset runAt,
			CancellationToken cancellationToken = default
		) => ValueTask.FromResult(new JobHandle("legacy"));
	}

	private sealed class TestInvoker : IJobInvoker
	{
		public ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution)
		{
			scopedServices.GetRequiredService<InvocationCounter>().Count++;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class CapturingSchedulerLogger : ILogger<JobSchedulerService>
	{
		public List<int> EventIds { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter
		) => EventIds.Add(eventId.Id);
	}
}

[JsonSerializable(typeof(TestingPackageTests.TestPayload))]
internal sealed partial class TestingJsonContext : JsonSerializerContext;
#pragma warning restore CS1591
