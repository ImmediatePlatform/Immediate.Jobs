using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	private static readonly RedisValue[] RecurringMutableFields = ["record", "paused", "next", "last", "cancellationReason"];

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
				JsonSerializer.Serialize(schedule with { QueueName = null }, RedisJsonSerializerContext.Default.RecurringJobSchedule),
				schedule.IsCodeDefined ? 1 : 0,
				schedule.IsPaused ? 1 : 0,
				Timestamp(schedule.NextRunAt),
				NullableTimestamp(schedule.LastRunAt),
				schedule.Name.ToUpperInvariant(),
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
		if (result < 0)
			throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules.");
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
			[name.ToUpperInvariant(), _root],
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
		var arguments = new List<RedisValue> { _root, Score(now), batchSize };
		arguments.AddRange(jobNames.Select(static name => (RedisValue)name.ToUpperInvariant()));
		var result = await Database.ScriptEvaluateAsync(RedisScripts.GetDueRecurringForJobs, [RecurringDueKey], [.. arguments]).WaitAsync(cancellationToken);
		RedisValue[] values = [.. ((RedisResult[])result!).Select(static item => (RedisValue)(string)item!)];

		var members = values.Select(static value => (string)value!).ToList();
		var names = members.Select(static member => member[(member.IndexOf('|', StringComparison.Ordinal) + 1)..]).Distinct(StringComparer.Ordinal).ToList();
		var schedules = await ReadRecurringAsync(
			names,
			cancellationToken
		);
		var schedulesByName = schedules.ToDictionary(static schedule => schedule.Name, StringComparer.OrdinalIgnoreCase);
		var due = new List<RecurringJobSchedule>(schedules.Count);
		var added = new HashSet<string>(StringComparer.Ordinal);
		var stale = new List<RedisValue>();
		foreach (var member in members)
		{
			var name = member[(member.IndexOf('|', StringComparison.Ordinal) + 1)..];
			if (!schedulesByName.TryGetValue(name, out var schedule)
				|| !string.Equals(member, RecurringDueMember(schedule.NextRunAt, name), StringComparison.Ordinal))
			{
				stale.Add(member);
				continue;
			}

			if (schedule.IsPaused || schedule.NextRunAt > now
				|| !jobNames.Contains(schedule.JobName, StringComparer.OrdinalIgnoreCase))
				continue;

			if (added.Add(name))
				due.Add(schedule);
		}

		if (stale.Count != 0)
		{
			_ = await Database.SortedSetRemoveAsync(RecurringDueKey, [.. stale])
				.WaitAsync(cancellationToken);
		}

		return due;
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

	private async Task<IReadOnlyList<RecurringJobSchedule>> ReadAllRecurringAsync(CancellationToken cancellationToken)
	{
		var names = await Database.SetMembersAsync(RecurringNamesKey)
			.WaitAsync(cancellationToken);
		return await ReadRecurringAsync(
			[.. names.Select(static value => (string)value!)],
			cancellationToken
		);
	}

	private async Task<IReadOnlyList<RecurringJobSchedule>> ReadRecurringAsync(
		IReadOnlyList<string> names,
		CancellationToken cancellationToken
	)
	{
		var tasks = names.Select(name => ReadRecurringDataAsync(name, cancellationToken).AsTask()).ToList();
		var schedules = await Task.WhenAll(tasks).WaitAsync(cancellationToken);
		var records = schedules.OfType<RecurringJobSchedule>().ToList();
		var queues = await ReadDefinitionQueuesAsync(records.Select(static schedule => schedule.JobName), cancellationToken);
		return [.. records
			.Select(schedule => schedule with { QueueName = queues[schedule.JobName] })
			.OrderBy(schedule => schedule.NextRunAt)
			.ThenBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)];
	}

	private async ValueTask<RecurringJobSchedule?> ReadRecurringDataAsync(
		string name,
		CancellationToken cancellationToken
	)
	{
		var values = await Database.HashGetAsync(RecurringKey(name), RecurringMutableFields)
			.WaitAsync(cancellationToken);
		if (values[0].IsNull)
			return null;
		var schedule = JsonSerializer.Deserialize(
			(string)values[0]!,
			RedisJsonSerializerContext.Default.RecurringJobSchedule
		) ?? throw new ImmediateJobException($"Recurring schedule '{name}' contains invalid data.");
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
		schedule.Name.ToUpperInvariant(),
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

	private static string RecurringDueMember(DateTimeOffset nextRunAt, string name) => $"{Timestamp(nextRunAt)}|{name.ToUpperInvariant()}";

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
