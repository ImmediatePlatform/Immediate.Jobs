using System.Globalization;
using System.Text.Json;
using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;
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

	private static async ValueTask<IResult> HandleAsync(
		Query _,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
		snapshot = snapshot with { Capabilities = storage.GetCapabilities() };
		return Results.Json(snapshot, DashboardJsonSerializerContext.Default.JobMonitoringSnapshot);
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

	private static async ValueTask<IResult> HandleAsync(
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
		}, cancellationToken).ConfigureAwait(false);
		var page = new DashboardJobPage(
			[.. jobs.Take(pageSize)],
			pageStart,
			pageSize,
			jobs.Count > pageSize
		);
		return Results.Json(page, DashboardJsonSerializerContext.Default.DashboardJobPage);
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

	private static async ValueTask<IResult> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var jobs = await storage.QueryJobsAsync(
			new() { Id = query.JobId, Take = 1 },
			cancellationToken
		).ConfigureAwait(false);
		return jobs.SingleOrDefault() is { } job
			? Results.Json(job, DashboardJsonSerializerContext.Default.JobRecord)
			: Results.NotFound();
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

	private static async ValueTask<IResult> HandleAsync(
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
		).ConfigureAwait(false);
		if (jobs.Count == 0)
			return Results.NotFound();

		var executions = await storage.QueryJobExecutionsAsync(new()
		{
			JobId = query.JobId,
			Skip = pageStart,
			Take = pageSize + 1,
		}, cancellationToken).ConfigureAwait(false);
		var page = new DashboardJobExecutionPage(
			[.. executions.Take(pageSize)],
			pageStart,
			pageSize,
			executions.Count > pageSize
		);
		return Results.Json(page, DashboardJsonSerializerContext.Default.DashboardJobExecutionPage);
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

	private static ValueTask<IResult> HandleAsync(
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

	private static ValueTask<IResult> HandleAsync(
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

	private static async ValueTask<IResult> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		if (storage is not IJobGraphStorage graphStorage)
			return Results.NotFound();

		var batches = await graphStorage.QueryBatchesAsync(new()
		{
			State = query.State,
			Skip = query.Skip ?? 0,
			Take = Math.Min(query.Take ?? 100, 500),
		}, cancellationToken).ConfigureAwait(false);
		return Results.Json(
			[.. batches],
			DashboardJsonSerializerContext.Default.BatchStatusArray
		);
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

	private static async ValueTask<IResult> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		if (storage is not IJobGraphStorage graphStorage)
			return Results.NotFound();

		return await graphStorage.GetBatchStatusAsync(query.BatchId, cancellationToken).ConfigureAwait(false) is { } status
			? Results.Json(status, DashboardJsonSerializerContext.Default.BatchStatus)
			: Results.NotFound();
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

	private static async ValueTask<IResult> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		if (storage is not IJobGraphStorage graphStorage)
			return Results.NotFound();

		var members = await graphStorage.QueryBatchMembersAsync(query.BatchId, new()
		{
			State = query.State,
			Skip = query.Skip ?? 0,
			Take = Math.Min(query.Take ?? 100, 500),
		}, cancellationToken).ConfigureAwait(false);
		return Results.Json(
			[.. members],
			DashboardJsonSerializerContext.Default.BatchMemberStatusArray
		);
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

	private static async ValueTask<IResult> HandleAsync(
		Query query,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		if (storage is not IJobGraphStorage graphStorage)
			return Results.NotFound();

		return await graphStorage.GetBatchGraphAsync(query.BatchId, cancellationToken).ConfigureAwait(false) is { } graph
			? Results.Json(graph, DashboardJsonSerializerContext.Default.BatchGraph)
			: Results.NotFound();
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

	private static async ValueTask<IResult> HandleAsync(
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
		).ConfigureAwait(false);
		return Results.Empty;
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

	private static ValueTask<IResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => storage is IJobGraphStorage graphStorage
		? DashboardApiEndpointOperations.MutateBatchAsync(
			() => graphStorage.CancelBatchAsync(command.BatchId, cancellationToken)
		)
		: ValueTask.FromResult<IResult>(Results.NotFound());
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

	private static ValueTask<IResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => storage is IJobGraphStorage graphStorage
		? DashboardApiEndpointOperations.MutateBatchAsync(
			() => graphStorage.DeleteBatchAsync(command.BatchId, cancellationToken)
		)
		: ValueTask.FromResult<IResult>(Results.NotFound());
}

[Handler]
[MapGet("recurring")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardRecurringJobs
{
	internal sealed record Query;

	private static async ValueTask<IResult> HandleAsync(
		Query _,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return Results.Json(
			[.. snapshot.Recurring],
			DashboardJsonSerializerContext.Default.RecurringJobScheduleArray
		);
	}
}

[Handler]
[MapGet("servers")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardServers
{
	internal sealed record Query;

	private static async ValueTask<IResult> HandleAsync(
		Query _,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return Results.Json(
			[.. snapshot.Servers],
			DashboardJsonSerializerContext.Default.JobServerSnapshotArray
		);
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

	private static ValueTask<IResult> HandleAsync(
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

	private static ValueTask<IResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.MutateJobAsync(
		() => storage.CancelAsync(command.JobId, cancellationToken)
	);
}

[Handler]
[MapDelete("jobs/{jobId}")]
[MapGroup<DashboardApi>]
internal static partial class DeleteDashboardJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[NotEmpty]
		public required string JobId { get; init; }
	}

	private static ValueTask<IResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.MutateJobAsync(
		() => storage.DeleteAsync(command.JobId, cancellationToken)
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

	private static async ValueTask<IResult> HandleAsync(
		Command command,
		IServiceProvider serviceProvider,
		IJobStorage storage,
		IEnumerable<JobDefinition> definitions,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var schedule = snapshot.Recurring.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, command.Name, StringComparison.Ordinal));
		if (schedule is null)
			return Results.NotFound();

		var definition = definitions.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, schedule.JobName, StringComparison.Ordinal));
		if (definition is null)
		{
			return Results.Problem(
				detail: $"No generated job definition exists for '{schedule.JobName}'.",
				statusCode: StatusCodes.Status409Conflict
			);
		}

		var idGenerator = serviceProvider.GetRequiredService<IIdGenerator>();
		var now = serviceProvider.GetService<TimeProvider>()?.GetUtcNow() ?? TimeProvider.System.GetUtcNow();
		var job = new JobRecord
		{
			Id = idGenerator.CreateId(IdKind.Job),
			JobName = schedule.JobName,
			QueueName = definition.Queue.Name,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
		};
		await storage.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
		return Results.Accepted();
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

	private static ValueTask<IResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => storage is IRecurringJobStorage recurringStorage
		? DashboardApiEndpointOperations.MutateRecurringAsync(
			() => recurringStorage.PauseRecurringAsync(command.Name, cancellationToken)
		)
		: ValueTask.FromResult<IResult>(Results.NotFound());
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

	private static ValueTask<IResult> HandleAsync(
		Command command,
		IJobStorage storage,
		CancellationToken cancellationToken
	) => storage is IRecurringJobStorage recurringStorage
		? DashboardApiEndpointOperations.MutateRecurringAsync(
			() => recurringStorage.ResumeRecurringAsync(command.Name, cancellationToken)
		)
		: ValueTask.FromResult<IResult>(Results.NotFound());
}

[Handler]
[MapGet("events")]
[MapGroup<DashboardApi>]
internal static partial class StreamDashboardEvents
{
	internal sealed record Query;

	private static async ValueTask<IResult> HandleAsync(
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
		).ConfigureAwait(false);
		return Results.Empty;
	}
}

internal static class DashboardApiEndpointOperations
{
	internal static async ValueTask<IResult> GetJobTelemetryLinksAsync(
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
		).ConfigureAwait(false);
		var job = jobs.SingleOrDefault();
		if (job is null)
			return Results.NotFound();

		JobExecutionRecord? execution = null;
		if (executionNumber is { } attempt)
		{
			var executions = await storage.QueryJobExecutionsAsync(
				new() { JobId = jobId, Attempt = attempt, Take = 1 },
				cancellationToken
			).ConfigureAwait(false);
			execution = executions.SingleOrDefault();
			if (execution is null)
				return Results.NotFound();
		}

		if (options.TelemetryLinks.Count == 0)
			return Results.Json([], DashboardJsonSerializerContext.Default.JobTelemetryLinkArray);

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

		return Results.Json([.. links], DashboardJsonSerializerContext.Default.JobTelemetryLinkArray);
	}

	internal static ValueTask<IResult> MutateJobAsync(Func<ValueTask> operation) => MutateAsync(
		operation,
		includeConflict: true
	);

	internal static ValueTask<IResult> MutateBatchAsync(Func<ValueTask> operation) => MutateAsync(
		operation,
		includeConflict: true
	);

	internal static ValueTask<IResult> MutateRecurringAsync(Func<ValueTask> operation) => MutateAsync(
		operation,
		includeConflict: false
	);

	private static async ValueTask<IResult> MutateAsync(
		Func<ValueTask> operation,
		bool includeConflict
	)
	{
		try
		{
			await operation().ConfigureAwait(false);
			return Results.NoContent();
		}
		catch (KeyNotFoundException)
		{
			return Results.NotFound();
		}
		catch (ImmediateJobException exception) when (includeConflict)
		{
			return Results.Problem(
				detail: exception.Message,
				statusCode: StatusCodes.Status409Conflict
			);
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
			await context.Response.WriteAsync("retry: 3000\n\n", cancellationToken).ConfigureAwait(false);
			await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

			while (!cancellationToken.IsCancellationRequested)
			{
				var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
				snapshot = snapshot with { Capabilities = storage.GetCapabilities() };
				var jobs = await storage.QueryJobsAsync(new() { Take = 100 }, cancellationToken).ConfigureAwait(false);
				var batches = storage is IJobGraphStorage graphStorage
					? await graphStorage.QueryBatchesAsync(new() { Take = 100 }, cancellationToken)
						.ConfigureAwait(false)
					: [];
				var state = new DashboardState(snapshot, [.. jobs], [.. batches]);
				var json = JsonSerializer.Serialize(state, DashboardJsonSerializerContext.Default.DashboardState);
				await context.Response.WriteAsync(
					"id: " + snapshot.CapturedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "\n",
					cancellationToken
				).ConfigureAwait(false);
				await context.Response.WriteAsync("event: state\ndata: " + json + "\n\n", cancellationToken)
					.ConfigureAwait(false);
				await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
				await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
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

		var status = await graphStorage.GetBatchStatusAsync(batchId, cancellationToken).ConfigureAwait(false);
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
			await context.Response.WriteAsync("retry: 3000\n\n", cancellationToken).ConfigureAwait(false);
			await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

			while (!cancellationToken.IsCancellationRequested)
			{
				status = await graphStorage.GetBatchStatusAsync(batchId, cancellationToken).ConfigureAwait(false);
				var graph = await graphStorage.GetBatchGraphAsync(batchId, cancellationToken).ConfigureAwait(false);
				if (status is null || graph is null)
					break;

				var statusJson = JsonSerializer.Serialize(status, DashboardJsonSerializerContext.Default.BatchStatus);
				var graphJson = JsonSerializer.Serialize(graph, DashboardJsonSerializerContext.Default.BatchGraph);
				var currentState = statusJson + graphJson;
				if (!string.Equals(previousState, currentState, StringComparison.Ordinal))
				{
					var eventId = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()
						.ToString(CultureInfo.InvariantCulture);
					await context.Response.WriteAsync("id: " + eventId + "\n", cancellationToken)
						.ConfigureAwait(false);
					await context.Response.WriteAsync("event: status\ndata: " + statusJson + "\n\n", cancellationToken)
						.ConfigureAwait(false);
					await context.Response.WriteAsync("event: graph\ndata: " + graphJson + "\n\n", cancellationToken)
						.ConfigureAwait(false);
					await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
					previousState = currentState;
				}

				await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}
}
