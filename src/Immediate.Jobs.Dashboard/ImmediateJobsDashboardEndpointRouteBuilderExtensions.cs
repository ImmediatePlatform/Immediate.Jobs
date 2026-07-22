using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.Dashboard;

/// <summary>Maps the embedded Immediate.Jobs dashboard and monitoring API.</summary>
public static class ImmediateJobsDashboardEndpointRouteBuilderExtensions
{
	/// <summary>Maps the dashboard at <c>/jobs</c>.</summary>
	public static RouteGroupBuilder MapImmediateJobsDashboard(
		this IEndpointRouteBuilder endpoints,
		Action<ImmediateJobsDashboardOptions>? configure = null
	) => endpoints.MapImmediateJobsDashboard("/jobs", configure);

	/// <summary>Maps the dashboard and its API under <paramref name="prefix"/>.</summary>
	public static RouteGroupBuilder MapImmediateJobsDashboard(
		this IEndpointRouteBuilder endpoints,
		string prefix,
		Action<ImmediateJobsDashboardOptions>? configure = null
	)
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
		if (prefix[0] != '/')
			prefix = "/" + prefix;

		prefix = prefix.TrimEnd('/');
		if (prefix.Length == 0)
			throw new ArgumentException("The dashboard must be mapped below the application root.", nameof(prefix));

		var options = new ImmediateJobsDashboardOptions();
		configure?.Invoke(options);
		if (options.UpdateInterval <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(configure), "The dashboard update interval must be positive.");

		var group = endpoints.MapGroup(prefix).WithTags("Immediate.Jobs Dashboard");
		_ = options.AuthorizationPolicy is { } policy
			? group.RequireAuthorization(new AuthorizeAttribute(policy))
			: group.AddEndpointFilter(new DevelopmentDashboardFilter());

		MapApi(group, options);
		_ = group.MapGet("/", (Delegate)((HttpContext context) =>
			context.Request.Path.Value is { Length: > 0 } path && path[^1] == '/'
				? DashboardAssets.GetIndexAsync(context, prefix)
				: Task.FromResult<IResult>(Results.Redirect(prefix + "/"))
		)).ExcludeFromDescription();
		_ = group.MapGet("/app.css", () => DashboardAssets.GetAsync("app.css")).ExcludeFromDescription();
		_ = group.MapGet("/app.js", () => DashboardAssets.GetAsync("app.js")).ExcludeFromDescription();
		_ = group.MapGet("/{**path}", (string path, HttpContext context) =>
			path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
				? Task.FromResult<IResult>(Results.NotFound())
				: DashboardAssets.GetIndexAsync(context, prefix)
		).WithOrder(int.MaxValue).ExcludeFromDescription();

		return group;
	}

	private static void MapApi(RouteGroupBuilder dashboard, ImmediateJobsDashboardOptions options)
	{
		var api = dashboard.MapGroup("/api");

		_ = api.MapGet("/overview", async (IJobStorage storage, CancellationToken cancellationToken) =>
		{
			var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
			return Results.Json(snapshot, DashboardJsonSerializerContext.Default.JobMonitoringSnapshot);
		});

		_ = api.MapGet("/jobs", async (
			IJobStorage storage,
			JobState? state,
			string? queue,
			string? search,
			int? skip,
			int? take,
			CancellationToken cancellationToken
		) =>
		{
			var pageSize = Math.Clamp(take ?? 50, 1, 200);
			var pageStart = Math.Max(0, skip ?? 0);
			var query = new JobQuery
			{
				State = state,
				QueueName = queue,
				Search = search,
				Skip = pageStart,
				Take = pageSize + 1,
			};
			var jobs = await storage.QueryJobsAsync(query, cancellationToken).ConfigureAwait(false);
			var page = new DashboardJobPage(
				[.. jobs.Take(pageSize)],
				pageStart,
				pageSize,
				jobs.Count > pageSize
			);
			return Results.Json(page, DashboardJsonSerializerContext.Default.DashboardJobPage);
		});

		_ = api.MapGet("/jobs/{jobId}", GetJobAsync);
		_ = api.MapGet("/batches", async (
			IJobStorage storage,
			BatchState? state,
			int? skip,
			int? take,
			CancellationToken cancellationToken
		) => Results.Json(
			[.. await storage.QueryBatchesAsync(new()
			{
				State = state,
				Skip = Math.Max(0, skip ?? 0),
				Take = Math.Clamp(take ?? 100, 1, 500),
			}, cancellationToken).ConfigureAwait(false)],
			DashboardJsonSerializerContext.Default.BatchStatusArray
		));
		_ = api.MapGet("/batches/{batchId}", async (
			string batchId,
			IJobStorage storage,
			CancellationToken cancellationToken
		) => await storage.GetBatchStatusAsync(batchId, cancellationToken).ConfigureAwait(false) is { } status
			? Results.Json(status, DashboardJsonSerializerContext.Default.BatchStatus)
			: Results.NotFound());
		_ = api.MapGet("/batches/{batchId}/members", async (
			string batchId,
			IJobStorage storage,
			JobState? state,
			int? skip,
			int? take,
			CancellationToken cancellationToken
		) => Results.Json(
			[.. await storage.QueryBatchMembersAsync(batchId, new()
			{
				State = state,
				Skip = Math.Max(0, skip ?? 0),
				Take = Math.Clamp(take ?? 100, 1, 500),
			}, cancellationToken).ConfigureAwait(false)],
			DashboardJsonSerializerContext.Default.BatchMemberStatusArray
		));
		_ = api.MapGet("/batches/{batchId}/graph", async (
			string batchId,
			IJobStorage storage,
			CancellationToken cancellationToken
		) => await storage.GetBatchGraphAsync(batchId, cancellationToken).ConfigureAwait(false) is { } graph
			? Results.Json(graph, DashboardJsonSerializerContext.Default.BatchGraph)
			: Results.NotFound());
		_ = api.MapGet("/batches/{batchId}/stream", (
			string batchId,
			HttpContext context,
			IJobStorage storage
		) => StreamBatchEventsAsync(context, batchId, storage, options.UpdateInterval));
		_ = api.MapPost("/batches/{batchId}/cancel", async (
			string batchId,
			IJobStorage storage,
			CancellationToken cancellationToken
		) => await MutateBatchAsync(storage.CancelBatchAsync(batchId, cancellationToken)).ConfigureAwait(false));
		_ = api.MapDelete("/batches/{batchId}", async (
			string batchId,
			IJobStorage storage,
			CancellationToken cancellationToken
		) => await MutateBatchAsync(storage.DeleteBatchAsync(batchId, cancellationToken)).ConfigureAwait(false));
		_ = api.MapGet("/recurring", async (IJobStorage storage, CancellationToken cancellationToken) =>
		{
			var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
			return Results.Json([.. snapshot.Recurring], DashboardJsonSerializerContext.Default.RecurringJobScheduleArray);
		});
		_ = api.MapGet("/servers", async (IJobStorage storage, CancellationToken cancellationToken) =>
		{
			var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
			return Results.Json([.. snapshot.Servers], DashboardJsonSerializerContext.Default.JobServerSnapshotArray);
		});
		_ = api.MapPost("/jobs/{jobId}/retry", RetryJobAsync);
		_ = api.MapDelete("/jobs/{jobId}", DeleteJobAsync);
		_ = api.MapPost("/recurring/{name}/trigger", TriggerRecurringAsync);
		_ = api.MapPost("/recurring/{name}/pause", (
			string name,
			IJobStorage storage,
			CancellationToken cancellationToken
		) => MutateRecurringAsync(storage.PauseRecurringAsync(name, cancellationToken)));
		_ = api.MapPost("/recurring/{name}/resume", (
			string name,
			IJobStorage storage,
			CancellationToken cancellationToken
		) => MutateRecurringAsync(storage.ResumeRecurringAsync(name, cancellationToken)));
		_ = api.MapGet("/events", (HttpContext context, IJobStorage storage) =>
			StreamEventsAsync(context, storage, options.UpdateInterval));
	}

	private static async Task<IResult> GetJobAsync(
		string jobId,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		var jobs = await storage.QueryJobsAsync(new() { Id = jobId, Take = 1 }, cancellationToken).ConfigureAwait(false);
		var job = jobs.SingleOrDefault();
		return job is null
			? Results.NotFound()
			: Results.Json(job, DashboardJsonSerializerContext.Default.JobRecord);
	}

	private static async Task<IResult> RetryJobAsync(
		string jobId,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await storage.RetryAsync(jobId, cancellationToken).ConfigureAwait(false);
			return Results.NoContent();
		}
		catch (KeyNotFoundException)
		{
			return Results.NotFound();
		}
		catch (InvalidOperationException exception)
		{
			return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
		}
	}

	private static async Task<IResult> DeleteJobAsync(
		string jobId,
		IJobStorage storage,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await storage.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
			return Results.NoContent();
		}
		catch (KeyNotFoundException)
		{
			return Results.NotFound();
		}
		catch (InvalidOperationException exception)
		{
			return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
		}
	}

	private static async Task<IResult> TriggerRecurringAsync(
		string name,
		HttpContext context,
		IJobStorage storage,
		IEnumerable<JobDefinition> definitions,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var schedule = snapshot.Recurring.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, name, StringComparison.Ordinal));
		if (schedule is null)
			return Results.NotFound();
		var definition = definitions.FirstOrDefault(candidate => candidate.Name == schedule.JobName);
		if (definition is null)
			return Results.Problem(
				detail: $"No generated job definition exists for '{schedule.JobName}'.",
				statusCode: StatusCodes.Status409Conflict
			);

		var now = context.RequestServices.GetService<TimeProvider>()?.GetUtcNow() ?? TimeProvider.System.GetUtcNow();
		var job = new JobRecord
		{
			Id = Guid.NewGuid().ToString("N"),
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

	private static async Task<IResult> MutateRecurringAsync(ValueTask operation)
	{
		try
		{
			await operation.ConfigureAwait(false);
			return Results.NoContent();
		}
		catch (KeyNotFoundException)
		{
			return Results.NotFound();
		}
	}

	private static async Task<IResult> MutateBatchAsync(ValueTask operation)
	{
		try
		{
			await operation.ConfigureAwait(false);
			return Results.NoContent();
		}
		catch (KeyNotFoundException)
		{
			return Results.NotFound();
		}
		catch (InvalidOperationException exception)
		{
			return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
		}
	}

	private static async Task StreamEventsAsync(
		HttpContext context,
		IJobStorage storage,
		TimeSpan interval
	)
	{
		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = "text/event-stream";
		context.Response.Headers.CacheControl = "no-cache, no-store";
		context.Response.Headers.Append("X-Accel-Buffering", "no");
		var cancellationToken = context.RequestAborted;

		try
		{
			await context.Response.WriteAsync("retry: 3000\n\n", cancellationToken).ConfigureAwait(false);
			await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

			while (!cancellationToken.IsCancellationRequested)
			{
				var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
				var jobs = await storage.QueryJobsAsync(new() { Take = 100 }, cancellationToken).ConfigureAwait(false);
				var batches = await storage.QueryBatchesAsync(new() { Take = 100 }, cancellationToken)
					.ConfigureAwait(false);
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

	private static async Task StreamBatchEventsAsync(
		HttpContext context,
		string batchId,
		IJobStorage storage,
		TimeSpan interval
	)
	{
		var cancellationToken = context.RequestAborted;
		var status = await storage.GetBatchStatusAsync(batchId, cancellationToken).ConfigureAwait(false);
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
				status = await storage.GetBatchStatusAsync(batchId, cancellationToken).ConfigureAwait(false);
				var graph = await storage.GetBatchGraphAsync(batchId, cancellationToken).ConfigureAwait(false);
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
