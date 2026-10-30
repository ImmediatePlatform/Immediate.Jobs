using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static partial class GraphStorageConformance
{
	private const string PagingName = "Graph.Batches.PagesTiedTimestampsWithFilters";

	private static PersistedJobState PagingJobState()
	{
		var now = new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
		var batches = Enumerable.Range(0, 12).Select(index => new BatchRecord
		{
			BatchHandle = BatchHandle.FromString(string.Create(provider: null, $"page-batch-{index:D2}")),
			CreatedAt = now.AddMinutes(index % 3),
			State = index % 2 == 0 ? BatchState.Executing : BatchState.Succeeded,
			TotalJobs = 4,
			PendingCount = index % 2 == 0 ? 2 : 0,
			SucceededCount = index % 2 == 0 ? 2 : 4,
		}).ToList();
		var jobs = Enumerable.Range(0, 48).Select(index => CreateJob(string.Create(provider: null, $"page-job-{index:D2}"), batches[index / 4].BatchHandle) with
		{
			JobName = "paging",
			CreatedAt = now.AddMinutes(index % 3),
			State = (index / 4) % 2 == 0 && index % 2 == 0 ? JobState.Pending : JobState.Succeeded,
		}).Select(static job => job.ToJobRecord("paging")).ToList();
		return new() { Jobs = jobs, Batches = batches, Edges = [], RecurringSchedules = [] };
	}

	private static async ValueTask PagesTiedTimestampsAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = "paging", QueueName = "priority" }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		var seed = PagingJobState();
		foreach (var state in new JobState?[] { null, JobState.Pending })
		{
			var page = await storage.QueryJobsAsync(new() { State = state, JobName = "paging", QueueName = "priority", Skip = 3, Take = 7 }, token);
			ConformanceAssert.SequenceEqual(seed.Jobs.Where(job => state == null || job.State == state)
				.OrderByDescending(static job => job.CreatedAt).ThenBy(static job => job.JobHandle.Value, StringComparer.Ordinal)
				.Skip(3).Take(7).Select(static job => job.JobHandle), page.Select(static job => job.JobHandle), PagingName,
				"job pages apply filters before skip/take and break timestamp ties by handle");
		}

		ConformanceAssert.Equal(0, (await storage.QueryJobsAsync(new() { QueueName = "missing", Take = 1 }, token)).Count,
			PagingName, "queue filters must be applied before key paging");
		var graph = GetGraph(storage, PagingName);
		foreach (var state in new BatchState?[] { null, BatchState.Executing })
		{
			var headers = await graph.QueryBatchesAsync(new() { State = state, Skip = 2, Take = 3 }, token);
			ConformanceAssert.SequenceEqual(seed.Batches.Where(batch => state == null || batch.State == state)
				.OrderByDescending(static batch => batch.CreatedAt).ThenBy(static batch => batch.BatchHandle.Value, StringComparer.Ordinal)
				.Skip(2).Take(3).Select(static batch => batch.BatchHandle), headers.Select(static batch => batch.BatchHandle), PagingName,
				"batch pages preserve state filters and deterministic ordering");
		}

		var batchHandle = seed.Batches[0].BatchHandle;
		foreach (var state in new JobState?[] { null, JobState.Pending })
		{
			var members = await graph.QueryBatchMembersAsync(batchHandle, new() { State = state, Skip = 1, Take = 2 }, token);
			ConformanceAssert.SequenceEqual(seed.Jobs.Where(job => job.BatchHandle == batchHandle && (state == null || job.State == state))
				.OrderBy(static job => job.CreatedAt).ThenBy(static job => job.JobHandle.Value, StringComparer.Ordinal)
				.Skip(1).Take(2).Select(static job => job.JobHandle), members.Select(static job => job.JobHandle), PagingName,
				"member pages sort ascending and preserve batch and state filters");
			ConformanceAssert.True(members.All(static member => string.Equals(member.QueueName, "priority", StringComparison.Ordinal)), PagingName,
				"member projections retain resolved definition queues");
		}
	}
}
