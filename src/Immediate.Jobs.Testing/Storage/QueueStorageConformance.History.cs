using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static partial class QueueStorageConformance
{
	private const string LegacyHistoryName = "Queue.Executions.QueriesSyntheticLegacyHistory";

	private static PersistedJobState LegacyHistoryState() => new()
	{
		Jobs = [new()
		{
			JobHandle = JobHandle.FromString("legacy-history"), JobName = "legacy-history", Payload = "{}",
			State = JobState.Failed, Attempt = 3, LastError = "legacy error", CompletedAt = DateTimeOffset.UnixEpoch,
			CreatedAt = DateTimeOffset.UnixEpoch, DueAt = DateTimeOffset.UnixEpoch,
		}],
		Batches = [],
		Edges = [],
		RecurringSchedules = [],
	};

	private static async ValueTask QueriesLegacyHistoryAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var handle = JobHandle.FromString("legacy-history");
		var execution = (await storage.QueryJobExecutionsAsync(handle, new() { Attempt = 3 }, token)).Single();
		ConformanceAssert.True(execution.IsSynthetic, LegacyHistoryName, "legacy jobs synthesize their latest execution");
		ConformanceAssert.Equal(JobExecutionState.Failed, execution.State, LegacyHistoryName, "synthetic history preserves state");
		ConformanceAssert.Equal("legacy error", execution.Error, LegacyHistoryName, "synthetic history preserves the failure");
		ConformanceAssert.Equal(0, (await storage.QueryJobExecutionsAsync(handle, new() { Skip = 1 }, token)).Count,
			LegacyHistoryName, "paging beyond the synthetic execution returns no history");
		ConformanceAssert.Equal(0, (await storage.QueryJobExecutionsAsync(handle, new() { Attempt = 2 }, token)).Count,
			LegacyHistoryName, "synthetic history must respect attempt filters");
		ConformanceAssert.Equal(0, (await storage.QueryJobExecutionsAsync(JobHandle.FromString("missing-history"), new(), token)).Count,
			LegacyHistoryName, "missing jobs have no history");
		ConformanceAssert.Equal(execution, (await storage.QueryJobExecutionsAsync(handle, new(), token)).Single(),
			LegacyHistoryName, "reading history preserves the same legacy projection");
	}
}
