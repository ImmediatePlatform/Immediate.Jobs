using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.FunctionalTests;

public sealed class ContextPropagationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ContextRoundTripsIntoJobAndHandlerBehaviors(bool reverseExtractorDeclarations)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var probe = new ContextProbe();
		await using var harness = CreateHarness(probe);

		JobHandle id;
		await using (var scope = harness.Services.CreateAsyncScope())
		{
			var ambient = scope.ServiceProvider.GetRequiredService<PropagationScopeState>();
			ambient.TenantId = "tenant-42";
			ambient.CorrelationId = "correlation-7";
			if (reverseExtractorDeclarations)
			{
				var scheduler = scope.ServiceProvider.GetRequiredService<ReversedContextRoundTripJob.Scheduler>();
				id = await scheduler.EnqueueAsync(new("hello"), cancellationToken);
			}
			else
			{
				var scheduler = scope.ServiceProvider.GetRequiredService<ContextRoundTripJob.Scheduler>();
				id = await scheduler.EnqueueAsync(new("hello"), cancellationToken);
			}
		}

		var enqueued = await harness.GetJobAsync(id, cancellationToken);
		Assert.Contains("tenant-42", enqueued.Context, StringComparison.Ordinal);
		Assert.Contains("correlation-7", enqueued.Context, StringComparison.Ordinal);

		await harness.DrainAsync(cancellationToken);

		string[] expectedEvents =
			[
				"capture:tenant",
				"capture:correlation",
				"restore:tenant",
				"restore:correlation",
				"handler-behavior:tenant-42/correlation-7",
				"handler:tenant-42/correlation-7:hello",
			];
		Assert.Equal(
			expectedEvents.Order(StringComparer.Ordinal),
			probe.Events.Order(StringComparer.Ordinal)
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
			() => scheduler.EnqueueAsync(default, cancellationToken).AsTask()
		);

		Assert.Equal("Capture failed", exception.Message);
		Assert.Empty(await harness.QueryJobsAsync(cancellationToken: cancellationToken));
	}

	[Fact]
	public async Task RestoreFailureRetriesAndEventuallyDeadLetters()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(new());
		JobHandle id;
		await using (var scope = harness.Services.CreateAsyncScope())
		{
			var scheduler = scope.ServiceProvider.GetRequiredService<RestoreFailureJob.Scheduler>();
			id = await scheduler.EnqueueAsync(default, cancellationToken);
		}

		await harness.DrainAsync(cancellationToken);
		Assert.Equal(JobState.Scheduled, (await harness.GetJobAsync(id, cancellationToken)).State);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromSeconds(1), cancellationToken);
		var failed = await harness.GetJobAsync(id, cancellationToken);
		Assert.Equal(JobState.Failed, failed.State);
		Assert.Equal(2, failed.Attempt);
		Assert.Contains("Restore failed", failed.LastError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DuplicateKeysThrowEvenWhenExtractorsCaptureNull()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(new());
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<DuplicateContextKeyJob.Scheduler>();

		var exception = await Assert.ThrowsAsync<ImmediateJobException>(
			() => scheduler.EnqueueAsync(default, cancellationToken).AsTask()
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

		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(record.JobId, cancellationToken)).State);
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
			JobId = JobHandle.FromString(Guid.NewGuid().ToString("N")),
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

		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(record.JobId, cancellationToken)).State);
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
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(record.JobId, cancellationToken)).State);
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
			candidate => string.Equals(candidate.JobName, "context-cron"
, StringComparison.Ordinal));
		Assert.Null(job.Context);
		Assert.Equal(JobState.Succeeded, job.State);
		Assert.Contains("cron:no-context", probe.Events);
	}

	[Fact]
	public async Task DynamicRecurringSchedulerValidatesAndPersistsGeneratedJobSchedule()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(new());
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<CaptureFailureJob.Scheduler>();

		await scheduler.AddOrUpdateRecurringAsync(
			"dynamic-capture",
			"0 * * * *",
			"UTC",
			cancellationToken
		);

		var schedule = Assert.Single(
			(await harness.Storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring,
			static candidate => string.Equals(candidate.Name, "dynamic-capture", StringComparison.Ordinal)
		);
		Assert.Equal("capture-failure", schedule.JobName);
		Assert.Equal("0 * * * *", schedule.Cron);
		Assert.Equal("UTC", schedule.TimeZone);
		Assert.False(schedule.IsCodeDefined);
		Assert.True(schedule.NextRunAt > harness.TimeProvider.GetUtcNow());

		await scheduler.RemoveRecurringAsync("dynamic-capture", cancellationToken);
		Assert.DoesNotContain(
			(await harness.Storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring,
			static candidate => string.Equals(candidate.Name, "dynamic-capture", StringComparison.Ordinal)
		);
	}

	[Fact]
	public async Task CustomIdGeneratorCreatesJobBatchAndRecurringIds()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var probe = new ContextProbe();
		await using var harness = new JobTestHarness(services =>
		{
			_ = services.AddSingleton(probe);
			_ = services.AddSingleton(new ExecutionState());
			_ = services.AddScoped<PropagationScopeState>();
			_ = services.AddSingleton<IIdGenerator, ReplacedIdGenerator>();
			_ = services.AddImmediateJobsFunctionalTestsHandlers();
			_ = services.AddImmediateJobsFunctionalTestsJobs().UseIdGenerator<TestIdGenerator>();
		});

		_ = Assert.IsType<TestIdGenerator>(harness.Services.GetRequiredService<IIdGenerator>());
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<ContextRoundTripJob.Scheduler>();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var job = await scheduler.EnqueueAsync(new("custom-id"), cancellationToken);
		await using var batch = batches.Begin();
		var batchJob = scheduler.Enqueue(new("batch-id"), batch);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		await harness.DrainAsync(cancellationToken);
		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromSeconds(1), cancellationToken);
		var recurring = Assert.Single(
			await harness.QueryJobsAsync(cancellationToken: cancellationToken),
			candidate => string.Equals(candidate.JobName, "context-cron", StringComparison.Ordinal));

		Assert.StartsWith("job_", job.JobId, StringComparison.Ordinal);
		Assert.StartsWith("job_", batchJob.JobId.JobId, StringComparison.Ordinal);
		Assert.StartsWith("batch_", batchHandle.BatchId, StringComparison.Ordinal);
		Assert.StartsWith("job_", recurring.JobId.JobId, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CodeScheduleInitializationRemovesObsoleteDefinitionsAndPreservesDynamicSchedules()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(new());
		var recurringStorage = Assert.IsType<IRecurringJobStorage>(harness.Storage, exactMatch: false);
		var nextRunAt = harness.TimeProvider.GetUtcNow() + TimeSpan.FromHours(1);
		await recurringStorage.UpsertRecurringAsync(
			CreateRecurringSchedule("removed-job", "removed-job", isCodeDefined: true, nextRunAt),
			cancellationToken
		);
		await recurringStorage.UpsertRecurringAsync(
			CreateRecurringSchedule("context-round-trip", "context-round-trip", isCodeDefined: true, nextRunAt),
			cancellationToken
		);
		await recurringStorage.UpsertRecurringAsync(
			CreateRecurringSchedule("dynamic-context", "context-round-trip", isCodeDefined: false, nextRunAt),
			cancellationToken
		);

		await harness.DrainAsync(cancellationToken);

		var names = (await harness.Storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring
			.Select(static schedule => schedule.Name)
			.Order(StringComparer.Ordinal);
		Assert.Equal(["context-cron", "dynamic-context"], names);
	}

	[Fact]
	public async Task GeneratedSchedulerIsScopedAndSingletonConsumerCanOpenScope()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(new());

		_ = Assert.Throws<InvalidOperationException>(
			harness.Services.GetRequiredService<ContextRoundTripJob.Scheduler>
		);

		var consumer = harness.Services.GetRequiredService<ScopedSchedulerConsumer>();
		var id = await consumer.EnqueueAsync(cancellationToken);
		Assert.Equal(JobState.Pending, (await harness.GetJobAsync(id, cancellationToken)).State);
	}

	private static JobTestHarness CreateHarness(ContextProbe probe, bool captureLogs = false) => new(services =>
	{
		_ = services.AddSingleton(probe);
		_ = services.AddSingleton(new ExecutionState());
		_ = services.AddScoped<PropagationScopeState>();
		_ = services.AddSingleton<ScopedSchedulerConsumer>();
		if (captureLogs)
		{
			_ = services.AddLogging(builder =>
				_ = builder.AddProvider(new CapturingLoggerProvider(probe))
			);
		}

		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
	});

	private static JobRecord CreateContextRecord(JobTestHarness harness, [StringSyntax("json")] string context) =>
		new()
		{
			JobId = JobHandle.FromString(Guid.NewGuid().ToString("N")),
			JobName = "context-round-trip",
			Payload = "{\"message\":\"legacy\"}",
			State = JobState.Pending,
			DueAt = harness.TimeProvider.GetUtcNow(),
			CreatedAt = harness.TimeProvider.GetUtcNow(),
			Context = context,
		};

	private static RecurringJobSchedule CreateRecurringSchedule(
		string name,
		string jobName,
		bool isCodeDefined,
		DateTimeOffset nextRunAt
	) => new()
	{
		Name = name,
		JobName = jobName,
		QueueName = "default",
		Cron = "0 * * * *",
		TimeZone = "UTC",
		IsCodeDefined = isCodeDefined,
		NextRunAt = nextRunAt,
	};
}

public sealed class ContextProbe
{
	public IList<string> Events { get; } = [];
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
	: JobContextExtractor<TenantContext>
{
	public override string Key => "tenant";

	public override TenantContext? Capture()
	{
		probe.Events.Add("capture:tenant");
		return state.TenantId is null ? null : new TenantContext(state.TenantId);
	}

	public override void Restore(TenantContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		probe.Events.Add("restore:tenant");
		state.TenantId = context.TenantId;
	}
}

public sealed class CorrelationContextExtractor(PropagationScopeState state, ContextProbe probe)
	: JobContextExtractor<CorrelationContext>
{
	public override string Key => "correlation";

	public override CorrelationContext? Capture()
	{
		probe.Events.Add("capture:correlation");
		return state.CorrelationId is null ? null : new CorrelationContext(state.CorrelationId);
	}

	public override void Restore(CorrelationContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		probe.Events.Add("restore:correlation");
		state.CorrelationId = context.CorrelationId;
	}
}

public sealed class ThrowingCaptureExtractor : JobContextExtractor<FailureContext>
{
	public override string Key => "capture-failure";
	public override FailureContext? Capture() =>
		throw new InvalidOperationException("Capture failed");
	public override void Restore(FailureContext context) { }
}

public sealed class ThrowingRestoreExtractor : JobContextExtractor<FailureContext>
{
	public override string Key => "restore-failure";
	public override FailureContext? Capture() => new("captured");
	public override void Restore(FailureContext context) =>
		throw new InvalidOperationException("Restore failed");
}

public sealed class FirstNullExtractor : JobContextExtractor<EmptyContext>
{
	public override string Key => "duplicate";
	public override EmptyContext? Capture() => null;
	public override void Restore(EmptyContext context) { }
}

public sealed class SecondNullExtractor : JobContextExtractor<EmptyContext>
{
	public override string Key => "duplicate";
	public override EmptyContext? Capture() => null;
	public override void Restore(EmptyContext context) { }
}

public sealed class ReplacedIdGenerator : IIdGenerator
{
	public string CreateId(IdKind kind) => "replaced";
}

public sealed class TestIdGenerator(TimeProvider timeProvider) : IIdGenerator
{
	private int _sequence;

	public string CreateId(IdKind kind)
	{
		var prefix = kind switch
		{
			IdKind.Job => "job",
			IdKind.Batch => "batch",
			_ => throw new ArgumentOutOfRangeException(nameof(kind)),
		};
		return string.Create(CultureInfo.InvariantCulture, $"{prefix}_{timeProvider.GetUtcNow().UtcTicks}_{Interlocked.Increment(ref _sequence)}");
	}
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
[Job(Name = "context-round-trip")]
[UsesJobContext<TenantContextExtractor>, UsesJobContext<CorrelationContextExtractor>]
public sealed partial class ContextRoundTripJob(PropagationScopeState state, ContextProbe probe)
{
	public sealed record Payload(string Message) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		probe.Events.Add($"handler:{state.TenantId}/{state.CorrelationId}:{payload.Message}");
		return ValueTask.CompletedTask;
	}
}

[Handler, ContextHandlerPipeline]
[Job(Name = "reversed-context-round-trip")]
[UsesJobContext<CorrelationContextExtractor>, UsesJobContext<TenantContextExtractor>]
public sealed partial class ReversedContextRoundTripJob(PropagationScopeState state, ContextProbe probe)
{
	public sealed record Payload(string Message) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		probe.Events.Add($"handler:{state.TenantId}/{state.CorrelationId}:{payload.Message}");
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "capture-failure"), UsesJobContext<ThrowingCaptureExtractor>]
public sealed partial class CaptureFailureJob
{
	private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken)
	{
		_ = payload;
		_ = cancellationToken;
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "restore-failure", MaxAttempts = 2, Backoff = BackoffStrategy.Fixed, BackoffBase = "00:00:01")]
[UsesJobContext<ThrowingRestoreExtractor>]
public sealed partial class RestoreFailureJob
{
	private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken)
	{
		_ = payload;
		_ = cancellationToken;
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "duplicate-context-key")]
[UsesJobContext<FirstNullExtractor>, UsesJobContext<SecondNullExtractor>]
public sealed partial class DuplicateContextKeyJob
{
	private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken)
	{
		_ = payload;
		_ = cancellationToken;
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "context-cron", Cron = "* * * * * *")]
[UsesJobContext<TenantContextExtractor>]
public sealed partial class ContextCronJob(PropagationScopeState state, ContextProbe probe)
{
	private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		_ = payload.JobDetails ?? throw new InvalidOperationException("Job details were not populated.");
		probe.Events.Add(state.TenantId is null ? "cron:no-context" : "cron:" + state.TenantId);
		return ValueTask.CompletedTask;
	}
}

public sealed class ScopedSchedulerConsumer(IServiceScopeFactory scopeFactory)
{
	public async ValueTask<string> EnqueueAsync(CancellationToken cancellationToken)
	{
		await using var scope = scopeFactory.CreateAsyncScope();
		var state = scope.ServiceProvider.GetRequiredService<PropagationScopeState>();
		state.TenantId = "singleton-tenant";
		state.CorrelationId = "singleton-correlation";
		var scheduler = scope.ServiceProvider.GetRequiredService<ContextRoundTripJob.Scheduler>();
		return (await scheduler.EnqueueAsync(new("singleton"), cancellationToken)).JobId;
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
