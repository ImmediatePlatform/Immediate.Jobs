using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(
		RecurringJobSchedule schedule,
		CancellationToken cancellationToken = default
	)
	{
		UpsertRecurringAsyncCalled(schedule.Name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var result = await EvaluateInt64Async(
			RedisScripts.UpsertRecurring,
			[RecurringKey(schedule.Name), RecurringNamesKey, RecurringDueKey, DefinitionMetadataKey],
			[
				JsonSerializer.Serialize(schedule, RedisJsonSerializerContext.Default.RecurringJobSchedule),
				schedule.IsCodeDefined ? 1 : 0,
				schedule.IsPaused ? 1 : 0,
				Timestamp(schedule.NextRunAt),
				NullableTimestamp(schedule.LastRunAt),
				NormalizeOrdinalIgnoreCase(schedule.Name),
				Score(schedule.NextRunAt),
				RecurringDueMember(schedule.NextRunAt, schedule.Name),
				schedule.Cron,
				schedule.TimeZone,
				schedule.JobName.ToUpperInvariant(),
				_root,
			],
			cancellationToken
		);
		if (result == -2)
			throw new KeyNotFoundException($"Job definition '{schedule.JobName}' was not found.");
		if (result == -3)
			throw new ImmediateJobException("Code-defined recurring schedules must be created by definition reconciliation.");
		if (result < 0)
			throw new ImmediateJobException("Recurring schedule ownership cannot be changed by upsert.");
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		RemoveRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var result = await EvaluateInt64Async(
			RedisScripts.RemoveRecurring,
			[RecurringKey(name), RecurringNamesKey, RecurringDueKey],
			[NormalizeOrdinalIgnoreCase(name), _root],
			cancellationToken
		);
		if (result == 0)
			throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
		if (result < 0)
			throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		PauseRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await SetRecurringPausedAsync(name, isPaused: true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ResumeRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await SetRecurringPausedAsync(name, isPaused: false, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		IReadOnlyList<string> jobNames,
		CancellationToken cancellationToken = default
	)
	{
		GetDueRecurringAsyncCalled(batchSize);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		if (jobNames.Count == 0)
			return [];
		var arguments = new List<RedisValue> { _root, Score(now), batchSize, Timestamp(now) };
		arguments.AddRange(jobNames.Select(static name => (RedisValue)name.ToUpperInvariant()));
		var result = await Database.ScriptEvaluateAsync(RedisScripts.GetDueRecurringForJobs, [RecurringDueKey], [.. arguments]).WaitAsync(cancellationToken);
		return [.. ReadRecurringRecords(result).Where(schedule =>
			jobNames.Contains(schedule.JobName, StringComparer.OrdinalIgnoreCase))];
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		IReadOnlyList<JobContinuationEdge>? dependencies = null,
		CancellationToken cancellationToken = default
	)
	{
		MaterializeRecurringAsyncCalled(job.JobHandle, schedule.Name);

		if (dependencies != null)
		{
			MaterializeRecurringAsyncCalledWithDependencies(job.JobHandle, schedule.Name);
			throw new ImmediateJobException("Unable to process recurring jobs with dependencies.");
		}

		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		ValidateMaterializedJob(job);
		var jobArguments = CreateMaterializeArguments(schedule, job, nextRunAt, _timeProvider.GetUtcNow());
		var result = await EvaluateInt64Async(
			RedisScripts.MaterializeRecurring,
			[
				RecurringKey(schedule.Name),
				RecurringDedupeKey,
				JobKey(job.JobHandle),
				AllJobsKey,
				StateKey(job.State),
				DueKey(job.JobName),
				RecurringDueKey,
				CompletedKey(job.State),
			],
			jobArguments,
			cancellationToken
		);
		if (result < 0)
			throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
		return result == 1;
	}

	private async ValueTask SetRecurringPausedAsync(
		string name,
		bool isPaused,
		CancellationToken cancellationToken
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		var result = await EvaluateInt64Async(
			RedisScripts.SetRecurringPaused,
			[RecurringKey(name), RecurringDueKey],
			[isPaused ? 1 : 0, _root],
			cancellationToken
		);
		if (result == -1)
			throw new ImmediateJobException("Canceled recurring schedules cannot be resumed.");
		if (result == 0)
			throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
	}

	private static IReadOnlyList<RecurringJobSchedule> ReadRecurringRecords(RedisResult result) =>
		[.. ((RedisResult[])result!).Select(static item => ToRecurringRecord(ReadValues(item)))
			.OrderBy(static schedule => schedule.NextRunAt)
			.ThenBy(static schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)];

	private static RecurringJobSchedule ToRecurringRecord(RedisValue[] values)
	{
		var schedule = JsonSerializer.Deserialize(
			(byte[])values[0]!, RedisJsonSerializerContext.Default.RecurringJobSchedule
		) ?? throw new ImmediateJobException("Recurring schedule contains invalid data.");
		return schedule with
		{
			IsPaused = values[1] == "1",
			CancellationReason = values[4].IsNull ? schedule.CancellationReason : NullIfEmpty(values[4]),
			NextRunAt = FromTimestamp(values[2]),
			LastRunAt = FromNullableTimestamp(values[3]),
		};
	}

	private RedisValue[] CreateMaterializeArguments(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		DateTimeOffset now
	) =>
	[
		Timestamp(schedule.NextRunAt),
		job.RecurringKey ?? "",
		job.JobHandle.Value,
		JsonSerializer.Serialize(job with { QueueName = null }, RedisJsonSerializerContext.Default.JobRecord),
		(int)job.State,
		Timestamp(job.DueAt),
		Score(job.DueAt),
		job.Attempt,
		job.WorkerId ?? "",
		NullableTimestamp(job.LeaseExpiresAt),
		job.LastError ?? "",
		NullableTimestamp(job.CompletedAt),
		job.ExecutionTraceId ?? "",
		job.ExecutionSpanId ?? "",
		NullableTimestamp(job.ExecutionStartedAt),
		job.JobName.ToUpperInvariant(),
		Score(job.CreatedAt),
		Timestamp(nextRunAt),
		Score(nextRunAt),
		NormalizeOrdinalIgnoreCase(schedule.Name),
		Timestamp(job.CreatedAt),
		job.CompletedAt is { } completedAt ? Score(completedAt) : 0,
		Score(now),
		_root,
	];

	private static void ValidateMaterializedJob(JobRecord job)
	{
		if (job.BatchHandle is not null || job.RemainingDependencies != 0 || job.FailedDependencies != 0)
		{
			throw new NotSupportedException(
				"Batches & continuations require a graph-capable storage provider (a SQL database)."
			);
		}

		if (job.State is not (JobState.Pending or JobState.Scheduled or JobState.Cancelled or JobState.Skipped))
			throw new ImmediateJobException($"Recurring job '{job.JobHandle}' has invalid state '{job.State}'.");
		if (job.CompletedAt is null && (job.State == JobState.Cancelled || job.State == JobState.Skipped))
			throw new ImmediateJobException($"Terminal recurring job '{job.JobHandle}' must have a completion time.");
	}

	private static string RecurringDueMember(DateTimeOffset nextRunAt, string name) => $"{Timestamp(nextRunAt)}|{NormalizeOrdinalIgnoreCase(name)}";

	[LoggerMessage(
		EventId = LibraryEventIds.UpsertRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Redis.UpsertRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpsertRecurringAsync called (Schedule={Schedule})"
	)]
	private partial void UpsertRecurringAsyncCalled(string schedule);

	[LoggerMessage(
		EventId = LibraryEventIds.RemoveRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Redis.RemoveRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RemoveRecurringAsync called (Name={Name})"
	)]
	private partial void RemoveRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.PauseRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Redis.PauseRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseRecurringAsync called (Name={Name})"
	)]
	private partial void PauseRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.ResumeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Redis.ResumeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeRecurringAsync called (Name={Name})"
	)]
	private partial void ResumeRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.GetDueRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Redis.GetDueRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetDueRecurringAsync called (BatchSize={BatchSize})"
	)]
	private partial void GetDueRecurringAsyncCalled(int batchSize);

	[LoggerMessage(
		EventId = LibraryEventIds.MaterializeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Redis.MaterializeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MaterializeRecurringAsync called (JobHandle={JobHandle}, Schedule={Schedule})"
	)]
	private partial void MaterializeRecurringAsyncCalled(JobHandle jobHandle, string schedule);

	[LoggerMessage(
		EventId = LibraryEventIds.MaterializeRecurringAsyncCalledWithDependencies,
		EventName = "Immediate.Jobs.Redis.MaterializeRecurringAsyncCalledWithDependencies",
		Level = LogLevel.Warning,
		Message = "MaterializeRecurringAsync invalidly called with dependencies (JobHandle={JobHandle}, Schedule={Schedule})"
	)]
	private partial void MaterializeRecurringAsyncCalledWithDependencies(JobHandle jobHandle, string schedule);
}
