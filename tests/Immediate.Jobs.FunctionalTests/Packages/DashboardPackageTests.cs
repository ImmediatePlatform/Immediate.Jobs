using System.Globalization;
using System.Net;
using System.Text.Json;
using Immediate.Jobs.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Immediate.Jobs.FunctionalTests.Packages;

#pragma warning disable CS1591
public sealed class DashboardPackageTests
{
	[Fact]
	public void PackageEmbedsCompleteSpaAssetSet()
	{
		var resources = typeof(ImmediateJobsDashboardOptions).Assembly.GetManifestResourceNames();

		Assert.Contains("Immediate.Jobs.Dashboard.Assets.index.html", resources);
		Assert.Contains("Immediate.Jobs.Dashboard.Assets.app.css", resources);
		Assert.Contains("Immediate.Jobs.Dashboard.Assets.app.js", resources);
	}

	[Fact]
	public void AuthorizationPolicyRejectsBlankNames()
	{
		var options = new ImmediateJobsDashboardOptions();

		_ = Assert.Throws<ArgumentException>(() => options.RequireAuthorization(" "));
		_ = Assert.Throws<ArgumentException>(() => options.AddTelemetryLink(
			" ",
			JobTelemetryLinkKind.Trace,
			static _ => null
		));
	}

	[Fact]
	public async Task JobAndExecutionTelemetryApisSupplyTheExpectedCallbackContext()
	{
		const string JobId = "job:with retries";
		const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
		var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
		await using var storage = new InMemoryJobStorage(TimeProvider.System);
		await storage.EnqueueAsync(new()
		{
			Id = JobId,
			JobName = "SendGreeting",
			Payload = "{}",
			State = JobState.Succeeded,
			DueAt = now,
			CreatedAt = now,
			Attempt = 3,
			ExecutionTraceId = TraceId,
			ExecutionStartedAt = now.AddMinutes(2),
			CompletedAt = now.AddMinutes(3),
		}, TestContext.Current.CancellationToken);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard(options =>
		{
			_ = options.AddTelemetryLink(
				"View execution trace",
				JobTelemetryLinkKind.Trace,
				context => context.Execution?.ExecutionTraceId is { } traceId
					? new($"https://traces.example/trace/{traceId}")
					: null
			);
			_ = options.AddTelemetryLink(
				"View execution logs",
				JobTelemetryLinkKind.Logs,
				context => context.Execution is { } execution
					? new(string.Create(
						CultureInfo.InvariantCulture,
						$"https://logs.example/search?jobId={Uri.EscapeDataString(context.Job.Id)}&attempt={execution.Attempt}"
					))
					: null
			);
			_ = options.AddTelemetryLink(
				"View all retry logs",
				JobTelemetryLinkKind.Logs,
				context => context.Execution is null
					? new($"https://logs.example/search?jobId={Uri.EscapeDataString(context.Job.Id)}")
					: null
			);
		});
		_ = builder.Services.AddSingleton<IJobStorage>(storage);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var jobResponse = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(JobId)}/telemetry-links", UriKind.Relative),
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
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(JobId)}/executions/3/telemetry-links", UriKind.Relative),
			TestContext.Current.CancellationToken
		);
		_ = executionResponse.EnsureSuccessStatusCode();
		using var executionDocument = JsonDocument.Parse(
			await executionResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
		);
		var executionLinks = executionDocument.RootElement.EnumerateArray().ToArray();
		Assert.Equal(2, executionLinks.Length);
		Assert.Equal("View execution trace", executionLinks[0].GetProperty("label").GetString());
		Assert.Equal("Trace", executionLinks[0].GetProperty("kind").GetString());
		Assert.Equal($"https://traces.example/trace/{TraceId}", executionLinks[0].GetProperty("url").GetString());
		Assert.Equal("View execution logs", executionLinks[1].GetProperty("label").GetString());
		Assert.Contains("attempt=3", executionLinks[1].GetProperty("url").GetString(), StringComparison.Ordinal);

		using var pageResponse = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(JobId)}/executions?skip=0&take=1", UriKind.Relative),
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
		const string JobId = "job:legacy telemetry callback";
		const string FirstTraceId = "11111111111111111111111111111111";
		const string FirstSpanId = "1111111111111111";
		const string LatestTraceId = "22222222222222222222222222222222";
		const string LatestSpanId = "2222222222222222";
		var cancellationToken = TestContext.Current.CancellationToken;
		var now = TimeProvider.System.GetUtcNow();
		await using var storage = new InMemoryJobStorage(TimeProvider.System);
		await storage.EnqueueAsync(new()
		{
			Id = JobId,
			JobName = "SendGreeting",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
		}, cancellationToken);
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
		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		await storage.SetExecutionTelemetryAsync(
			JobId,
			first.Attempt,
			"worker",
			FirstTraceId,
			FirstSpanId,
			now,
			cancellationToken
		);
		await storage.FailAsync(JobId, first.Attempt, "worker", "first failure", now, cancellationToken);
		var latest = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		await storage.SetExecutionTelemetryAsync(
			JobId,
			latest.Attempt,
			"worker",
			LatestTraceId,
			LatestSpanId,
			now.AddSeconds(1),
			cancellationToken
		);
		await storage.CompleteAsync(JobId, latest.Attempt, "worker", cancellationToken);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(storage);
		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard(configure: options =>
		{
			_ = options.AddTelemetryLink(
				"Legacy trace callback",
				JobTelemetryLinkKind.Trace,
				context => context.Job.ExecutionTraceId is { } traceId
					? new($"https://traces.example/trace/{traceId}")
					: null
			);
			_ = options.AddTelemetryLink(
				"Legacy attempt callback",
				JobTelemetryLinkKind.Logs,
				context => new(string.Create(
					CultureInfo.InvariantCulture,
					$"https://logs.example/search?attempt={context.Job.Attempt}&span={context.Job.ExecutionSpanId}"
				))
			);
		});
		await app.StartAsync(cancellationToken);

		using var executionResponse = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(JobId)}/executions/1/telemetry-links", UriKind.Relative),
			cancellationToken
		);
		_ = executionResponse.EnsureSuccessStatusCode();
		using var executionDocument = JsonDocument.Parse(await executionResponse.Content.ReadAsStringAsync(cancellationToken));
		var executionLinks = executionDocument.RootElement.EnumerateArray().ToArray();
		Assert.Equal($"https://traces.example/trace/{FirstTraceId}", executionLinks[0].GetProperty("url").GetString());
		Assert.Contains("attempt=1", executionLinks[1].GetProperty("url").GetString(), StringComparison.Ordinal);
		Assert.Contains($"span={FirstSpanId}", executionLinks[1].GetProperty("url").GetString(), StringComparison.Ordinal);

		using var jobResponse = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(JobId)}/telemetry-links", UriKind.Relative),
			cancellationToken
		);
		_ = jobResponse.EnsureSuccessStatusCode();
		using var jobDocument = JsonDocument.Parse(await jobResponse.Content.ReadAsStringAsync(cancellationToken));
		var jobLinks = jobDocument.RootElement.EnumerateArray().ToArray();
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
		await using var storage = new InMemoryJobStorage(TimeProvider.System);
		await storage.EnqueueAsync(new()
		{
			Id = "job",
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
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(storage);
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
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(
			static _ => new StorageCapabilityTests.QueueOnlyStorage(TimeProvider.System)
		);

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
	[InlineData("DELETE", "/jobs/api/jobs/missing")]
	public async Task InMemoryDashboardReturnsNotFoundForMissingMutationTargets(string method, string path)
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(static _ => new InMemoryJobStorage(TimeProvider.System));

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var request = new HttpRequestMessage(new(method), path);
		using var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Theory]
	[InlineData("/jobs/")]
	[InlineData("/jobs/invocations")]
	[InlineData("/jobs/batches/batch-42/jobs/job-7")]
	public async Task SpaRoutesAreUnambiguous(string path)
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(static _ => new InMemoryJobStorage(TimeProvider.System));

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
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(static _ => new InMemoryJobStorage(TimeProvider.System));

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
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(static _ => new InMemoryJobStorage(TimeProvider.System));

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
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Development,
		});
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(static _ => new InMemoryJobStorage(TimeProvider.System));

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
		var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
		await using var storage = new InMemoryJobStorage(TimeProvider.System);
		await storage.EnqueueAsync(new()
		{
			Id = "86bf8c31-d8e6-415b-8e92-45587a09fc52",
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
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(storage);

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
		var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
		await using var storage = new InMemoryJobStorage(TimeProvider.System);
		await storage.EnqueueAsync(new()
		{
			Id = JobId,
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
		_ = builder.WebHost.UseTestServer();
		_ = builder.Services.AddImmediateJobsDashboard();
		_ = builder.Services.AddSingleton<IJobStorage>(storage);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard();
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var response = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{JobId}", UriKind.Relative),
			TestContext.Current.CancellationToken
		);

		_ = response.EnsureSuccessStatusCode();
		using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
		Assert.Equal(JobId, document.RootElement.GetProperty("id").GetString());
		Assert.Equal("tenant-a", document.RootElement.GetProperty("groupId").GetString());
	}
}
#pragma warning restore CS1591
