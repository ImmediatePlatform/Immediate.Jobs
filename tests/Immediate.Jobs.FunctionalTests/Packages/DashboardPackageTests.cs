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
	}

	[Theory]
	[InlineData("/jobs/")]
	[InlineData("/jobs/succeeded")]
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

		using var response = await app.GetTestClient().GetAsync(new Uri("/jobs", UriKind.Relative), TestContext.Current.CancellationToken);

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
