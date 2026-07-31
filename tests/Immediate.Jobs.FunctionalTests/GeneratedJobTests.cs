using System.Diagnostics;
using System.Globalization;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Testing;
using Immediate.Validations.Shared;
using Microsoft.Extensions.DependencyInjection;

[assembly: Behaviors(
	typeof(Immediate.Jobs.FunctionalTests.JobCountingBehavior<,>),
	typeof(ValidationBehavior<,>)
)]

namespace Immediate.Jobs.FunctionalTests;

#pragma warning disable CS1591
public sealed class GeneratedJobTests
{
	[Fact]
	public async Task JobConstrainedBehaviorDoesNotApplyToOrdinaryHandlers()
	{
		var state = new ExecutionState();
		var services = new ServiceCollection();
		_ = services.AddSingleton(state);
		_ = services.AddImmediateJobsFunctionalTestsBehaviors();
		_ = OrdinaryHandler.AddHandlers(services);
		await using var provider = services.BuildServiceProvider();

		var handler = provider.GetRequiredService<OrdinaryHandler.Handler>();
		_ = await handler.HandleAsync(new("ordinary"), TestContext.Current.CancellationToken);

		Assert.Equal(["ordinary"], state.Events);
		Assert.Empty(state.Details);
	}

	[Fact]
	public async Task TypedSchedulerRoundTripsPayloadAndRunsGeneratedPipeline()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var activityListener = ListenForJobActivities();
		var state = new ExecutionState();
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(state);
			_ = services.AddSingleton(new ContextProbe());
			_ = services.AddScoped<PropagationScopeState>();
			_ = services.AddImmediateJobsFunctionalTestsHandlers();
			_ = services.AddImmediateJobsFunctionalTestsBehaviors();
			_ = services.AddImmediateJobsFunctionalTestsJobs();
		});
		await using var enqueueScope = harness.Services.CreateAsyncScope();
		var scheduler = enqueueScope.ServiceProvider.GetRequiredService<RecordMessageJob.Scheduler>();

		var id = await scheduler.EnqueueAsync(new("hello"), cancellationToken);
		var enqueued = await harness.AssertEnqueuedAsync<RecordMessageJob.Payload>(id, JobState.Pending, cancellationToken);
		Assert.Equal("hello", enqueued.Payload.Message);
		Assert.Null(enqueued.Payload.JobDetails);
		Assert.DoesNotContain("jobDetails", enqueued.Record.Payload, StringComparison.OrdinalIgnoreCase);
		Assert.Equal("messages", enqueued.Record.QueueName);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(["before:hello", "job:hello", "after:hello"], state.Events);
		var details = Assert.Single(state.Details);
		Assert.Equal(id.Id, details.JobId);
		Assert.Equal("record-message", details.JobName);
		Assert.Equal("messages", details.QueueName);
		Assert.Equal(1, details.Attempt);
		Assert.Equal(enqueued.Record.CreatedAt, details.CreatedAt);
		Assert.Equal(enqueued.Record.DueAt, details.ScheduledAt);
		var completed = await harness.GetJobAsync(id, cancellationToken);
		Assert.Equal(JobState.Succeeded, completed.State);
		Assert.Equal(32, completed.ExecutionTraceId?.Length);
		Assert.Equal(16, completed.ExecutionSpanId?.Length);
		_ = Assert.NotNull(completed.ExecutionStartedAt);
	}

	[Fact]
	public async Task GeneratedMetadataRoundTripsArraysListsAndDictionaries()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new CollectionExecutionState();
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(state);
			_ = services.AddSingleton(new ExecutionState());
			_ = services.AddSingleton(new ContextProbe());
			_ = services.AddScoped<PropagationScopeState>();
			_ = services.AddImmediateJobsFunctionalTestsHandlers();
			_ = services.AddImmediateJobsFunctionalTestsBehaviors();
			_ = services.AddImmediateJobsFunctionalTestsJobs();
		});
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<CollectionPayloadJob.Scheduler>();
		var payload = new CollectionPayloadJob.Payload(
			[new(1), new(2)],
			[new(3), new(4)],
			[new(5), new(6)],
			[new(7), new(8)],
			[new(9), new(0)],
			new Dictionary<string, CollectionItem> { ["five"] = new(5) },
			new Dictionary<string, CollectionItem> { ["six"] = new(6) },
			new Dictionary<string, CollectionItem> { ["seven"] = new(7) }
		);

		var handle = await scheduler.EnqueueAsync(payload, cancellationToken);
		await harness.DrainAsync(cancellationToken);

		var received = Assert.IsType<CollectionPayloadJob.Payload>(state.Payload);
		Assert.Equal([1, 2], received.Array.Select(static item => item.Value));
		Assert.Equal([3, 4], received.List.Select(static item => item.Value));
		Assert.Equal(5, received.Dictionary["five"].Value);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(handle, cancellationToken)).State);
	}

	[Fact]
	public async Task GeneratedMetadataRoundTripsPropertyBackedPayloads()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new PropertyBackedPayloadState();
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(state);
			_ = services.AddSingleton(new ExecutionState());
			_ = services.AddSingleton(new ContextProbe());
			_ = services.AddScoped<PropagationScopeState>();
			_ = services.AddImmediateJobsFunctionalTestsHandlers();
			_ = services.AddImmediateJobsFunctionalTestsBehaviors();
			_ = services.AddImmediateJobsFunctionalTestsJobs();
		});
		await using var scope = harness.Services.CreateAsyncScope();
		var optionalScheduler = scope.ServiceProvider.GetRequiredService<PropertyBackedPayloadJob.Scheduler>();
		var requiredScheduler = scope.ServiceProvider.GetRequiredService<RequiredPropertyBackedPayloadJob.Scheduler>();

		var optionalHandle = await optionalScheduler.EnqueueAsync(new() { Value = 42 }, cancellationToken);
		var requiredHandle = await requiredScheduler.EnqueueAsync(new() { Value = 43 }, cancellationToken);
		await harness.DrainAsync(cancellationToken);

		Assert.Equal(42, state.OptionalValue);
		Assert.Equal(43, state.RequiredValue);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(optionalHandle, cancellationToken)).State);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(requiredHandle, cancellationToken)).State);
	}

	[Fact]
	public async Task FailedJobRetriesAfterGeneratedBackoff()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var activityListener = ListenForJobActivities();
		var state = new ExecutionState { FailuresRemaining = 1 };
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(state);
			_ = services.AddSingleton(new ContextProbe());
			_ = services.AddScoped<PropagationScopeState>();
			_ = services.AddImmediateJobsFunctionalTestsHandlers();
			_ = services.AddImmediateJobsFunctionalTestsBehaviors();
			_ = services.AddImmediateJobsFunctionalTestsJobs();
		});
		await using var enqueueScope = harness.Services.CreateAsyncScope();
		var scheduler = enqueueScope.ServiceProvider.GetRequiredService<RetryOnceJob.Scheduler>();
		var id = await scheduler.EnqueueAsync(new(42), cancellationToken);

		await harness.DrainAsync(cancellationToken);
		var failedAttempt = await harness.GetJobAsync(id, cancellationToken);
		Assert.Equal(JobState.Scheduled, failedAttempt.State);
		_ = Assert.IsType<string>(failedAttempt.ExecutionTraceId);
		var firstSpanId = Assert.IsType<string>(failedAttempt.ExecutionSpanId);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromSeconds(1), cancellationToken);
		var completed = await harness.GetJobAsync(id, cancellationToken);
		Assert.Equal(JobState.Succeeded, completed.State);
		Assert.Equal(2, completed.Attempt);
		_ = Assert.IsType<string>(completed.ExecutionTraceId);
		var secondSpanId = Assert.IsType<string>(completed.ExecutionSpanId);
		Assert.NotEqual(firstSpanId, secondSpanId);
		Assert.Equal([1, 2], state.Details
			.Where(details => string.Equals(details.JobId, id.Id, StringComparison.Ordinal))
			.Select(details => details.Attempt));
	}

	[Fact]
	public async Task JobTimeoutCancelsTheInvocationAndPersistsFailure()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeoutState = new TimeoutExecutionState();
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(timeoutState);
			_ = services.AddSingleton(new ExecutionState());
			_ = services.AddSingleton(new ContextProbe());
			_ = services.AddScoped<PropagationScopeState>();
			_ = services.AddImmediateJobsFunctionalTestsHandlers();
			_ = services.AddImmediateJobsFunctionalTestsBehaviors();
			_ = services.AddImmediateJobsFunctionalTestsJobs();
		});
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<TimeoutJob.Scheduler>();
		var handle = await scheduler.EnqueueAsync(default, cancellationToken);

		var drain = harness.DrainAsync(cancellationToken).AsTask();
		await timeoutState.Started.Task.WaitAsync(cancellationToken);
		harness.TimeProvider.Advance(TimeSpan.FromSeconds(1));
		await drain.WaitAsync(cancellationToken);

		var failed = await harness.GetJobAsync(handle, cancellationToken);
		Assert.Equal(JobState.Failed, failed.State);
		Assert.Equal(1, failed.Attempt);
		Assert.Contains("TaskCanceledException", failed.LastError, StringComparison.Ordinal);
	}

	private static ActivityListener ListenForJobActivities()
	{
		var listener = new ActivityListener
		{
			ShouldListenTo = ShouldListenToJobs,
			Sample = SampleJobActivity,
			SampleUsingParentId = SampleJobActivityWithParentId,
		};
		ActivitySource.AddActivityListener(listener);
		return listener;
	}

	private static bool ShouldListenToJobs(ActivitySource source) => string.Equals(source.Name, "Immediate.Jobs", StringComparison.Ordinal);

	private static ActivitySamplingResult SampleJobActivity(ref ActivityCreationOptions<ActivityContext> options)
	{
		_ = options;
		return ActivitySamplingResult.AllDataAndRecorded;
	}

	private static ActivitySamplingResult SampleJobActivityWithParentId(ref ActivityCreationOptions<string> options)
	{
		_ = options;
		return ActivitySamplingResult.AllDataAndRecorded;
	}

	[Fact]
	public async Task ValueTypeRequestReceivesJobDetailsWithoutBoxingAwayTheAssignment()
	{
		var state = new ExecutionState();
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(state);
			_ = services.AddSingleton(new ContextProbe());
			_ = services.AddScoped<PropagationScopeState>();
			_ = services.AddImmediateJobsFunctionalTestsHandlers();
			_ = services.AddImmediateJobsFunctionalTestsBehaviors();
			_ = services.AddImmediateJobsFunctionalTestsJobs();
		});
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<ValueTypeJob.Scheduler>();

		var id = await scheduler.EnqueueAsync(new(7), TestContext.Current.CancellationToken);
		await harness.DrainAsync(TestContext.Current.CancellationToken);

		Assert.Equal(["before:7", "job:7", "after:7"], state.Events);
		Assert.Equal(id.Id, Assert.Single(state.Details).JobId);
	}

	[Fact]
	public async Task PlainRequestRunsWithoutJobDetailsCapability()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new ExecutionState();
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(state);
			_ = services.AddSingleton(new ContextProbe());
			_ = services.AddScoped<PropagationScopeState>();
			_ = services.AddImmediateJobsFunctionalTestsHandlers();
			_ = services.AddImmediateJobsFunctionalTestsBehaviors();
			_ = services.AddImmediateJobsFunctionalTestsJobs();
		});
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<PlainRequestJob.Scheduler>();

		var id = await scheduler.EnqueueAsync(new("hello"), cancellationToken);
		var enqueued = await harness.AssertEnqueuedAsync<PlainRequestJob.Payload>(id, JobState.Pending, cancellationToken);
		Assert.Equal("hello", enqueued.Payload.Message);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(["plain:hello"], state.Events);
		Assert.Empty(state.Details);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(id, cancellationToken)).State);
	}
}

public sealed class ExecutionState
{
	public IList<string> Events { get; } = [];
	public IList<JobDetails> Details { get; } = [];
	public int FailuresRemaining { get; set; }
}

public sealed class TimeoutExecutionState
{
	public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class CollectionExecutionState
{
	public CollectionPayloadJob.Payload? Payload { get; set; }
}

public sealed class PropertyBackedPayloadState
{
	public int OptionalValue { get; set; }
	public int RequiredValue { get; set; }
}

public sealed record CollectionItem(int Value);

public sealed class JobCountingBehavior<TRequest, TResponse>(ExecutionState state)
	: Behavior<TRequest, TResponse>
	where TRequest : IJobRequest
{
	public override async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken)
	{
		var value = request switch
		{
			RecordMessageJob.Payload payload => payload.Message,
			RetryOnceJob.Payload payload => payload.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
			ValueTypeJob.Request valueTypeRequest => valueTypeRequest.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
			_ => typeof(TRequest).Name,
		};
		state.Details.Add(request.JobDetails ?? throw new InvalidOperationException("Job details were not populated."));
		state.Events.Add("before:" + value);
		var response = await Next(request, cancellationToken);
		state.Events.Add("after:" + value);
		return response;
	}
}

[QueueDefinition(Name = "messages", Priority = 10, Concurrency = 1)]
public sealed class MessagesLane;

[Handler, Job(Name = "record-message"), UsesQueue<MessagesLane>]
public sealed partial class RecordMessageJob(ExecutionState state)
{
	public sealed record Payload(string Message) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		state.Events.Add("job:" + payload.Message);
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "retry-once", MaxAttempts = 2, Backoff = BackoffStrategy.Fixed, BackoffBase = "00:00:01")]
public sealed partial class RetryOnceJob(ExecutionState state)
{
	public sealed record Payload(int Value) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = payload;
		_ = cancellationToken;
		if (state.FailuresRemaining-- > 0)
			throw new InvalidOperationException("Retry me");
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "value-type")]
public sealed partial class ValueTypeJob(ExecutionState state)
{
	public record struct Request(int Value) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private ValueTask HandleAsync(Request request, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		_ = request.JobDetails ?? throw new InvalidOperationException("Job details were not populated.");
		state.Events.Add(string.Create(CultureInfo.InvariantCulture, $"job:{request.Value}"));
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "plain-request")]
public sealed partial class PlainRequestJob(ExecutionState state)
{
	public sealed record Payload(string Message);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		state.Events.Add("plain:" + payload.Message);
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "timeout", MaxAttempts = 1, Timeout = "00:00:01")]
public sealed partial class TimeoutJob(TimeoutExecutionState? state = null)
{
	private async ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken)
	{
		_ = payload;
		if (state is null)
			return;
		_ = state.Started.TrySetResult();
		await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
	}
}

#pragma warning disable CA1002, CA1819, MA0016 // The payload intentionally exercises List<T> and array metadata generation.
[Handler, Job(Name = "collection-payload")]
public sealed partial class CollectionPayloadJob(CollectionExecutionState? state = null)
{
	public sealed record Payload(
		CollectionItem[] Array,
		List<CollectionItem> List,
		IList<CollectionItem> IList,
		IReadOnlyList<CollectionItem> IReadOnlyList,
		IEnumerable<CollectionItem> IEnumerable,
		Dictionary<string, CollectionItem> Dictionary,
		IDictionary<string, CollectionItem> IDictionary,
		IReadOnlyDictionary<string, CollectionItem> IReadOnlyDictionary
	);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		state?.Payload = payload;
		return ValueTask.CompletedTask;
	}
}
#pragma warning restore CA1002, CA1819, MA0016

[Handler, Job(Name = "property-backed-payload")]
public sealed partial class PropertyBackedPayloadJob(PropertyBackedPayloadState? state = null)
{
	public sealed record Payload
	{
		public int Value { get; init; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (state is not null)
			state.OptionalValue = payload.Value;
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "required-property-backed-payload")]
public sealed partial class RequiredPropertyBackedPayloadJob(PropertyBackedPayloadState? state = null)
{
	public sealed record Payload
	{
		public required int Value { get; set; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (state is not null)
			state.RequiredValue = payload.Value;
		return ValueTask.CompletedTask;
	}
}

[Handler]
public sealed partial class OrdinaryHandler(ExecutionState state)
{
	public sealed record Request(string Value);

	private ValueTask HandleAsync(Request request, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		state.Events.Add(request.Value);
		return ValueTask.CompletedTask;
	}
}
#pragma warning restore CS1591
