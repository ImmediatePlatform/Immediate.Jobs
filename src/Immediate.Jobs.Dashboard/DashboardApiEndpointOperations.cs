using System.Globalization;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

namespace Immediate.Jobs.Dashboard;

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
	internal static Results<JsonHttpResult<IReadOnlyList<JobTelemetryLink>>, NotFound> TransformTelemetryLinksResult(
		IReadOnlyList<JobTelemetryLink>? result
	) => result is null
		? TypedResults.NotFound()
		: TypedResults.Json(result, DashboardJsonSerializerContext.Default.IReadOnlyListJobTelemetryLink);

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

	internal static async ValueTask<IReadOnlyList<JobTelemetryLink>?> GetJobTelemetryLinksAsync(
		string jobId,
		int? executionNumber,
		JobMonitor monitor,
		ImmediateJobsDashboardOptions options,
		CancellationToken cancellationToken
	)
	{
		var job = await monitor.GetJobAsync(jobId, cancellationToken);
		if (job is null)
			return null;

		var executions = await monitor.QueryExecutionsAsync(
			new() { JobId = jobId, Attempt = executionNumber, Take = 1 },
			cancellationToken
		);
		var telemetryExecution = executions.SingleOrDefault();
		if (executionNumber is not null && telemetryExecution is null)
			return null;
		var exactExecution = executionNumber is null ? null : telemetryExecution;

		if (options.TelemetryLinks.Count == 0)
			return [];

		var contextJob = new JobRecord
		{
			Id = job.JobId,
			JobName = job.JobName,
			QueueName = job.QueueName,
			Payload = string.Empty,
			State = job.State,
			Attempt = telemetryExecution?.Attempt ?? job.Attempt,
			DueAt = job.DueAt,
			CreatedAt = job.CreatedAt,
			CompletedAt = job.CompletedAt,
			LastError = job.LastError,
			BatchId = job.BatchId,
			ExecutionTraceId = telemetryExecution?.ExecutionTraceId,
			ExecutionSpanId = telemetryExecution?.ExecutionSpanId,
			ExecutionStartedAt = telemetryExecution?.ExecutionStartedAt,
		};
		var context = new JobTelemetryLinkContext(contextJob) { Execution = exactExecution, MaxAttempts = job.MaxAttempts };
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
		JobMonitor monitor,
		TimeProvider timeProvider,
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
				var snapshot = await monitor.GetSnapshotAsync(cancellationToken);
				var jobs = await monitor.QueryJobsAsync(new() { Take = 100 }, cancellationToken);
				var batches = await monitor.QueryBatchesAsync(new() { Take = 100 }, cancellationToken) ?? [];
				var state = new DashboardState(snapshot, [.. jobs], [.. batches]);
				var json = JsonSerializer.Serialize(state, DashboardJsonSerializerContext.Default.DashboardState);
				await context.Response.WriteAsync(
					"id: " + snapshot.CapturedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "\n",
					cancellationToken
				);
				await context.Response.WriteAsync("event: state\ndata: " + json + "\n\n", cancellationToken);
				await context.Response.Body.FlushAsync(cancellationToken);
				await Task.Delay(interval, timeProvider, cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	internal static async Task StreamBatchEventsAsync(
		HttpContext context,
		string batchId,
		JobMonitor monitor,
		TimeProvider timeProvider,
		TimeSpan interval,
		CancellationToken cancellationToken
	)
	{
		var status = await monitor.GetBatchAsync(batchId, cancellationToken);
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
				status = await monitor.GetBatchAsync(batchId, cancellationToken);
				var graph = await monitor.GetBatchGraphAsync(batchId, cancellationToken);
				if (status is null || graph is null)
					break;

				var statusJson = JsonSerializer.Serialize(status, DashboardJsonSerializerContext.Default.BatchStatus);
				var graphJson = JsonSerializer.Serialize(graph, DashboardJsonSerializerContext.Default.BatchGraph);
				var currentState = statusJson + graphJson;
				if (!string.Equals(previousState, currentState, StringComparison.Ordinal))
				{
					var eventId = timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
						.ToString(CultureInfo.InvariantCulture);
					await context.Response.WriteAsync("id: " + eventId + "\n", cancellationToken);
					await context.Response.WriteAsync("event: status\ndata: " + statusJson + "\n\n", cancellationToken);
					await context.Response.WriteAsync("event: graph\ndata: " + graphJson + "\n\n", cancellationToken);
					await context.Response.Body.FlushAsync(cancellationToken);
					previousState = currentState;
				}

				await Task.Delay(interval, timeProvider, cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}
}
