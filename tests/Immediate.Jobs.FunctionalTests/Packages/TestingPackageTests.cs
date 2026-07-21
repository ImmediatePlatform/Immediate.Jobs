using System.Text.Json.Serialization;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.FunctionalTests.Packages;

#pragma warning disable CS1591
public sealed class TestingPackageTests
{
	[Fact]
	public async Task CaptureOnlySchedulerRecordsTypedCalls()
	{
		var scheduler = new CaptureOnlyJobScheduler<TestPayload>();
		var payload = new TestPayload("hello");

		var id = await scheduler.Enqueue(payload, TestContext.Current.CancellationToken);

		var capture = Assert.Single(scheduler.Captures);
		Assert.Equal(id, capture.Id);
		Assert.Equal(payload, capture.Payload);
	}

	[Fact]
	public async Task HarnessAdvancesTimeDrainsAndAssertsPayload()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var counter = new InvocationCounter();
		var definition = new JobDefinition
		{
			Name = "test-job",
			JobType = typeof(TestingPackageTests),
			Invoker = new TestInvoker(),
		};
		await using var harness = new JobTestHarness(services =>
		{
			services.AddSingleton(counter);
			services.AddSingleton(definition);
		});
		var scheduler = new TestScheduler(
			harness.Storage,
			harness.Services.GetRequiredService<IJobSerializer>(),
			harness.TimeProvider
		);

		var id = await scheduler.Schedule(new("payload"), TimeSpan.FromMinutes(5), cancellationToken);
		var queued = await harness.AssertEnqueuedAsync<TestPayload>(id, JobState.Scheduled, cancellationToken);
		Assert.Equal("payload", queued.Payload.Value);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromMinutes(5), cancellationToken);

		Assert.Equal(1, counter.Count);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(id, cancellationToken)).State);
	}

	public sealed record TestPayload(string Value);

	private sealed class InvocationCounter
	{
		public int Count { get; set; }
	}

	private sealed class TestScheduler(IJobStorage storage, IJobSerializer serializer, TimeProvider timeProvider)
		: JobScheduler<TestPayload>(
			storage,
			serializer,
			timeProvider,
			"test-job",
			JobQueueDefinition.DefaultName,
			static options => new TestingJsonContext(options).TestPayload
		);

	private sealed class TestInvoker : IJobInvoker
	{
		public ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution)
		{
			scopedServices.GetRequiredService<InvocationCounter>().Count++;
			return ValueTask.CompletedTask;
		}
	}
}

[JsonSerializable(typeof(TestingPackageTests.TestPayload))]
internal sealed partial class TestingJsonContext : JsonSerializerContext;
#pragma warning restore CS1591
