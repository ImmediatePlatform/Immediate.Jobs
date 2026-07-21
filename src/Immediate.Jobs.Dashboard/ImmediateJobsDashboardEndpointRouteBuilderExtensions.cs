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
		if (!prefix.StartsWith('/'))
			prefix = "/" + prefix;

		prefix = prefix.TrimEnd('/');
		if (prefix.Length == 0)
			throw new ArgumentException("The dashboard must be mapped below the application root.", nameof(prefix));

		var options = new ImmediateJobsDashboardOptions();
		configure?.Invoke(options);
		if (options.UpdateInterval <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(configure), "The dashboard update interval must be positive.");

		var group = endpoints.MapGroup(prefix).WithTags("Immediate.Jobs Dashboard");
		if (options.AuthorizationPolicy is { } policy)
			group.RequireAuthorization(new AuthorizeAttribute(policy));
		else
			group.AddEndpointFilter<DevelopmentDashboardFilter>();

		MapApi(group, options);
		group.MapGet("/", (Delegate)((HttpContext context) =>
			context.Request.Path.Value?.EndsWith('/') is true
				? DashboardAssets.GetAsync("index.html")
				: Task.FromResult<IResult>(Results.Redirect(prefix + "/"))
		)).ExcludeFromDescription();
		group.MapGet("/app.css", () => DashboardAssets.GetAsync("app.css")).ExcludeFromDescription();
		group.MapGet("/app.js", () => DashboardAssets.GetAsync("app.js")).ExcludeFromDescription();
		group.MapGet("/{**path}", (string path) =>
			path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
				? Task.FromResult<IResult>(Results.NotFound())
				: DashboardAssets.GetAsync("index.html")
		).WithOrder(int.MaxValue).ExcludeFromDescription();

		return group;
	}

	private static void MapApi(RouteGroupBuilder dashboard, ImmediateJobsDashboardOptions options)
	{
		var api = dashboard.MapGroup("/api");

		api.MapGet("/overview", async (IJobStorage storage, CancellationToken cancellationToken) =>
		{
			var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
			return Results.Json(snapshot, DashboardJsonSerializerContext.Default.JobMonitoringSnapshot);
		});

		api.MapGet("/jobs", async (
			IJobStorage storage,
			JobState? state,
			string? queue,
			string? search,
			int? skip,
			int? take,
			CancellationToken cancellationToken
		) =>
		{
			var query = new JobQuery
			{
				State = state,
				QueueName = queue,
				Search = search,
				Skip = Math.Max(0, skip ?? 0),
				Take = Math.Clamp(take ?? 100, 1, 500),
			};
			var jobs = await storage.QueryJobsAsync(query, cancellationToken).ConfigureAwait(false);
			return Results.Json(jobs.ToArray(), DashboardJsonSerializerContext.Default.JobRecordArray);
		});

		api.MapGet("/jobs/{jobId:guid}", GetJobAsync);
		api.MapGet("/recurring", async (IJobStorage storage, CancellationToken cancellationToken) =>
		{
			var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
			return Results.Json(snapshot.Recurring.ToArray(), DashboardJsonSerializerContext.Default.RecurringJobScheduleArray);
		});
		api.MapGet("/servers", async (IJobStorage storage, CancellationToken cancellationToken) =>
		{
			var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
			return Results.Json(snapshot.Servers.ToArray(), DashboardJsonSerializerContext.Default.JobServerSnapshotArray);
		});
		api.MapPost("/jobs/{jobId:guid}/retry", RetryJobAsync);
		api.MapDelete("/jobs/{jobId:guid}", DeleteJobAsync);
		api.MapPost("/recurring/{name}/trigger", TriggerRecurringAsync);
		api.MapPost("/recurring/{name}/pause", (
			string name,
			IJobStorage storage,
			CancellationToken cancellationToken
		) => MutateRecurringAsync(storage.PauseRecurringAsync(name, cancellationToken)));
		api.MapPost("/recurring/{name}/resume", (
			string name,
			IJobStorage storage,
			CancellationToken cancellationToken
		) => MutateRecurringAsync(storage.ResumeRecurringAsync(name, cancellationToken)));
		api.MapGet("/events", (HttpContext context, IJobStorage storage) =>
			StreamEventsAsync(context, storage, options.UpdateInterval));
	}

	private static async Task<IResult> GetJobAsync(
		Guid jobId,
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
		Guid jobId,
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
		Guid jobId,
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
			Id = Guid.NewGuid(),
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
				var state = new DashboardState(snapshot, jobs.ToArray());
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
}
