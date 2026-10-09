using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests;

public sealed class JobDefinitionTests
{
	[Fact]
	public async Task DisabledWorkersStillRegisterMetadata()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<TimeProvider>(clock);
		services.AddSingleton(Definition("metadata-only", new RecordingInvoker()));
		services.AddImmediateJobsCore().DisableWorkers().ConfigureStorage(builder => builder.UseStorage<StartupStorage>().UseDistributed());
		await using var provider = services.BuildServiceProvider();
		var scheduler = provider.GetRequiredService<JobSchedulingService>();
		await scheduler.StartAsync(token);
		var storage = provider.GetRequiredService<StartupStorage>();
		Assert.True(storage.Registered.Task.IsCompletedSuccessfully);
		await storage.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
		Assert.Equal("metadata-only", Assert.Single(await storage.GetJobDefinitionsAsync(token)).Name);
		await scheduler.StopAsync(token);
	}

	[Fact]
	public async Task CaseVariantInvocationsUseTheRegisteredHandlerAndStoredDefinition()
	{
		var token = TestContext.Current.CancellationToken;
		var invoker = new RecordingInvoker();
		await using var harness = new JobTestHarness(services => services.AddSingleton(Definition("Invoice", invoker)));
		await harness.DrainAsync(token);
		var now = harness.TimeProvider.GetUtcNow();
		var handle = JobHandle.FromString("case-variant");
		await harness.Storage.EnqueueAsync(new()
		{
			JobHandle = handle,
			JobName = "invoice",
			Payload = "{}",
			State = JobState.Pending,
			CreatedAt = now,
			DueAt = now,
		}, token);
		await harness.DrainAsync(token);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(handle, token)).State);
		Assert.Equal(["invoice"], invoker.Names);
	}

	private static JobDefinition Definition(string name, IJobInvoker invoker) => new()
	{
		Name = name,
		Invoker = invoker,
		JobType = typeof(RecordingInvoker),
	};

	private sealed class RecordingInvoker : IJobInvoker
	{
		public List<string> Names { get; } = [];
		public ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution)
		{
			Names.Add(execution.Record.JobName);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class StartupStorage(TimeProvider clock) : CapturingJobStorage(clock)
	{
		public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<JobServerSnapshot> Heartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
		{
			await base.MergeJobDefinitionsListAsync(registration, cancellationToken);
			Registered.TrySetResult();
		}
		public override async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
		{
			await base.HeartbeatAsync(server, cancellationToken);
			Heartbeat.TrySetResult(server);
		}
	}
}
