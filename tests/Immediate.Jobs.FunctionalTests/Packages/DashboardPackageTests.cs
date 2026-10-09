using System.Globalization;
using System.Net;
using System.Text.Json;
using Immediate.Jobs.Dashboard;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Packages;

public sealed class DashboardPackageTests
{
	[Fact]
	public async Task DefinitionActionsPausePendingWorkAndResumeWithRegisteredLimits()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
		builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsCore().DisableWorkers().ConfigureStorage(options => _ = options.UseInMemory()).AddImmediateJobsDashboard();
		_ = LimitedAcquisitionJob.AddJob(builder.Services);
		ConfigureDashboardTestServices(builder.Services, storage, clock);
		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard("/operations/background-work");
		await app.StartAsync(token);
		await app.Services.GetRequiredService<JobSchedulingService>().DrainAsync(token);
		using var client = app.GetTestClient();
		var monitor = app.Services.GetRequiredService<Immediate.Jobs.Shared.Interfaces.IJobMonitor>();
		var definition = Assert.Single((await monitor.GetDefinitionsAsync(token)).Jobs);
		await storage.EnqueueAsync(new()
		{
			JobHandle = JobHandle.FromString("definition-action-active"),
			JobName = definition.Name,
			Payload = "{}",
			State = JobState.Pending,
			CreatedAt = clock.GetUtcNow(),
			DueAt = clock.GetUtcNow(),
		}, token);
		var request = new JobAcquisitionRequest
		{
			WorkerId = "dashboard-worker",
			Lease = TimeSpan.FromSeconds(30),
			BatchSize = 1,
			Queues = [new() { QueueName = definition.QueueName, Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [definition.Name] = 2 } }],
			JobLimits = new Dictionary<string, JobAcquisitionLimits>(StringComparer.Ordinal) { [definition.Name] = definition.AcquisitionLimits },
		};
		_ = Assert.Single(await storage.AcquireDueJobsAsync(request, token));
		var pendingHandle = JobHandle.FromString("definition-action-pending");
		await storage.EnqueueAsync(new()
		{
			JobHandle = pendingHandle,
			JobName = definition.Name,
			Payload = "{}",
			State = JobState.Pending,
			CreatedAt = clock.GetUtcNow(),
			DueAt = clock.GetUtcNow(),
		}, token);
		foreach (var action in new[] { "pause", "pause", "resume", "resume" })
		{
			using var response = await client.PostAsync(
				new Uri($"/operations/background-work/api/definitions/{definition.Name}/{action}", UriKind.Relative),
				content: null,
				token
			);
			Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
			using var statusResponse = await client.GetAsync(new Uri("/operations/background-work/api/overview", UriKind.Relative), token);
			_ = statusResponse.EnsureSuccessStatusCode();
			using var document = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync(token));
			var acquisition = Assert.Single(document.RootElement.GetProperty("definitionStatuses").EnumerateArray());
			Assert.Equal(string.Equals(action, "pause", StringComparison.Ordinal), acquisition.GetProperty("isPaused").GetBoolean());
			Assert.Equal(string.Equals(action, "pause", StringComparison.Ordinal) ? "Paused" : "RateLimited", acquisition.GetProperty("acquisitionStatus").GetString());
			Assert.Equal(1, acquisition.GetProperty("activeCount").GetInt32());
			Assert.Empty(await storage.AcquireDueJobsAsync(request, token));
			var pending = await monitor.GetJobAsync(pendingHandle, token);
			Assert.NotNull(pending);
			Assert.Equal(JobState.Pending, pending.State);
			Assert.Equal(0, pending.Attempt);
		}

		foreach (var action in new[] { "pause", "resume" })
		{
			using var response = await client.PostAsync(
				new Uri($"/operations/background-work/api/definitions/missing-definition/{action}", UriKind.Relative),
				content: null,
				token
			);
			Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		}

		Assert.False((await storage.GetJobAcquisitionStateAsync("missing-definition", new(), token)).IsPaused);
		Assert.Empty(await storage.AcquireDueJobsAsync(request, token));
		clock.Advance(TimeSpan.FromSeconds(2));
		Assert.Equal(pendingHandle, Assert.Single(await storage.AcquireDueJobsAsync(request, token)).JobHandle);
	}

	[Fact]
	public async Task DefinitionsApiAndOverviewExposePauseAndConcurrencyIndependently()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.EnqueueAsync(new()
		{
			JobHandle = JobHandle.FromString("definition-dashboard-job"),
			JobName = "limited-acquisition",
			Payload = "{}",
			State = JobState.Pending,
			CreatedAt = clock.GetUtcNow(),
			DueAt = clock.GetUtcNow(),
		}, token);
		_ = await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "dashboard-worker",
			Lease = TimeSpan.FromSeconds(30),
			BatchSize = 1,
			Queues = [new() { QueueName = "default", Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { ["limited-acquisition"] = 1 } }],
			JobLimits = new Dictionary<string, JobAcquisitionLimits>(StringComparer.Ordinal) { ["limited-acquisition"] = new() { MaxConcurrency = 1 } },
		}, token);
		await storage.PauseJobAsync("limited-acquisition", token);
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
		builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsCore().DisableWorkers().ConfigureStorage(options => _ = options.UseInMemory()).AddImmediateJobsDashboard();
		_ = LimitedAcquisitionJob.AddJob(builder.Services);
		builder.Services.Replace(ServiceDescriptor.Singleton<JobDefinition>(provider => LimitedAcquisitionJob.CreateJobDefinition(provider) with { MaxConcurrency = 1 }));
		ConfigureDashboardTestServices(builder.Services, storage, clock);
		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(token);
		await app.Services.GetRequiredService<JobSchedulingService>().DrainAsync(token);
		using var client = app.GetTestClient();
		IReadOnlyList<string> routes = ["/jobs/api/definitions", "/jobs/api/overview"];
		foreach (var route in routes)
		{
			using var response = await client.GetAsync(new Uri(route, UriKind.Relative), token);
			_ = response.EnsureSuccessStatusCode();
			using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
			if (route.EndsWith("definitions", StringComparison.Ordinal))
			{
				var definition = Assert.Single(document.RootElement.GetProperty("jobs").EnumerateArray());
				Assert.Equal("limited-acquisition", definition.GetProperty("name").GetString());
				Assert.Equal(1, definition.GetProperty("maxConcurrency").GetInt32());
				Assert.False(definition.TryGetProperty("acquisition", out _));
			}
			else
			{
				var acquisition = Assert.Single(document.RootElement.GetProperty("definitionStatuses").EnumerateArray());
				Assert.Equal("limited-acquisition", acquisition.GetProperty("jobName").GetString());
				Assert.True(acquisition.GetProperty("isPaused").GetBoolean());
				Assert.Equal("Paused", acquisition.GetProperty("acquisitionStatus").GetString());
				Assert.Equal(1, acquisition.GetProperty("activeCount").GetInt32());
				Assert.True(acquisition.GetProperty("isConcurrencyLimited").GetBoolean());
				Assert.False(acquisition.TryGetProperty("limits", out _));
			}
		}
	}

	private static void ConfigureDashboardTestServices(
		IServiceCollection services,
		IJobStorage storage,
		FakeTimeProvider timeProvider
	)
	{
		services.Replace(ServiceDescriptor.Singleton(storage));
		services.Replace(ServiceDescriptor.Singleton<TimeProvider>(timeProvider));
		services.RemoveAll<IHostedService>();
	}

	[Theory]
	[InlineData("/jobs")]
	[InlineData("/monitor")]
	public async Task DefinitionsAndSnapshotsShowStorageCatalogueBeyondLocalTagsAndRegistrations(string prefix)
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [new() { Name = "ordinary" }] }, token);
		await storage.MergeJobDefinitionsListAsync(new()
		{
			ServerTags = ["email"],
			Definitions = [new()
			{
				Name = "remote-email",
				Tags = ["email"],
				QueueName = "priority",
				MaxAttempts = 7,
				SlidingWindowMax = 1,
				SlidingWindowPeriod = TimeSpan.FromMinutes(1),
				MaxConcurrency = 2,
			}],
		}, token);
		await storage.HeartbeatAsync(new()
		{
			WorkerId = "email-server",
			Tags = ["email", "default"],
			LastHeartbeat = clock.GetUtcNow(),
			ActiveWorkers = 0,
			MaxWorkers = 1,
		}, token);
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
		builder.WebHost.UseTestServer();
		builder.Services.AddImmediateJobsCore().DisableWorkers()
			.ConfigureWorkers(options => options.Tags = ["dashboard"])
			.ConfigureStorage(options => options.UseInMemory())
			.AddImmediateJobsDashboard();
		ConfigureDashboardTestServices(builder.Services, storage, clock);
		await using var app = builder.Build();
		app.MapImmediateJobsDashboard(prefix);
		await app.StartAsync(token);
		using var definitionsResponse = await app.GetTestClient().GetAsync(new Uri($"{prefix}/api/definitions", UriKind.Relative), token);
		definitionsResponse.EnsureSuccessStatusCode();
		using var definitions = JsonDocument.Parse(await definitionsResponse.Content.ReadAsStringAsync(token));
		Assert.Equal(["ordinary", "remote-email"], definitions.RootElement.GetProperty("jobs").EnumerateArray().Select(static item => item.GetProperty("name").GetString()));
		var remote = definitions.RootElement.GetProperty("jobs")[1];
		Assert.Equal("email", remote.GetProperty("tags")[0].GetString());
		Assert.Equal("priority", remote.GetProperty("queueName").GetString());
		Assert.Equal(7, remote.GetProperty("maxAttempts").GetInt32());
		var monitor = app.Services.GetRequiredService<Immediate.Jobs.Shared.Interfaces.IJobMonitor>();
		var remoteStatus = (await monitor.GetDefinitionsAsync(token)).Jobs.Single(definition => string.Equals(definition.Name, "remote-email", StringComparison.OrdinalIgnoreCase));
		Assert.NotNull(remoteStatus);
		Assert.Equal(1, remoteStatus.AcquisitionLimits.SlidingWindowMax);
		await storage.EnqueueAsync(new()
		{
			JobHandle = JobHandle.FromString("remote-definition-active"),
			JobName = remoteStatus.Name,
			QueueName = remoteStatus.QueueName,
			Payload = "{}",
			State = JobState.Pending,
			CreatedAt = clock.GetUtcNow(),
			DueAt = clock.GetUtcNow(),
		}, token);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "remote-worker",
			Lease = TimeSpan.FromMinutes(5),
			BatchSize = 1,
			Queues = [new() { QueueName = remoteStatus.QueueName, Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [remoteStatus.Name] = 1 } }],
			JobLimits = new Dictionary<string, JobAcquisitionLimits>(StringComparer.Ordinal) { [remoteStatus.Name] = remoteStatus.AcquisitionLimits },
		}, token));
		foreach (var action in new[] { "pause", "resume" })
		{
			using var response = await app.GetTestClient().PostAsync(new Uri($"{prefix}/api/definitions/{remoteStatus.Name}/{action}", UriKind.Relative), content: null, token);
			Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
			var status = (await monitor.GetSnapshotAsync(token)).DefinitionStatuses.Single(state => string.Equals(state.JobName, remoteStatus.Name, StringComparison.OrdinalIgnoreCase));
			Assert.NotNull(status);
			Assert.Equal(string.Equals(action, "pause", StringComparison.Ordinal) ? JobAcquisitionStatus.Paused : JobAcquisitionStatus.RateLimited, status.AcquisitionStatus);
			Assert.Equal(1, status.ActiveCount);
		}

		using var overviewResponse = await app.GetTestClient().GetAsync(new Uri($"{prefix}/api/overview", UriKind.Relative), token);
		overviewResponse.EnsureSuccessStatusCode();
		using var snapshot = JsonDocument.Parse(await overviewResponse.Content.ReadAsStringAsync(token));
		Assert.False(snapshot.RootElement.TryGetProperty("definitions", out _));
		Assert.Equal(2, snapshot.RootElement.GetProperty("definitionStatuses").GetArrayLength());
		Assert.Equal("RateLimited", snapshot.RootElement.GetProperty("definitionStatuses")[1].GetProperty("acquisitionStatus").GetString());
		Assert.Equal("email-server", snapshot.RootElement.GetProperty("servers")[0].GetProperty("workerId").GetString());
	}

	[Fact]
	public void PackageEmbedsCompleteSpaAssetSet()
	{
		var resources = typeof(ImmediateJobsDashboardOptions).Assembly.GetManifestResourceNames();

		Assert.Contains("Immediate.Jobs.Dashboard.Assets.index.html", resources);
		Assert.Contains("Immediate.Jobs.Dashboard.Assets.app.css", resources);
		Assert.Contains("Immediate.Jobs.Dashboard.Assets.app.js", resources);
	}

	[Theory]
	[InlineData("Development", false, HttpStatusCode.Redirect)]
	[InlineData("Local", false, HttpStatusCode.Forbidden)]
	[InlineData("Local", true, HttpStatusCode.Redirect)]
	public async Task DashboardEnvironmentRestrictionIsConfigurable(
		string environmentName,
		bool allowInAnyEnvironment,
		HttpStatusCode expectedStatus
	)
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
		var now = timeProvider.GetUtcNow();

		await using var storage = new InMemoryJobStorage(timeProvider);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = environmentName,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard()
			.ConfigureDashboard(o => o.RestrictToDevelopmentEnvironment = !allowInAnyEnvironment);

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var response = await app.GetTestClient().GetAsync(
			new Uri("/jobs", UriKind.Relative),
			TestContext.Current.CancellationToken
		);

		Assert.Equal(expectedStatus, response.StatusCode);
		foreach (var action in new[] { "pause", "resume" })
		{
			using var mutationResponse = await app.GetTestClient().PostAsync(
				new Uri($"/jobs/api/definitions/missing-definition/{action}", UriKind.Relative),
				content: null,
				TestContext.Current.CancellationToken
			);
			Assert.Equal(expectedStatus == HttpStatusCode.Forbidden ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, mutationResponse.StatusCode);
		}
	}

	[Fact]
	public async Task JobAndExecutionTelemetryApisSupplyTheExpectedCallbackContext()
	{
		var jobHandle = JobHandle.FromString("job:with retries");
		const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
		var now = timeProvider.GetUtcNow();

		await using var storage = new InMemoryJobStorage(timeProvider);

		await storage.EnqueueAsync(new()
		{
			JobHandle = jobHandle,
			JobName = "SendGreeting",
			Payload = "{}",
			State = JobState.Succeeded,
			DueAt = now,
			CreatedAt = now,
			Attempt = 3,
			ExecutionTraceId = TraceId,
		}, TestContext.Current.CancellationToken);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();
		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard()
			.AddTelemetryLink(
				"View execution trace",
				JobTelemetryLinkKind.Trace,
				context => context.Execution?.ExecutionTraceId is { } traceId
					? new($"https://traces.example/trace/{traceId}")
					: null
			)
			.AddTelemetryLink(
				"View execution logs",
				JobTelemetryLinkKind.Logs,
				context => context.Execution is { } execution
					? new(string.Create(
						CultureInfo.InvariantCulture,
						$"https://logs.example/search?jobHandle={Uri.EscapeDataString(context.Job.JobHandle.Value)}&attempt={execution.Attempt}"
					))
					: null
			)
			.AddTelemetryLink(
				"View all retry logs",
				JobTelemetryLinkKind.Logs,
				context => context.Execution is null
					? new($"https://logs.example/search?jobHandle={Uri.EscapeDataString(context.Job.JobHandle.Value)}")
					: null
			);

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var jobResponse = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(jobHandle.Value)}/telemetry-links", UriKind.Relative),
			TestContext.Current.CancellationToken
		);

		_ = jobResponse.EnsureSuccessStatusCode();
		using var jobDocument = JsonDocument.Parse(
			await jobResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
		);
		var jobLink = Assert.Single(jobDocument.RootElement.EnumerateArray());
		Assert.Equal("View all retry logs", jobLink.GetProperty("label").GetString());
		Assert.Contains("job%3Awith%20retries", jobLink.GetProperty("url").GetString(), StringComparison.Ordinal);

		using var executionResponse = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(jobHandle.Value)}/executions/3/telemetry-links", UriKind.Relative),
			TestContext.Current.CancellationToken
		);
		_ = executionResponse.EnsureSuccessStatusCode();
		using var executionDocument = JsonDocument.Parse(
			await executionResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
		);
		var executionLinks = executionDocument.RootElement.EnumerateArray().ToList();
		Assert.Equal(2, executionLinks.Count);
		Assert.Equal("View execution trace", executionLinks[0].GetProperty("label").GetString());
		Assert.Equal("Trace", executionLinks[0].GetProperty("kind").GetString());
		Assert.Equal($"https://traces.example/trace/{TraceId}", executionLinks[0].GetProperty("url").GetString());
		Assert.Equal("View execution logs", executionLinks[1].GetProperty("label").GetString());
		Assert.Contains("attempt=3", executionLinks[1].GetProperty("url").GetString(), StringComparison.Ordinal);

		using var pageResponse = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(jobHandle.Value)}/executions?skip=0&take=1", UriKind.Relative),
			TestContext.Current.CancellationToken
		);
		_ = pageResponse.EnsureSuccessStatusCode();
		using var pageDocument = JsonDocument.Parse(
			await pageResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
		);
		var execution = Assert.Single(pageDocument.RootElement.GetProperty("items").EnumerateArray());
		Assert.Equal(3, execution.GetProperty("attempt").GetInt32());
		Assert.True(execution.GetProperty("isSynthetic").GetBoolean());
		Assert.False(pageDocument.RootElement.GetProperty("hasNext").GetBoolean());
	}

	[Fact]
	public async Task ExactExecutionTelemetryScopesLegacyJobProjectionToTheSelectedAttempt()
	{
		var jobHandle = JobHandle.FromString("job:legacy telemetry callback");
		const string FirstTraceId = "11111111111111111111111111111111";
		const string FirstSpanId = "1111111111111111";
		const string LatestTraceId = "22222222222222222222222222222222";
		const string LatestSpanId = "2222222222222222";

		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
		var now = timeProvider.GetUtcNow();

		await using var storage = new InMemoryJobStorage(timeProvider);

		await storage.EnqueueAsync(new()
		{
			JobHandle = jobHandle,
			JobName = "SendGreeting",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
		}, TestContext.Current.CancellationToken);

		var request = new JobAcquisitionRequest
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
					JobCapacities = new Dictionary<string, int> { ["SendGreeting"] = 1 },
				},
			],
		};
		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, TestContext.Current.CancellationToken));
		await storage.SetExecutionTelemetryAsync(
			jobHandle,
			first.Attempt,
			"worker",
			FirstTraceId,
			FirstSpanId,
			now,
			TestContext.Current.CancellationToken
		);
		await storage.FailAsync(jobHandle, first.Attempt, "worker", "first failure", now, TestContext.Current.CancellationToken);
		var latest = Assert.Single(await storage.AcquireDueJobsAsync(request, TestContext.Current.CancellationToken));
		await storage.SetExecutionTelemetryAsync(
			jobHandle,
			latest.Attempt,
			"worker",
			LatestTraceId,
			LatestSpanId,
			now.AddSeconds(1),
			TestContext.Current.CancellationToken
		);
		await storage.CompleteAsync(jobHandle, latest.Attempt, "worker", TestContext.Current.CancellationToken);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard()
			.AddTelemetryLink(
				"Legacy trace callback",
				JobTelemetryLinkKind.Trace,
				context => context.Job.ExecutionTraceId is { } traceId
					? new($"https://traces.example/trace/{traceId}")
					: null
			)
			.AddTelemetryLink(
				"Legacy attempt callback",
				JobTelemetryLinkKind.Logs,
				context => new(string.Create(
					CultureInfo.InvariantCulture,
					$"https://logs.example/search?attempt={context.Job.Attempt}&span={context.Job.ExecutionSpanId}"
				))
			);

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var executionResponse = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(jobHandle.Value)}/executions/1/telemetry-links", UriKind.Relative),
			TestContext.Current.CancellationToken
		);
		_ = executionResponse.EnsureSuccessStatusCode();
		using var executionDocument = JsonDocument.Parse(await executionResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
		var executionLinks = executionDocument.RootElement.EnumerateArray().ToList();
		Assert.Equal($"https://traces.example/trace/{FirstTraceId}", executionLinks[0].GetProperty("url").GetString());
		Assert.Contains("attempt=1", executionLinks[1].GetProperty("url").GetString(), StringComparison.Ordinal);
		Assert.Contains($"span={FirstSpanId}", executionLinks[1].GetProperty("url").GetString(), StringComparison.Ordinal);

		using var jobResponse = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(jobHandle.Value)}/telemetry-links", UriKind.Relative),
			TestContext.Current.CancellationToken
		);
		_ = jobResponse.EnsureSuccessStatusCode();
		using var jobDocument = JsonDocument.Parse(await jobResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
		var jobLinks = jobDocument.RootElement.EnumerateArray().ToList();
		Assert.Equal($"https://traces.example/trace/{LatestTraceId}", jobLinks[0].GetProperty("url").GetString());
		Assert.Contains("attempt=2", jobLinks[1].GetProperty("url").GetString(), StringComparison.Ordinal);
		Assert.Contains($"span={LatestSpanId}", jobLinks[1].GetProperty("url").GetString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("/jobs/api/jobs/missing/executions", HttpStatusCode.NotFound)]
	[InlineData("/jobs/api/jobs/missing/executions/1/telemetry-links", HttpStatusCode.NotFound)]
	[InlineData("/jobs/api/jobs/job/executions?skip=-1", HttpStatusCode.BadRequest)]
	[InlineData("/jobs/api/jobs/job/executions?take=0", HttpStatusCode.BadRequest)]
	public async Task ExecutionApiValidatesPagingAndMissingResources(string path, HttpStatusCode expectedStatus)
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

		await using var storage = new InMemoryJobStorage(timeProvider);

		await storage.EnqueueAsync(new()
		{
			JobHandle = JobHandle.FromString("job"),
			JobName = "validation",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = DateTimeOffset.UnixEpoch,
			CreatedAt = DateTimeOffset.UnixEpoch,
		}, TestContext.Current.CancellationToken);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var response = await app.GetTestClient().GetAsync(
			new Uri(path, UriKind.Relative),
			TestContext.Current.CancellationToken
		);
		Assert.Equal(expectedStatus, response.StatusCode);
		if (expectedStatus == HttpStatusCode.BadRequest)
		{
			Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
			using var document = JsonDocument.Parse(
				await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
			);
			var error = Assert.Single(document.RootElement.GetProperty("errors").EnumerateObject());
			Assert.True(error.Name is "Skip" or "Take");
			Assert.NotEmpty(error.Value.EnumerateArray());
		}
	}

	[Fact]
	public async Task QueueOnlyStorageReportsCapabilitiesAndDisablesBatchApi()
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

		await using var storage = new StorageCapabilityTests.QueueOnlyStorage(timeProvider);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var overviewResponse = await app.GetTestClient().GetAsync(
			new Uri("/jobs/api/overview", UriKind.Relative),
			TestContext.Current.CancellationToken
		);
		_ = overviewResponse.EnsureSuccessStatusCode();
		using var overview = JsonDocument.Parse(
			await overviewResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
		);
		Assert.Equal("Queue", overview.RootElement.GetProperty("capabilities").GetString());

		using var batchesResponse = await app.GetTestClient().GetAsync(
			new Uri("/jobs/api/batches", UriKind.Relative),
			TestContext.Current.CancellationToken
		);
		Assert.Equal(HttpStatusCode.NotFound, batchesResponse.StatusCode);
	}

	[Theory]
	[InlineData("POST", "/jobs/api/recurring/missing/pause")]
	[InlineData("POST", "/jobs/api/recurring/missing/resume")]
	[InlineData("POST", "/jobs/api/batches/missing/cancel")]
	[InlineData("DELETE", "/jobs/api/batches/missing")]
	[InlineData("POST", "/jobs/api/jobs/missing/retry")]
	[InlineData("POST", "/jobs/api/jobs/missing/cancel")]
	public async Task InMemoryDashboardReturnsNotFoundForMissingMutationTargets(string method, string path)
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

		await using var storage = new InMemoryJobStorage(timeProvider);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var request = new HttpRequestMessage(new(method), path);
		using var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task DashboardCancelsNonTerminalJob()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
		var now = timeProvider.GetUtcNow();

		await using var storage = new InMemoryJobStorage(timeProvider);

		await storage.EnqueueAsync(new()
		{
			JobHandle = JobHandle.FromString("dashboard-cancel"),
			JobName = "cancel-test",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
		}, cancellationToken);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(cancellationToken);

		using var response = await app.GetTestClient().PostAsync(
			new Uri("/jobs/api/jobs/dashboard-cancel/cancel", UriKind.Relative),
			content: null,
			cancellationToken
		);

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(JobHandle.FromString("dashboard-cancel"), cancellationToken))!.State);
		using var conflict = await app.GetTestClient().PostAsync(
			new Uri("/jobs/api/jobs/dashboard-cancel/cancel", UriKind.Relative),
			content: null,
			cancellationToken
		);
		Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
	}

	[Theory]
	[InlineData("/jobs/")]
	[InlineData("/jobs/invocations")]
	[InlineData("/jobs/batches/batch-42/jobs/job-7")]
	public async Task SpaRoutesAreUnambiguous(string path)
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

		await using var storage = new InMemoryJobStorage(timeProvider);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var response = await app.GetTestClient().GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

		_ = response.EnsureSuccessStatusCode();
		Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
		Assert.Contains(
			"<base data-dashboard-base href=\"/jobs/\">",
			await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
			StringComparison.Ordinal
		);
	}

	[Fact]
	public async Task CustomDashboardPrefixIsInjectedIntoSpaBase()
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

		await using var storage = new InMemoryJobStorage(timeProvider);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard("/operations/background-work");
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var response = await app.GetTestClient().GetAsync(
			new Uri("/operations/background-work/batches/batch-42", UriKind.Relative),
			TestContext.Current.CancellationToken
		);

		_ = response.EnsureSuccessStatusCode();
		Assert.Contains(
			"<base data-dashboard-base href=\"/operations/background-work/\">",
			await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
			StringComparison.Ordinal
		);
	}

	[Fact]
	public async Task RequestPathBaseIsIncludedInSpaBase()
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

		await using var storage = new InMemoryJobStorage(timeProvider);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.UsePathBase("/tenant");
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var response = await app.GetTestClient().GetAsync(
			new Uri("/tenant/jobs/invocations/job-7", UriKind.Relative),
			TestContext.Current.CancellationToken
		);

		_ = response.EnsureSuccessStatusCode();
		Assert.Contains(
			"<base data-dashboard-base href=\"/tenant/jobs/\">",
			await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
			StringComparison.Ordinal
		);
	}

	[Fact]
	public async Task DashboardRootRedirectsToTrailingSlash()
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

		await using var storage = new InMemoryJobStorage(timeProvider);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var response = await app.GetTestClient().GetAsync(
			new Uri("/jobs", UriKind.Relative),
			TestContext.Current.CancellationToken
		);

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal("/jobs/", response.Headers.Location?.OriginalString);
	}

	[Fact]
	public async Task EventStreamIncludesSucceededJobHistory()
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
		var now = timeProvider.GetUtcNow();
		timeProvider.Advance(TimeSpan.FromSeconds(2));

		await using var storage = new InMemoryJobStorage(timeProvider);

		await storage.EnqueueAsync(new()
		{
			JobHandle = JobHandle.FromString("86bf8c31-d8e6-415b-8e92-45587a09fc52"),
			JobName = "SendGreeting",
			Payload = "{}",
			State = JobState.Succeeded,
			DueAt = now,
			CreatedAt = now,
			CompletedAt = now.AddSeconds(1),
		}, TestContext.Current.CancellationToken);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs/api/events");
		using var response = await app.GetTestClient().SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead,
			timeout.Token
		);
		_ = response.EnsureSuccessStatusCode();

		await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
		using var reader = new StreamReader(stream);

		string? data = null;
		while (await reader.ReadLineAsync(timeout.Token) is { } line)
		{
			if (line.StartsWith("data: ", StringComparison.Ordinal))
			{
				data = line[6..];
				break;
			}
		}

		Assert.NotNull(data);
		using var document = JsonDocument.Parse(data);
		var job = Assert.Single(document.RootElement.GetProperty("jobs").EnumerateArray());
		Assert.Equal("SendGreeting", job.GetProperty("jobName").GetString());
		Assert.Equal("Succeeded", job.GetProperty("state").GetString());
	}

	[Fact]
	public async Task JobApiAcceptsOpaqueStringIdentifiers()
	{
		const string JobId = "redis:jobs:01J2Z4J5Y6K7M8N9P0Q1R2S3T4";

		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
		var now = timeProvider.GetUtcNow();

		await using var storage = new InMemoryJobStorage(timeProvider);

		await storage.EnqueueAsync(new()
		{
			JobHandle = JobHandle.FromString(JobId),
			JobName = "SendGreeting",
			GroupId = "tenant-a",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
		}, TestContext.Current.CancellationToken);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});

		builder.WebHost.UseTestServer();

		builder.Services
			.AddImmediateJobsCore()
			.DisableWorkers()
			.ConfigureStorage(o => o.UseInMemory())
			.AddImmediateJobsDashboard();

		ConfigureDashboardTestServices(builder.Services, storage, timeProvider);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var response = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{JobId}", UriKind.Relative),
			TestContext.Current.CancellationToken
		);

		_ = response.EnsureSuccessStatusCode();
		using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
		Assert.Equal(JobId, document.RootElement.GetProperty("jobHandle").GetString());
		Assert.Equal("tenant-a", document.RootElement.GetProperty("groupId").GetString());
	}
}
