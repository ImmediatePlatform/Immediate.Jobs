using Immediate.Jobs.DistributedAspire.Shared.Jobs;

namespace Immediate.Jobs.DistributedAspire.Shared.Workflows;

public sealed class DistributedBatchWorkflow(PrepareBatchesJob.Scheduler prepareBatches)
{
	public async ValueTask StartAsync(int batchSize, int totalAmount, CancellationToken cancellationToken = default)
	{
		_ = await prepareBatches.EnqueueAsync(new PrepareBatchesJob.Payload()
		{
			BatchSize = batchSize,
			TotalAmount = totalAmount,
		}, cancellationToken);
	}
}
