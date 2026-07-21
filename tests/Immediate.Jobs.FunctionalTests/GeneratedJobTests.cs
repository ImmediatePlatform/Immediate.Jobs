using Immediate.Jobs.Testing;
using Immediate.Handlers.Shared;
using Microsoft.Extensions.DependencyInjection;

[assembly: Immediate.Jobs.JobBehaviors(typeof(Immediate.Jobs.FunctionalTests.CountingBehavior<>))]
[assembly: Behaviors(typeof(Immediate.Jobs.FunctionalTests.HandlerCountingBehavior<,>))]

namespace Immediate.Jobs.FunctionalTests;

#pragma warning disable CS1591
public sealed class GeneratedJobTests
{
	[Fact]
	public async Task TypedSchedulerRoundTripsPayloadAndRunsGeneratedPipeline()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new ExecutionState();
		await using var harness = new JobTestHarness(services =>
		{
			services.AddSingleton(state);
			services.AddSingleton(new ContextProbe());
			services.AddScoped<PropagationScopeState>();
			services.AddImmediateJobsFunctionalTestsBehaviors();
			services.AddImmediateJobs();
		});
		await using var enqueueScope = harness.Services.CreateAsyncScope();
		var scheduler = enqueueScope.ServiceProvider.GetRequiredService<RecordMessageJob.Scheduler>();

		var id = await scheduler.Enqueue(new("hello"), cancellationToken);
		var enqueued = await harness.AssertEnqueuedAsync<RecordMessageJob.Payload>(id, JobState.Pending, cancellationToken);
		Assert.Equal("hello", enqueued.Payload.Message);
		Assert.Equal("messages", enqueued.Record.QueueName);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(["before:hello", "handler-before:hello", "job:hello", "handler-after:hello", "after:hello"], state.Events);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(id, cancellationToken)).State);
	}

	[Fact]
	public async Task FailedJobRetriesAfterGeneratedBackoff()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new ExecutionState { FailuresRemaining = 1 };
		await using var harness = new JobTestHarness(services =>
		{
			services.AddSingleton(state);
			services.AddSingleton(new ContextProbe());
			services.AddScoped<PropagationScopeState>();
			services.AddImmediateJobsFunctionalTestsBehaviors();
			services.AddImmediateJobs();
		});
		await using var enqueueScope = harness.Services.CreateAsyncScope();
		var scheduler = enqueueScope.ServiceProvider.GetRequiredService<RetryOnceJob.Scheduler>();
		var id = await scheduler.Enqueue(new(42), cancellationToken);

		await harness.DrainAsync(cancellationToken);
		Assert.Equal(JobState.Scheduled, (await harness.GetJobAsync(id, cancellationToken)).State);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromSeconds(1), cancellationToken);
		var completed = await harness.GetJobAsync(id, cancellationToken);
		Assert.Equal(JobState.Succeeded, completed.State);
		Assert.Equal(2, completed.Attempt);
	}
}

public sealed class ExecutionState
{
	public List<string> Events { get; } = [];
	public int FailuresRemaining { get; set; }
}

public sealed class CountingBehavior<TPayload>(ExecutionState state) : JobBehavior<TPayload>
{
	public override async ValueTask HandleAsync(JobContext<TPayload> context, JobNext<TPayload> next)
	{
		var value = context.Payload switch
		{
			RecordMessageJob.Payload payload => payload.Message,
			RetryOnceJob.Payload payload => payload.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
			_ => typeof(TPayload).Name,
		};
		state.Events.Add("before:" + value);
		await next(context);
		state.Events.Add("after:" + value);
	}
}

public sealed class HandlerCountingBehavior<TRequest, TResponse>(ExecutionState state) : Behavior<TRequest, TResponse>
{
	public override async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken)
	{
		var value = request switch
		{
			RecordMessageJob.Payload payload => payload.Message,
			RetryOnceJob.Payload payload => payload.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
			_ => typeof(TRequest).Name,
		};
		state.Events.Add("handler-before:" + value);
		var response = await Next(request, cancellationToken);
		state.Events.Add("handler-after:" + value);
		return response;
	}
}

[QueueDefinition(Name = "messages", Priority = 10, Concurrency = 1)]
public sealed class MessagesLane;

[Handler, Job("record-message"), UsesQueue<MessagesLane>]
public sealed partial class RecordMessageJob(ExecutionState state)
{
	public sealed record Payload(string Message);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		state.Events.Add("job:" + payload.Message);
		return ValueTask.CompletedTask;
	}
}

[Handler, Job("retry-once", MaxAttempts = 2, Backoff = BackoffStrategy.Fixed, BackoffBase = "00:00:01")]
public sealed partial class RetryOnceJob(ExecutionState state)
{
	public sealed record Payload(int Value);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		if (state.FailuresRemaining-- > 0)
			throw new InvalidOperationException("Retry me");
		return ValueTask.CompletedTask;
	}
}
#pragma warning restore CS1591
