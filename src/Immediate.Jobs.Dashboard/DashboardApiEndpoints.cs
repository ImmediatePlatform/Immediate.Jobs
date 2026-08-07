using System.Globalization;
using System.Text.Json;
using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

[assembly: Behaviors(typeof(ValidationBehavior<,>))]

namespace Immediate.Jobs.Dashboard;

[RouteGroup("api")]
internal sealed partial class DashboardApi;

[Handler]
[MapGet("overview")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardOverview
{
	internal sealed record Query;

	internal static JsonHttpResult<JobMonitoringSnapshot> TransformResult(JobMonitoringSnapshot result) =>
		TypedResults.Json(result, DashboardJsonSerializerContext.Default.JobMonitoringSnapshot);

	private static async ValueTask<JobMonitoringSnapshot> HandleAsync(
		Query _,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		snapshot = snapshot with { Capabilities = storage.GetCapabilities() };
		return snapshot;
	}
}

[Handler]
[MapGet("jobs")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJobs
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		public JobState? State { get; init; }
		public string? Queue { get; init; }
		public string? Search { get; init; }

		[GreaterThanOrEqual(0)]
		public int? Skip { get; init; }

		[GreaterThan(0)]
		public int? Take { get; init; }
	}

	internal static JsonHttpResult<DashboardJobPage> TransformResult(DashboardJobPage result) =>
		TypedResults.Json(result, DashboardJsonSerializerContext.Default.DashboardJobPage);

	private static async ValueTask<DashboardJobPage> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var pageSize = Math.Min(query.Take ?? 50, 200);
		var pageStart = query.Skip ?? 0;
		var jobs = await storage.QueryJobsAsync(new JobQuery
		{
			State = query.State,
			QueueName = query.Queue,
			Search = query.Search,
			Skip = pageStart,
			Take = pageSize + 1,
		}, cancellationToken);
		return new(
			[.. jobs.Take(pageSize)],
			pageStart,
			pageSize,
			jobs.Count > pageSize
		);
	}
}

[Handler]
[MapGet("jobs/{jobId}")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJob
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string JobId { get; init; }
	}

	internal static Results<JsonHttpResult<JobRecord>, NotFound> TransformResult(JobRecord? result) =>
		result is null
			? TypedResults.NotFound()
			: TypedResults.Json(result, DashboardJsonSerializerContext.Default.JobRecord);

	private static async ValueTask<JobRecord?> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var jobs = await storage.QueryJobsAsync(
			new() { Id = query.JobId, Take = 1 },
			cancellationToken
		);
		return jobs.SingleOrDefault();
	}
}

[Handler]
[MapGet("jobs/{jobId}/executions")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJobExecutions
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string JobId { get; init; }

		[GreaterThanOrEqual(0)]
		public int? Skip { get; init; }

		[GreaterThan(0)]
		public int? Take { get; init; }
	}

	internal static Results<JsonHttpResult<DashboardJobExecutionPage>, NotFound> TransformResult(
		DashboardJobExecutionPage? result
	) => result is null
		? TypedResults.NotFound()
		: TypedResults.Json(result, DashboardJsonSerializerContext.Default.DashboardJobExecutionPage);

	private static async ValueTask<DashboardJobExecutionPage?> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var pageStart = query.Skip ?? 0;
		var pageSize = Math.Min(query.Take ?? 50, 200);
		var jobs = await storage.QueryJobsAsync(
			new() { Id = query.JobId, Take = 1 },
			cancellationToken
		);
		if (jobs.Count == 0)
			return null;

		var executions = await storage.QueryJobExecutionsAsync(new()
		{
			JobId = query.JobId,
			Skip = pageStart,
			Take = pageSize + 1,
		}, cancellationToken);
		return new(
			[.. executions.Take(pageSize)],
			pageStart,
			pageSize,
			executions.Count > pageSize
		);
	}
}

[Handler]
[MapGet("jobs/{jobId}/telemetry-links")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJobTelemetryLinks
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string JobId { get; init; }
	}

	internal static Results<JsonHttpResult<JobTelemetryLink[]>, NotFound> TransformResult(
		JobTelemetryLink[]? result
	) => DashboardApiEndpointOperations.TransformTelemetryLinksResult(result);

	private static ValueTask<JobTelemetryLink[]?> HandleAsync(
		Query query,
		IJobStorage storage,
		ImmediateJobsDashboardOptions options,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.GetJobTelemetryLinksAsync(
		query.JobId,
		executionNumber: null,
		storage,
		options,
		cancellationToken
	);
}

[Handler]
[MapGet("jobs/{jobId}/executions/{executionNumber:int}/telemetry-links")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJobExecutionTelemetryLinks
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string JobId { get; init; }

		[GreaterThan(0)]
		public required int ExecutionNumber { get; init; }
	}

	internal static Results<JsonHttpResult<JobTelemetryLink[]>, NotFound> TransformResult(
		JobTelemetryLink[]? result
	) => DashboardApiEndpointOperations.TransformTelemetryLinksResult(result);

	private static ValueTask<JobTelemetryLink[]?> HandleAsync(
		Query query,
		IJobStorage storage,
		ImmediateJobsDashboardOptions options,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.GetJobTelemetryLinksAsync(
		query.JobId,
		query.ExecutionNumber,
		storage,
		options,
		cancellationToken
	);
}

[Handler]
[MapGet("batches")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardBatches
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		public BatchState? State { get; init; }

		[GreaterThanOrEqual(0)]
		public int? Skip { get; init; }

		[GreaterThan(0)]
		public int? Take { get; init; }
	}

	internal static Results<JsonHttpResult<BatchStatus[]>, NotFound> TransformResult(BatchStatus[]? result) =>
		result is null
			? TypedResults.NotFound()
			: TypedResults.Json(result, DashboardJsonSerializerContext.Default.BatchStatusArray);

	private static async ValueTask<BatchStatus[]?> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		if (storage is not IJobGraphStorage graphStorage)
			return null;

		var batches = await graphStorage.QueryBatchesAsync(new()
		{
			State = query.State,
			Skip = query.Skip ?? 0,
			Take = Math.Min(query.Take ?? 100, 500),
		}, cancellationToken);
		return [.. batches];
	}
}

[Handler]
[MapGet("batches/{batchId}")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardBatch
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string BatchId { get; init; }
	}

	internal static Results<JsonHttpResult<BatchStatus>, NotFound> TransformResult(BatchStatus? result) =>
		result is null
			? TypedResults.NotFound()
			: TypedResults.Json(result, DashboardJsonSerializerContext.Default.BatchStatus);

	private static async ValueTask<BatchStatus?> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		if (storage is not IJobGraphStorage graphStorage)
			return null;

		return await graphStorage.GetBatchStatusAsync(query.BatchId, cancellationToken);
	}
}

[Handler]
[MapGet("batches/{batchId}/members")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardBatchMembers
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string BatchId { get; init; }
		public JobState? State { get; init; }

		[GreaterThanOrEqual(0)]
		public int? Skip { get; init; }

		[GreaterThan(0)]
		public int? Take { get; init; }
	}

	internal static Results<JsonHttpResult<BatchMemberStatus[]>, NotFound> TransformResult(
		BatchMemberStatus[]? result
	) => result is null
		? TypedResults.NotFound()
		: TypedResults.Json(result, DashboardJsonSerializerContext.Default.BatchMemberStatusArray);

	private static async ValueTask<BatchMemberStatus[]?> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		if (storage is not IJobGraphStorage graphStorage)
			return null;

		var members = await graphStorage.QueryBatchMembersAsync(query.BatchId, new()
		{
			State = query.State,
			Skip = query.Skip ?? 0,
			Take = Math.Min(query.Take ?? 100, 500),
		}, cancellationToken);
		return [.. members];
	}
}

[Handler]
[MapGet("batches/{batchId}/graph")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardBatchGraph
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string BatchId { get; init; }
	}

	internal static Results<JsonHttpResult<BatchGraph>, NotFound> TransformResult(BatchGraph? result) =>
		result is null
			? TypedResults.NotFound()
			: TypedResults.Json(result, DashboardJsonSerializerContext.Default.BatchGraph);

	private static async ValueTask<BatchGraph?> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		if (storage is not IJobGraphStorage graphStorage)
			return null;

		return await graphStorage.GetBatchGraphAsync(query.BatchId, cancellationToken);
	}
}

[Handler]
[MapGet("batches/{batchId}/stream")]
[MapGroup<DashboardApi>]
internal static partial class StreamDashboardBatch
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string BatchId { get; init; }
	}

	internal static EmptyHttpResult TransformResult() => TypedResults.Empty;

	private static async ValueTask HandleAsync(
		Query query,
		IHttpContextAccessor httpContextAccessor,
		IJobStorage storage,
		ImmediateJobsDashboardOptions options,
		CancellationToken cancellationToken
	)
	{
		await DashboardApiEndpointOperations.StreamBatchEventsAsync(
			httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active dashboard HTTP request was found."),
			query.BatchId,
			storage,
			options.UpdateInterval,
			cancellationToken
		);
	}
}

[Handler]
[MapPost("batches/{batchId}/cancel")]
[MapGroup<DashboardApi>]
internal static partial class CancelDashboardBatch
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string BatchId { get; init; }
	}

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformMutationResult(result);

	private static ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => storage is IJobGraphStorage graphStorage
		? DashboardApiEndpointOperations.MutateBatchAsync(
			() => graphStorage.CancelBatchAsync(command.BatchId, cancellationToken)
		)
		: ValueTask.FromResult(DashboardMutationResult.NotFound);
}

[Handler]
[MapDelete("batches/{batchId}")]
[MapGroup<DashboardApi>]
internal static partial class DeleteDashboardBatch
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[NotEmpty]
		public required string BatchId { get; init; }
	}

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformMutationResult(result);

	private static ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => storage is IJobGraphStorage graphStorage
		? DashboardApiEndpointOperations.MutateBatchAsync(
			() => graphStorage.DeleteBatchAsync(command.BatchId, cancellationToken)
		)
		: ValueTask.FromResult(DashboardMutationResult.NotFound);
}

[Handler]
[MapGet("recurring")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardRecurringJobs
{
	internal sealed record Query;

	internal static JsonHttpResult<RecurringJobSchedule[]> TransformResult(RecurringJobSchedule[] result) =>
		TypedResults.Json(result, DashboardJsonSerializerContext.Default.RecurringJobScheduleArray);

	private static async ValueTask<RecurringJobSchedule[]> HandleAsync(
		Query _,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		return [.. snapshot.Recurring];
	}
}

[Handler]
[MapGet("servers")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardServers
{
	internal sealed record Query;

	internal static JsonHttpResult<JobServerSnapshot[]> TransformResult(JobServerSnapshot[] result) =>
		TypedResults.Json(result, DashboardJsonSerializerContext.Default.JobServerSnapshotArray);

	private static async ValueTask<JobServerSnapshot[]> HandleAsync(
		Query _,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		return [.. snapshot.Servers];
	}
}

[Handler]
[MapPost("jobs/{jobId}/retry")]
[MapGroup<DashboardApi>]
internal static partial class RetryDashboardJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string JobId { get; init; }
	}

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformMutationResult(result);

	private static ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.MutateJobAsync(
		() => storage.RetryAsync(command.JobId, cancellationToken)
	);
}

[Handler]
[MapPost("jobs/{jobId}/cancel")]
[MapGroup<DashboardApi>]
internal static partial class CancelDashboardJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string JobId { get; init; }
	}

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformMutationResult(result);

	private static ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.MutateJobAsync(
		() => storage.CancelAsync(command.JobId, cancellationToken)
	);
}

[Handler]
[MapPost("recurring/{name}/trigger")]
[MapGroup<DashboardApi>]
internal static partial class TriggerDashboardRecurringJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string Name { get; init; }
	}

	internal static Results<StatusCodeHttpResult, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformTriggerResult(result);

	private static async ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		IServiceProvider serviceProvider,
		IJobStorage storage,
		IEnumerable<JobDefinition> definitions,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		var schedule = snapshot.Recurring.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, command.Name, StringComparison.Ordinal));
		if (schedule is null)
			return DashboardMutationResult.NotFound;

		var definition = definitions.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, schedule.JobName, StringComparison.Ordinal));
		if (definition is null)
		{
			return DashboardMutationResult.Conflict(
				$"No generated job definition exists for '{schedule.JobName}'."
			);
		}

		var idGenerator = serviceProvider.GetRequiredService<IIdGenerator>();
		var now = serviceProvider.GetService<TimeProvider>()?.GetUtcNow() ?? TimeProvider.System.GetUtcNow();
		var job = new JobRecord
		{
			JobId = idGenerator.CreateId(IdKind.Job),
			JobName = schedule.JobName,
			QueueName = definition.Queue.Name,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
		};
		await storage.EnqueueAsync(job, cancellationToken);
		return DashboardMutationResult.Accepted;
	}
}

[Handler]
[MapPost("recurring/{name}/pause")]
[MapGroup<DashboardApi>]
internal static partial class PauseDashboardRecurringJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string Name { get; init; }
	}

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformMutationResult(result);

	private static ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => storage is IRecurringJobStorage recurringStorage
		? DashboardApiEndpointOperations.MutateRecurringAsync(
			() => recurringStorage.PauseRecurringAsync(command.Name, cancellationToken)
		)
		: ValueTask.FromResult(DashboardMutationResult.NotFound);
}

[Handler]
[MapPost("recurring/{name}/resume")]
[MapGroup<DashboardApi>]
internal static partial class ResumeDashboardRecurringJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string Name { get; init; }
	}

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformMutationResult(result);

	private static ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => storage is IRecurringJobStorage recurringStorage
		? DashboardApiEndpointOperations.MutateRecurringAsync(
			() => recurringStorage.ResumeRecurringAsync(command.Name, cancellationToken)
		)
		: ValueTask.FromResult(DashboardMutationResult.NotFound);
}

[Handler]
[MapGet("events")]
[MapGroup<DashboardApi>]
internal static partial class StreamDashboardEvents
{
	internal sealed record Query;

	internal static EmptyHttpResult TransformResult() => TypedResults.Empty;

	private static async ValueTask HandleAsync(
		Query _,
		IHttpContextAccessor httpContextAccessor,
		IJobStorage storage,
		ImmediateJobsDashboardOptions options,
		CancellationToken cancellationToken
	)
	{
		await DashboardApiEndpointOperations.StreamEventsAsync(
			httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active dashboard HTTP request was found."),
			storage,
			options.UpdateInterval,
			cancellationToken
		);
	}
}

internal enum DashboardMutationStatus
{
	NoContent,
	Accepted,
	NotFound,
	Conflict,
}

internal sealed record DashboardMutationResult(DashboardMutationStatus Status, string? Detail = null)
{
	internal static DashboardMutationResult NoContent { get; } = new(DashboardMutationStatus.NoContent);
	internal static DashboardMutationResult Accepted { get; } = new(DashboardMutationStatus.Accepted);
	internal static DashboardMutationResult NotFound { get; } = new(DashboardMutationStatus.NotFound);

	internal static DashboardMutationResult Conflict(string detail) =>
		new(DashboardMutationStatus.Conflict, detail);
}

internal static class DashboardApiEndpointOperations
{
	internal static Results<JsonHttpResult<JobTelemetryLink[]>, NotFound> TransformTelemetryLinksResult(
		JobTelemetryLink[]? result
	) => result is null
		? TypedResults.NotFound()
		: TypedResults.Json(result, DashboardJsonSerializerContext.Default.JobTelemetryLinkArray);

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformMutationResult(
		DashboardMutationResult result
	) => result.Status switch
	{
		DashboardMutationStatus.NoContent => TypedResults.NoContent(),
		DashboardMutationStatus.NotFound => TypedResults.NotFound(),
		DashboardMutationStatus.Conflict => TypedResults.Problem(
			detail: result.Detail,
			statusCode: StatusCodes.Status409Conflict
		),
		DashboardMutationStatus.Accepted => throw new InvalidOperationException(
			"An accepted result cannot be transformed as a standard mutation."
		),
		_ => throw new InvalidOperationException($"Unsupported mutation status '{result.Status}'."),
	};

	internal static Results<StatusCodeHttpResult, NotFound, ProblemHttpResult> TransformTriggerResult(
		DashboardMutationResult result
	) => result.Status switch
	{
		DashboardMutationStatus.Accepted => TypedResults.StatusCode(StatusCodes.Status202Accepted),
		DashboardMutationStatus.NotFound => TypedResults.NotFound(),
		DashboardMutationStatus.Conflict => TypedResults.Problem(
			detail: result.Detail,
			statusCode: StatusCodes.Status409Conflict
		),
		DashboardMutationStatus.NoContent => throw new InvalidOperationException(
			"A no-content result cannot be transformed as a recurring-job trigger."
		),
		_ => throw new InvalidOperationException($"Unsupported trigger status '{result.Status}'."),
	};

	internal static async ValueTask<JobTelemetryLink[]?> GetJobTelemetryLinksAsync(
		string jobId,
		int? executionNumber,
		IJobStorage storage,
		ImmediateJobsDashboardOptions options,
		CancellationToken cancellationToken
	)
	{
		var jobs = await storage.QueryJobsAsync(
			new() { Id = jobId, Take = 1 },
			cancellationToken
		);
		var job = jobs.SingleOrDefault();
		if (job is null)
			return null;

		JobExecutionRecord? execution = null;
		if (executionNumber is { } attempt)
		{
			var executions = await storage.QueryJobExecutionsAsync(
				new() { JobId = jobId, Attempt = attempt, Take = 1 },
				cancellationToken
			);
			execution = executions.SingleOrDefault();
			if (execution is null)
				return null;
		}

		if (options.TelemetryLinks.Count == 0)
			return [];

		var contextJob = execution is null
			? job
			: job with
			{
				Attempt = execution.Attempt,
				ExecutionTraceId = execution.ExecutionTraceId,
				ExecutionSpanId = execution.ExecutionSpanId,
				ExecutionStartedAt = execution.ExecutionStartedAt,
			};
		var context = new JobTelemetryLinkContext(contextJob) { Execution = execution };
		var links = new List<JobTelemetryLink>(options.TelemetryLinks.Count);
		foreach (var registration in options.TelemetryLinks)
		{
			var url = registration.CreateUrl(context);
			if (url is null)
				continue;
			if (url.IsAbsoluteUri &&
				!string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
				!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
			{
				throw new ImmediateJobException(
					$"Telemetry link '{registration.Label}' must use HTTP or HTTPS."
				);
			}

			links.Add(new(registration.Label, registration.Kind, url));
		}

		return [.. links];
	}

	internal static ValueTask<DashboardMutationResult> MutateJobAsync(Func<ValueTask> operation) => MutateAsync(
		operation,
		includeConflict: true
	);

	internal static ValueTask<DashboardMutationResult> MutateBatchAsync(Func<ValueTask> operation) => MutateAsync(
		operation,
		includeConflict: true
	);

	internal static ValueTask<DashboardMutationResult> MutateRecurringAsync(Func<ValueTask> operation) => MutateAsync(
		operation,
		includeConflict: false
	);

	private static async ValueTask<DashboardMutationResult> MutateAsync(
		Func<ValueTask> operation,
		bool includeConflict
	)
	{
		try
		{
			await operation();
			return DashboardMutationResult.NoContent;
		}
		catch (KeyNotFoundException)
		{
			return DashboardMutationResult.NotFound;
		}
		catch (ImmediateJobException exception) when (includeConflict)
		{
			return DashboardMutationResult.Conflict(exception.Message);
		}
	}

	internal static async Task StreamEventsAsync(
		HttpContext context,
		IJobStorage storage,
		TimeSpan interval,
		CancellationToken cancellationToken
	)
	{
		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = "text/event-stream";
		context.Response.Headers.CacheControl = "no-cache, no-store";
		context.Response.Headers.Append("X-Accel-Buffering", "no");

		try
		{
			await context.Response.WriteAsync("retry: 3000\n\n", cancellationToken);
			await context.Response.Body.FlushAsync(cancellationToken);

			while (!cancellationToken.IsCancellationRequested)
			{
				var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
				snapshot = snapshot with { Capabilities = storage.GetCapabilities() };
				var jobs = await storage.QueryJobsAsync(new() { Take = 100 }, cancellationToken);
				var batches = storage is IJobGraphStorage graphStorage
					? await graphStorage.QueryBatchesAsync(new() { Take = 100 }, cancellationToken)
					: [];
				var state = new DashboardState(snapshot, [.. jobs], [.. batches]);
				var json = JsonSerializer.Serialize(state, DashboardJsonSerializerContext.Default.DashboardState);
				await context.Response.WriteAsync(
					"id: " + snapshot.CapturedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "\n",
					cancellationToken
				);
				await context.Response.WriteAsync("event: state\ndata: " + json + "\n\n", cancellationToken);
				await context.Response.Body.FlushAsync(cancellationToken);
				await Task.Delay(interval, cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	internal static async Task StreamBatchEventsAsync(
		HttpContext context,
		string batchId,
		IJobStorage storage,
		TimeSpan interval,
		CancellationToken cancellationToken
	)
	{
		if (storage is not IJobGraphStorage graphStorage)
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		var status = await graphStorage.GetBatchStatusAsync(batchId, cancellationToken);
		if (status is null)
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = "text/event-stream";
		context.Response.Headers.CacheControl = "no-cache, no-store";
		context.Response.Headers.Append("X-Accel-Buffering", "no");
		string? previousState = null;

		try
		{
			await context.Response.WriteAsync("retry: 3000\n\n", cancellationToken);
			await context.Response.Body.FlushAsync(cancellationToken);

			while (!cancellationToken.IsCancellationRequested)
			{
				status = await graphStorage.GetBatchStatusAsync(batchId, cancellationToken);
				var graph = await graphStorage.GetBatchGraphAsync(batchId, cancellationToken);
				if (status is null || graph is null)
					break;

				var statusJson = JsonSerializer.Serialize(status, DashboardJsonSerializerContext.Default.BatchStatus);
				var graphJson = JsonSerializer.Serialize(graph, DashboardJsonSerializerContext.Default.BatchGraph);
				var currentState = statusJson + graphJson;
				if (!string.Equals(previousState, currentState, StringComparison.Ordinal))
				{
					var eventId = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()
						.ToString(CultureInfo.InvariantCulture);
					await context.Response.WriteAsync("id: " + eventId + "\n", cancellationToken);
					await context.Response.WriteAsync("event: status\ndata: " + statusJson + "\n\n", cancellationToken);
					await context.Response.WriteAsync("event: graph\ndata: " + graphJson + "\n\n", cancellationToken);
					await context.Response.Body.FlushAsync(cancellationToken);
					previousState = currentState;
				}

				await Task.Delay(interval, cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}
}
