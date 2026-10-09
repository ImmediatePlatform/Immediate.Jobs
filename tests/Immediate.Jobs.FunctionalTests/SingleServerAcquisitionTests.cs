using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests;

public sealed class SingleServerAcquisitionTests
{
	[Fact]
	public async Task AcquisitionWaitsUntilEnqueueHasUpdatedBothStores()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		using var durable = new DelayedEnqueueStorage(clock);
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<TimeProvider>(clock);
		_ = services.AddImmediateJobsCore().ConfigureStorage(options => _ = options.UseSingleServer(_ => durable));
		await using var provider = services.BuildServiceProvider();
		var storage = provider.GetRequiredService<IJobStorage>();
		await storage.InitializeAsync(token);
		var job = new JobRecord
		{
			JobHandle = JobHandle.FromString("enqueue-race"),
			JobName = "enqueue-race",
			Payload = "{}",
			State = JobState.Pending,
			CreatedAt = clock.GetUtcNow(),
			DueAt = clock.GetUtcNow(),
		};
		var enqueue = storage.EnqueueAsync(job, token).AsTask();
		await durable.EnqueueCommitted.Task.WaitAsync(token);
		var acquire = storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "worker",
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = 1,
			Queues = [new() { QueueName = job.QueueName, Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [job.JobName] = 1 } }],
		}, token).AsTask();

		try
		{
			await Assert.ThrowsAsync<TimeoutException>(() => durable.AcquisitionEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(100), token));
		}
		finally
		{
			durable.ReleaseEnqueue.SetResult();
		}

		await enqueue;
		var acquired = Assert.Single(await acquire);
		Assert.Equal(job.JobHandle, acquired.JobHandle);
		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(job.JobHandle, token))!.State);
		await storage.CompleteAsync(job.JobHandle, acquired.Attempt, "worker", token);
		Assert.Equal(JobState.Succeeded, (await storage.GetJobStatusAsync(job.JobHandle, token))!.State);
	}

	private sealed class DelayedEnqueueStorage(TimeProvider clock) : CapturingJobStorage(clock)
	{
		public TaskCompletionSource EnqueueCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseEnqueue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AcquisitionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
		{
			await base.EnqueueAsync(job, cancellationToken);
			EnqueueCommitted.SetResult();
			await ReleaseEnqueue.Task.WaitAsync(cancellationToken);
		}

		public override ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(JobAcquisitionRequest request, CancellationToken cancellationToken = default)
		{
			AcquisitionEntered.SetResult();
			return base.AcquireDueJobsAsync(request, cancellationToken);
		}
	}
}
