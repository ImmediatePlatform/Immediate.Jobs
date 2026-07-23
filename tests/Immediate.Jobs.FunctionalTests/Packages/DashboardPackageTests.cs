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
	public async Task JobTelemetryApiBuildsConfiguredExternalLinks()
	{
		const string JobId = "job:with retries";
		const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
		var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
		var storage = new InMemoryJobStorage(TimeProvider.System);
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
		_ = builder.Services.AddSingleton<IJobStorage>(storage);

		await using var app = builder.Build();
		_ = app.MapImmediateJobsDashboard(configure: options =>
		{
			_ = options.AddTelemetryLink(
				"View latest trace",
				JobTelemetryLinkKind.Trace,
				context => context.Job.ExecutionTraceId is { } traceId
					? new($"https://traces.example/trace/{traceId}")
					: null
			);
			_ = options.AddTelemetryLink(
				"View all retry logs",
				JobTelemetryLinkKind.Logs,
				context => new($"https://logs.example/search?jobId={Uri.EscapeDataString(context.Job.Id)}")
			);
		});
		await app.StartAsync(TestContext.Current.CancellationToken);

		using var response = await app.GetTestClient().GetAsync(
			new Uri($"/jobs/api/jobs/{Uri.EscapeDataString(JobId)}/telemetry-links", UriKind.Relative),
			TestContext.Current.CancellationToken
		);

		_ = response.EnsureSuccessStatusCode();
		using var document = JsonDocument.Parse(
			await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
		);
		var links = document.RootElement.EnumerateArray().ToArray();
		Assert.Equal(2, links.Length);
		Assert.Equal("View latest trace", links[0].GetProperty("label").GetString());
		Assert.Equal("Trace", links[0].GetProperty("kind").GetString());
		Assert.Equal($"https://traces.example/trace/{TraceId}", links[0].GetProperty("url").GetString());
		Assert.Equal("View all retry logs", links[1].GetProperty("label").GetString());
		Assert.Contains("job%3Awith%20retries", links[1].GetProperty("url").GetString(), StringComparison.Ordinal);
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
		_ = builder.Services.AddSingleton<IJobStorage>(new InMemoryJobStorage(TimeProvider.System));

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
		_ = builder.Services.AddSingleton<IJobStorage>(new InMemoryJobStorage(TimeProvider.System));

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
		_ = builder.Services.AddSingleton<IJobStorage>(new InMemoryJobStorage(TimeProvider.System));

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
		_ = builder.Services.AddSingleton<IJobStorage>(new InMemoryJobStorage(TimeProvider.System));

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
		var storage = new InMemoryJobStorage(TimeProvider.System);
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
		var storage = new InMemoryJobStorage(TimeProvider.System);
		await storage.EnqueueAsync(new()
		{
			Id = JobId,
			JobName = "SendGreeting",
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
	}
}
#pragma warning restore CS1591
