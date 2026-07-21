using Immediate.Handlers.Shared;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.FunctionalTests;

#pragma warning disable CS1591, CA1822
public sealed class ContextPropagationTests
{
	[Fact]
	public async Task ContextRoundTripsIntoJobAndHandlerBehaviorsInDeclaredOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var probe = new ContextProbe();
		await using var harness = CreateHarness(probe);

		Guid id;
		await using (var scope = harness.Services.CreateAsyncScope())
		{
			var ambient = scope.ServiceProvider.GetRequiredService<PropagationScopeState>();
			ambient.TenantId = "tenant-42";
			ambient.CorrelationId = "correlation-7";
			var scheduler = scope.ServiceProvider.GetRequiredService<ContextRoundTripJob.Scheduler>();
			id = await scheduler.Enqueue(new("hello"), cancellationToken);
		}

		var enqueued = await harness.GetJobAsync(id, cancellationToken);
		Assert.Contains("tenant-42", enqueued.Context);
		Assert.Contains("correlation-7", enqueued.Context);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(
			[
				"capture:tenant",
				"capture:correlation",
				"restore:tenant",
				"restore:correlation",
				"handler-behavior:tenant-42/correlation-7",
				"handler:tenant-42/correlation-7:hello",
			],
			probe.Events
		);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(id, cancellationToken)).State);
	}

	[Fact]
	public async Task CaptureFailurePropagatesAndDoesNotEnqueue()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(new());
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<CaptureFailureJob.Scheduler>();

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => scheduler.Enqueue(default, cancellationToken).AsTask()
		);

		Assert.Equal("Capture failed", exception.Message);
		Assert.Empty(await harness.QueryJobsAsync(cancellationToken: cancellationToken));
	}

	[Fact]
	public async Task RestoreFailureRetriesAndEventuallyDeadLetters()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(new());
		Guid id;
		await using (var scope = harness.Services.CreateAsyncScope())
		{
			var scheduler = scope.ServiceProvider.GetRequiredService<RestoreFailureJob.Scheduler>();
			id = await scheduler.Enqueue(default, cancellationToken);
		}

		await harness.DrainAsync(cancellationToken);
		Assert.Equal(JobState.Scheduled, (await harness.GetJobAsync(id, cancellationToken)).State);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromSeconds(1), cancellationToken);
		var failed = await harness.GetJobAsync(id, cancellationToken);
		Assert.Equal(JobState.Failed, failed.State);
		Assert.Equal(2, failed.Attempt);
		Assert.Contains("Restore failed", failed.LastError);
	}

	[Fact]
	public async Task DuplicateKeysThrowEvenWhenExtractorsCaptureNull()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(new());
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<DuplicateContextKeyJob.Scheduler>();

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => scheduler.Enqueue(default, cancellationToken).AsTask()
		);

		Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(await harness.QueryJobsAsync(cancellationToken: cancellationToken));
	}

	[Fact]
	public async Task OrphanedSliceIsLoggedAndJobStillRuns()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var probe = new ContextProbe();
		await using var harness = CreateHarness(probe, captureLogs: true);
		var record = CreateContextRecord(
			harness,
			"{\"removed-extractor\":{\"value\":\"legacy\"}}"
		);
		await harness.Storage.EnqueueAsync(record, cancellationToken);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(record.Id, cancellationToken)).State);
		Assert.Contains(probe.Events, item => item.Contains("removed-extractor", StringComparison.Ordinal));
	}

	[Fact]
	public async Task OrphanedEnvelopeOnJobWithoutExtractorsIsSkipped()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var probe = new ContextProbe();
		await using var harness = CreateHarness(probe, captureLogs: true);
		var now = harness.TimeProvider.GetUtcNow();
		var record = new JobRecord
		{
			Id = Guid.NewGuid(),
			JobName = "record-message",
			QueueName = "messages",
			Payload = "{\"message\":\"orphan\"}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
			Context = "{\"removed-extractor\":{\"value\":\"legacy\"}}",
		};
		await harness.Storage.EnqueueAsync(record, cancellationToken);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(record.Id, cancellationToken)).State);
		Assert.Contains(probe.Events, item => item.Contains("removed-extractor", StringComparison.Ordinal));
	}

	[Fact]
	public async Task StableKeysRestoreRecordsCreatedBeforeExtractorRename()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var probe = new ContextProbe();
		await using var harness = CreateHarness(probe);
		var record = CreateContextRecord(
			harness,
			"{\"tenant\":{\"tenantId\":\"legacy-tenant\"},\"correlation\":{\"correlationId\":\"legacy-correlation\"}}"
		);
		await harness.Storage.EnqueueAsync(record, cancellationToken);

		await harness.DrainAsync(cancellationToken);

		Assert.Contains("handler:legacy-tenant/legacy-correlation:legacy", probe.Events);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(record.Id, cancellationToken)).State);
	}

	[Fact]
	public async Task RecurringMaterializationHasNoRequestContextAndStillRuns()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var probe = new ContextProbe();
		await using var harness = CreateHarness(probe);

		await harness.DrainAsync(cancellationToken);
		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromSeconds(1), cancellationToken);

		var job = Assert.Single(
			await harness.QueryJobsAsync(cancellationToken: cancellationToken),
			candidate => candidate.JobName == "context-cron"
		);
		Assert.Null(job.Context);
		Assert.Equal(JobState.Succeeded, job.State);
		Assert.Contains("cron:no-context", probe.Events);
	}

	[Fact]
	public async Task GeneratedSchedulerIsScopedAndSingletonConsumerCanOpenScope()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(new());

		Assert.Throws<InvalidOperationException>(
			() => harness.Services.GetRequiredService<ContextRoundTripJob.IScheduler>()
		);

		var consumer = harness.Services.GetRequiredService<ScopedSchedulerConsumer>();
		var id = await consumer.EnqueueAsync(cancellationToken);
		Assert.Equal(JobState.Pending, (await harness.GetJobAsync(id, cancellationToken)).State);
	}

	private static JobTestHarness CreateHarness(ContextProbe probe, bool captureLogs = false) => new(services =>
	{
		services.AddSingleton(probe);
		services.AddSingleton(new ExecutionState());
		services.AddScoped<PropagationScopeState>();
		services.AddSingleton<ScopedSchedulerConsumer>();
		if (captureLogs)
			services.AddLogging(builder => builder.AddProvider(new CapturingLoggerProvider(probe)));
		services.AddImmediateJobsFunctionalTestsBehaviors();
		services.AddImmediateJobs();
	});

	private static JobRecord CreateContextRecord(JobTestHarness harness, string context) => new()
	{
		Id = Guid.NewGuid(),
		JobName = "context-round-trip",
		Payload = "{\"message\":\"legacy\"}",
		State = JobState.Pending,
		DueAt = harness.TimeProvider.GetUtcNow(),
		CreatedAt = harness.TimeProvider.GetUtcNow(),
		Context = context,
	};
}

public sealed class ContextProbe
{
	public List<string> Events { get; } = [];
}

public sealed class PropagationScopeState
{
	public string? TenantId { get; set; }
	public string? CorrelationId { get; set; }
}

public sealed record TenantContext(string TenantId);
public sealed record CorrelationContext(string CorrelationId);
public sealed record FailureContext(string Value);
public sealed record EmptyContext(string Value);

public sealed class TenantContextExtractor(PropagationScopeState state, ContextProbe probe)
	: IJobContextExtractor<TenantContext>
{
	public string Key => "tenant";

	public ValueTask<TenantContext?> CaptureAsync(CancellationToken cancellationToken)
	{
		probe.Events.Add("capture:tenant");
		return ValueTask.FromResult(state.TenantId is null ? null : new TenantContext(state.TenantId));
	}

	public ValueTask RestoreAsync(TenantContext context, CancellationToken cancellationToken)
	{
		probe.Events.Add("restore:tenant");
		state.TenantId = context.TenantId;
		return ValueTask.CompletedTask;
	}
}

public sealed class CorrelationContextExtractor(PropagationScopeState state, ContextProbe probe)
	: IJobContextExtractor<CorrelationContext>
{
	public string Key => "correlation";

	public ValueTask<CorrelationContext?> CaptureAsync(CancellationToken cancellationToken)
	{
		probe.Events.Add("capture:correlation");
		return ValueTask.FromResult(state.CorrelationId is null ? null : new CorrelationContext(state.CorrelationId));
	}

	public ValueTask RestoreAsync(CorrelationContext context, CancellationToken cancellationToken)
	{
		probe.Events.Add("restore:correlation");
		state.CorrelationId = context.CorrelationId;
		return ValueTask.CompletedTask;
	}
}

public sealed class ThrowingCaptureExtractor : IJobContextExtractor<FailureContext>
{
	public string Key => "capture-failure";
	public ValueTask<FailureContext?> CaptureAsync(CancellationToken cancellationToken) =>
		throw new InvalidOperationException("Capture failed");
	public ValueTask RestoreAsync(FailureContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class ThrowingRestoreExtractor : IJobContextExtractor<FailureContext>
{
	public string Key => "restore-failure";
	public ValueTask<FailureContext?> CaptureAsync(CancellationToken cancellationToken) =>
		ValueTask.FromResult<FailureContext?>(new("captured"));
	public ValueTask RestoreAsync(FailureContext context, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("Restore failed");
}

public sealed class FirstNullExtractor : IJobContextExtractor<EmptyContext>
{
	public string Key => "duplicate";
	public ValueTask<EmptyContext?> CaptureAsync(CancellationToken cancellationToken) => ValueTask.FromResult<EmptyContext?>(null);
	public ValueTask RestoreAsync(EmptyContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class SecondNullExtractor : IJobContextExtractor<EmptyContext>
{
	public string Key => "duplicate";
	public ValueTask<EmptyContext?> CaptureAsync(CancellationToken cancellationToken) => ValueTask.FromResult<EmptyContext?>(null);
	public ValueTask RestoreAsync(EmptyContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class ContextHandlerBehavior<TRequest, TResponse>(PropagationScopeState state, ContextProbe probe)
	: Behavior<TRequest, TResponse>
	where TRequest : IJobRequest
{
	public override ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken)
	{
		_ = request.JobDetails ?? throw new InvalidOperationException("Job details were not populated.");
		probe.Events.Add($"handler-behavior:{state.TenantId}/{state.CorrelationId}");
		return Next(request, cancellationToken);
	}
}

[Behaviors(typeof(ContextHandlerBehavior<,>))]
[AttributeUsage(AttributeTargets.Class)]
public sealed class ContextHandlerPipelineAttribute : Attribute;

[Handler, ContextHandlerPipeline]
[Job("context-round-trip")]
[UsesJobContext<TenantContextExtractor>, UsesJobContext<CorrelationContextExtractor>]
public sealed partial class ContextRoundTripJob(PropagationScopeState state, ContextProbe probe)
{
	public sealed record Payload(string Message) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		probe.Events.Add($"handler:{state.TenantId}/{state.CorrelationId}:{payload.Message}");
		return ValueTask.CompletedTask;
	}
}

[Handler, Job("capture-failure"), UsesJobContext<ThrowingCaptureExtractor>]
public sealed partial class CaptureFailureJob
{
	private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

[Handler, Job("restore-failure", MaxAttempts = 2, Backoff = BackoffStrategy.Fixed, BackoffBase = "00:00:01")]
[UsesJobContext<ThrowingRestoreExtractor>]
public sealed partial class RestoreFailureJob
{
	private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

[Handler, Job("duplicate-context-key")]
[UsesJobContext<FirstNullExtractor>, UsesJobContext<SecondNullExtractor>]
public sealed partial class DuplicateContextKeyJob
{
	private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

[Handler, Job("context-cron", Cron = "* * * * * *")]
[UsesJobContext<TenantContextExtractor>]
public sealed partial class ContextCronJob(PropagationScopeState state, ContextProbe probe)
{
	private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken)
	{
		_ = payload.JobDetails ?? throw new InvalidOperationException("Job details were not populated.");
		probe.Events.Add(state.TenantId is null ? "cron:no-context" : "cron:" + state.TenantId);
		return ValueTask.CompletedTask;
	}
}

public sealed class ScopedSchedulerConsumer(IServiceScopeFactory scopeFactory)
{
	public async ValueTask<Guid> EnqueueAsync(CancellationToken cancellationToken)
	{
		await using var scope = scopeFactory.CreateAsyncScope();
		var state = scope.ServiceProvider.GetRequiredService<PropagationScopeState>();
		state.TenantId = "singleton-tenant";
		state.CorrelationId = "singleton-correlation";
		var scheduler = scope.ServiceProvider.GetRequiredService<ContextRoundTripJob.Scheduler>();
		return await scheduler.Enqueue(new("singleton"), cancellationToken);
	}
}

internal sealed class CapturingLoggerProvider(ContextProbe probe) : ILoggerProvider
{
	public ILogger CreateLogger(string categoryName) => new CapturingLogger(probe);
	public void Dispose() { }

	private sealed class CapturingLogger(ContextProbe probe) : ILogger
	{
		public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter
		)
		{
			if (IsEnabled(logLevel))
				probe.Events.Add(formatter(state, exception));
		}
	}

	private sealed class NoopScope : IDisposable
	{
		public static NoopScope Instance { get; } = new();
		public void Dispose() { }
	}
}
#pragma warning restore CS1591, CA1822
