using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static partial class QueueStorageConformance
{
	private const string NonCompletedName = "Queue.Queries.ReturnsAllNonCompletedJobsForRequestedDefinition";
	private const string OpenJobName = "Open.Jobs";

	private static PersistedJobState NonCompletedState()
	{
		var now = DateTimeOffset.UnixEpoch;
		var open = Enumerable.Range(0, 1001).Select(index => new JobRecord
		{
			JobHandle = JobHandle.FromString(string.Create(CultureInfo.InvariantCulture, $"open-{index:D4}")),
			JobName = OpenJobName,
			QueueName = "priority",
			Payload = "{}",
			Context = "context: 雪🙂",
			State = (index % 3) switch
			{
				0 => JobState.Pending,
				1 => JobState.Scheduled,
				_ => JobState.WaitingForTrigger,
			},
			CreatedAt = now,
			DueAt = now.AddHours(1),
		});
		var unrelated = Enumerable.Range(0, 301).Select(index => new JobRecord
		{
			JobHandle = JobHandle.FromString(string.Create(CultureInfo.InvariantCulture, $"other-{index:D4}")),
			JobName = "Other.Jobs",
			QueueName = "ordinary",
			Payload = "{}",
			State = JobState.Pending,
			CreatedAt = now,
			DueAt = now,
		});
		var terminal = new[] { JobState.Succeeded, JobState.Failed, JobState.Cancelled }.Select(state => new JobRecord
		{
			JobHandle = JobHandle.FromString("terminal-" + state),
			JobName = OpenJobName,
			QueueName = "priority",
			Payload = "{}",
			State = state,
			CreatedAt = now,
			DueAt = now,
			CompletedAt = now,
		});
		return new() { Jobs = [.. open, .. unrelated, .. terminal], Batches = [], Edges = [], RecurringSchedules = [] };
	}

	private static async ValueTask QueriesAllNonCompletedAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = OpenJobName, QueueName = "priority" }, new() { Name = "Other.Jobs", QueueName = "ordinary" }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		var jobs = await storage.QueryNonCompletedJobsAsync("open.jobs", token);
		ConformanceAssert.SequenceEqual(
			Enumerable.Range(0, 1001).Select(static index => string.Create(CultureInfo.InvariantCulture, $"open-{index:D4}")),
			jobs.Select(static job => job.JobHandle.Value).Order(StringComparer.Ordinal),
			NonCompletedName, "the query returns every matching open job exactly once without a result cap");
		ConformanceAssert.True(jobs.All(static job =>
			string.Equals(job.JobName, OpenJobName, StringComparison.Ordinal)
			&& string.Equals(job.QueueName, "priority", StringComparison.Ordinal)
			&& string.Equals(job.Context, "context: 雪🙂", StringComparison.Ordinal)),
			NonCompletedName, "large queries preserve the canonical name, resolved queue, and complete job projection");
		ConformanceAssert.True(jobs.All(static job => job.State is JobState.Pending or JobState.Scheduled or JobState.WaitingForTrigger),
			NonCompletedName, "large queries exclude terminal jobs");
		ConformanceAssert.Equal(0, (await storage.QueryNonCompletedJobsAsync("Missing.Jobs", token)).Count,
			NonCompletedName, "unrelated backlog does not produce matches for an unknown definition");
		await ConformanceAssert.ThrowsAsync<OperationCanceledException>(
			async () => _ = await storage.QueryNonCompletedJobsAsync(OpenJobName, new CancellationToken(canceled: true)),
			NonCompletedName, "the query observes pre-cancelled operations");
	}
}
