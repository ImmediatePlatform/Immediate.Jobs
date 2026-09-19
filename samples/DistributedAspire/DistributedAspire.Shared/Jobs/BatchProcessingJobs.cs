using Immediate.Handlers.Shared;
using Immediate.Jobs.DistributedAspire.Shared.Data;
using Immediate.Jobs.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.DistributedAspire.Shared.Jobs;

[Handler, Job(Name = "prepare-batches", MaxAttempts = 1, MaxConcurrency = 1, OverlapPolicy = OverlapPolicy.Queue)]
public sealed partial class PrepareBatchesJob(
	ILogger<PrepareBatchesJob> logger,
	TimeProvider timeProvider,
	JobsDbContext dbContext,
	EnqueueBatchesJob.Scheduler enqueueBatches
)
{
	public sealed class Payload : IJobRequest
	{
		/// <summary>
		/// amount of data to generate and process
		/// </summary>
		public required int TotalAmount { get; init; }

		/// <summary>
		/// how many rows to process in a single batch
		/// </summary>
		public required int BatchSize { get; init; }

		public JobDetails? JobDetails { get; set; }
	}

	private async ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		logger.LogInformation(
			"PrepareBatchesJob {JobId} fired at {FiredAt}",
			payload.JobDetails?.JobHandle.Value,
			timeProvider.GetUtcNow()
		);

		if (!await dbContext.BatchableRows.AnyAsync(cancellationToken))
		{
			await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken: cancellationToken);

			var now = timeProvider.GetUtcNow();
			var enumeration = Enumerable
				.Range(0, payload.TotalAmount)
				.Select(i => new BatchEntity()
				{
					Data = Guid.NewGuid(),
					CreatedOn = now,
					ModifiedOn = null,
				})
				.Chunk(payload.BatchSize);

			foreach (var chunk in enumeration)
			{
				dbContext.BatchableRows.AddRange(chunk);

				_ = await dbContext.SaveChangesAsync(cancellationToken);

				dbContext.ChangeTracker.Clear();
			}

			await transaction.CommitAsync(cancellationToken);
		}

		_ = await enqueueBatches.EnqueueAsync(
			new EnqueueBatchesJob.Payload
			{
				BatchSize = payload.BatchSize,
			},
			cancellationToken
		);
	}
}

[Handler, Job(Name = "enqueue-batches", MaxAttempts = 1, MaxConcurrency = 1, OverlapPolicy = OverlapPolicy.Queue)]
public sealed partial class EnqueueBatchesJob(
	ILogger<EnqueueBatchesJob> logger,
	BatchScheduler batches,
	TimeProvider timeProvider,
	JobsDbContext dbContext,
	ProcessBatchJob.Scheduler processBatch,
	CleanupBatchesJob.Scheduler cleanupBatches)
{
	public sealed class Payload : IJobRequest
	{
		public required int BatchSize { get; init; }
		public JobDetails? JobDetails { get; set; }
	}

	private async ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		logger.LogInformation(
			"EnqueueBatchesJob {JobId} fired at {FiredAt}",
			payload.JobDetails?.JobHandle.Value,
			timeProvider.GetUtcNow()
		);

		await using var batch = batches.Begin();

		var batchHandles = new List<BatchJobHandle>();

		var currentId = 0;
		var continueSeek = true;

		while (continueSeek)
		{
			var nextId = await dbContext.BatchableRows
				.AsNoTracking()
				.Where(p => p.Id >= currentId)
				.OrderBy(p => p.Id)
				.Skip(payload.BatchSize)
				.Select(p => new { p.Id })
				.FirstOrDefaultAsync(cancellationToken: cancellationToken);

			if (nextId is null)
			{
				if (await dbContext.BatchableRows
					.AsNoTracking()
					.Where(p => p.Id >= currentId)
					.OrderBy(p => p.Id)
					.AnyAsync(cancellationToken: cancellationToken))
				{
					var nextIdValue = await dbContext.BatchableRows
						.AsNoTracking()
						.Where(p => p.Id >= currentId)
						.OrderBy(p => p.Id)
						.MaxAsync(p => p.Id, cancellationToken: cancellationToken);

					logger.LogInformation(
						"Enqueueing from {LowerBound} to {UpperBound}",
						currentId,
						nextIdValue
					);

					batchHandles.Add(processBatch.Enqueue(new ProcessBatchJob.Payload
					{
						LowerBound = currentId,
						UpperBound = nextIdValue,
					}, batch));

					currentId = nextIdValue;
				}

				continueSeek = false;
			}
			else
			{
				logger.LogInformation(
					"Enqueueing from {LowerBound} to {UpperBound}",
					currentId,
					nextId.Id
				);

				batchHandles.Add(processBatch.Enqueue(new ProcessBatchJob.Payload
				{
					LowerBound = currentId,
					UpperBound = nextId.Id,
				}, batch));

				currentId = nextId.Id;
			}
		}

		_ = cleanupBatches.ScheduleAfter(new(), batchHandles);

		_ = await batch.CommitAsync(cancellationToken);
	}
}

[Handler, Job(Name = "process-batch", MaxAttempts = 5, MaxConcurrency = 24, OverlapPolicy = OverlapPolicy.Concurrent)]
public sealed partial class ProcessBatchJob(
	ILogger<ProcessBatchJob> logger,
	TimeProvider timeProvider,
	JobsDbContext dbContext
)
{
	public sealed class Payload : IJobRequest
	{
		public required int LowerBound { get; init; }
		public required int UpperBound { get; set; }
		public JobDetails? JobDetails { get; set; }
	}

	private async ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		logger.LogInformation(
			"ProcessBatchJob {JobId} fired at {FiredAt}",
			payload.JobDetails?.JobHandle.Value,
			timeProvider.GetUtcNow()
		);

		await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken: cancellationToken);

		var batchableRows = await dbContext.BatchableRows
			.AsTracking()
			.Where(p => p.Id >= payload.LowerBound && p.Id < payload.UpperBound)
			.ToListAsync(cancellationToken: cancellationToken);

		logger.LogInformation("Processing from: {LowerBound} - to: {UpperBound} (Total: {Total})", batchableRows[0].Id, batchableRows[^1].Id, batchableRows.Count);

		foreach (var row in batchableRows)
		{
			row.Data = Guid.NewGuid();
		}

		await dbContext.SaveChangesAsync(cancellationToken);

		await transaction.CommitAsync(cancellationToken);

		logger.LogInformation("Processed: from: {LowerBound} - to: {UpperBound} (Total: {Total})", batchableRows[0].Id, batchableRows[^1].Id, batchableRows.Count);
	}
}

[Handler, Job(Name = "cleanup-batches", MaxAttempts = 1, MaxConcurrency = 1, OverlapPolicy = OverlapPolicy.Queue)]
public sealed partial class CleanupBatchesJob(
	ILogger<CleanupBatchesJob> logger,
	TimeProvider timeProvider,
	JobsDbContext dbContext
)
{
	private async ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		logger.LogInformation(
			"CleanupBatchesJob {JobId} fired at {FiredAt}",
			payload.JobDetails?.JobHandle.Value,
			timeProvider.GetUtcNow()
		);

		_ = await dbContext.BatchableRows.ExecuteDeleteAsync(cancellationToken: cancellationToken);
	}
}
