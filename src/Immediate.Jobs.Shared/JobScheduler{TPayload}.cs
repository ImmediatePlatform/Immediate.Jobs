using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;
using Cronos;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Runtime base used by source-generated typed schedulers.
/// </summary>
/// <typeparam name="TPayload">
/// 	The job payload type.
/// </typeparam>
/// <param name="storage">
/// 	The storage provider used to persist invocations and schedules.
/// </param>
/// <param name="serializer">
/// 	The serializer used for payload and context data.
/// </param>
/// <param name="timeProvider">
/// 	The clock used to determine due times.
/// </param>
/// <param name="idGenerator">
/// 	The generator used to create invocation identifiers.
/// </param>
/// <param name="jobName">
/// 	The stable generated job name.
/// </param>
/// <param name="queueName">
/// 	The queue used for new invocations.
/// </param>
/// <param name="payloadTypeInfoFactory">
/// 	The factory that supplies JSON metadata for the payload type.
/// </param>
public abstract class JobScheduler<TPayload>(
	IJobStorage storage,
	IJobSerializer serializer,
	TimeProvider timeProvider,
	IIdGenerator idGenerator,
	string jobName,
	string queueName,
	Func<System.Text.Json.JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
) : IJobScheduler<TPayload>
{
	/// <summary>
	/// 	The storage provider.
	/// </summary>
	/// <value>
	/// 	The provider used to persist jobs and schedules.
	/// </value>
	protected IJobStorage Storage { get; } = storage;

	/// <summary>
	/// 	The serializer used with generated payload and context metadata.
	/// </summary>
	/// <value>
	/// 	The serializer used by this scheduler.
	/// </value>
	protected IJobSerializer Serializer { get; } = serializer;

	/// <summary>
	/// 	The clock used for deterministic scheduling.
	/// </summary>
	/// <value>
	/// 	The clock used to determine due times.
	/// </value>
	protected TimeProvider TimeProvider { get; } = timeProvider;

	/// <summary>
	/// 	The stable generated name.
	/// </summary>
	public string JobName { get; } = jobName;

	/// <summary>
	/// 	The stable queue used for new invocations.
	/// </summary>
	public string QueueName { get; } = queueName;

	/// <summary>
	/// 	Captures the context envelope persisted with a new invocation.
	/// </summary>
	/// <returns>
	/// 	The serialized context envelope, or <see langword="null"/> when there is no context.
	/// </returns>
	protected virtual string? CaptureContext() => null;

	/// <inheritdoc />
	public ValueTask<JobHandle> EnqueueAsync(TPayload payload, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> EnqueueAsync(TPayload payload, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAsync(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAsync(TPayload payload, DateTimeOffset at, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		DateTimeOffset at,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, JobHandle job, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, JobHandle job, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, JobHandle job, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		JobHandle job,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<JobHandle> jobs, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<JobHandle> jobs, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<JobHandle> jobs, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<JobHandle> jobs,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, BatchHandle batch, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, BatchHandle batch, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, BatchHandle batch, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		BatchHandle batch,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<BatchHandle> batches, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<BatchHandle> batches, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<BatchHandle> batches, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<BatchHandle> batches,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle Enqueue(TPayload payload, Batch batch) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle Enqueue(TPayload payload, Batch batch, string groupId) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay, string groupId) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at, string groupId) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(TPayload payload, BatchJobHandle job, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(TPayload payload, BatchJobHandle job, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(TPayload payload, BatchJobHandle job, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(TPayload payload, IReadOnlyList<BatchJobHandle> jobs, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(TPayload payload, IReadOnlyList<BatchJobHandle> jobs, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(TPayload payload, IReadOnlyList<BatchJobHandle> jobs, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public ValueTask CancelAsync(JobHandle job, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	private async ValueTask<JobHandle> ScheduleAfterCoreAsync(
		JobHandle[] parents,
		TPayload payload,
		ContinuationTrigger on,
		TimeSpan? delay,
		CancellationToken cancellationToken
	)
	{
		var graphStorage = JobStorageCapabilityGuards.RequireGraph(Storage);
		var runAt = TimeProvider.GetUtcNow() + (delay ?? TimeSpan.Zero);
		var parentIds = ValidateContinuationParents(parents);
		var batch = parents[0].Batch;
		if (batch is not null)
		{
			var batchRecord = CreateRecord(payload, runAt);
			return batch.Add(batchRecord, parents, on);
		}

		var record = CreateRecord(payload, runAt);
		var waiting = record with { State = JobState.AwaitingContinuation, RemainingDependencies = parents.Length };
		var edges = parentIds.Select(parentId => new JobContinuationEdge
		{
			ChildJobId = record.Id,
			ParentJobId = parentId,
			Trigger = on,
		}).ToArray();
		await graphStorage.EnqueueContinuationAsync(waiting, edges, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return new(record.Id);
	}

	private static HashSet<string> ValidateContinuationParents(JobHandle[] parents)
	{
		var parentIds = new HashSet<string>(StringComparer.Ordinal);
		var batch = parents[0].Batch;
		foreach (var parent in parents)
		{
			if (string.IsNullOrWhiteSpace(parent.Id))
				throw new ImmediateJobException("Continuation parent handles must have a non-empty identifier.");
			if (!ReferenceEquals(parent.Batch, batch))
				throw new ImmediateJobException("Continuation handles from unrelated scopes cannot be mixed.");
			if (!parentIds.Add(parent.Id))
				throw new ImmediateJobException($"Duplicate continuation parent '{parent.Id}'.");
		}

		batch?.EnsureOpen();
		return parentIds;
	}

	private JobRecord CreateRecord(TPayload payload, DateTimeOffset runAt, string? groupId = null)
	{
		groupId = NormalizeGroupId(groupId);
		var now = TimeProvider.GetUtcNow();
		var (traceParent, traceState) = TraceContextCapture.Current();
		var context = CaptureContext();
		return new()
		{
			Id = idGenerator.CreateId(IdKind.Job),
			JobName = JobName,
			QueueName = QueueName,
			GroupId = groupId,
			Payload = Serializer.Serialize(payload, payloadTypeInfoFactory),
			State = runAt <= now ? JobState.Pending : JobState.Scheduled,
			DueAt = runAt,
			CreatedAt = now,
			TraceParent = traceParent,
			TraceState = traceState,
			Context = context,
		};
	}

	private static string? NormalizeGroupId(string? groupId)
	{
		if (string.IsNullOrWhiteSpace(groupId))
			return null;
		if (groupId.Length > 128)
			throw new ArgumentException("A fair queue group id cannot exceed 128 characters.", nameof(groupId));
		return groupId;
	}

	/// <summary>
	/// 	Validates and persists a dynamic recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The unique schedule name.
	/// </param>
	/// <param name="cron">
	/// 	The cron expression that determines occurrences.
	/// </param>
	/// <param name="timeZone">
	/// 	The time zone used to evaluate <paramref name="cron"/>.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	A task that completes when the schedule has been persisted.
	/// </returns>
	protected async ValueTask AddOrUpdateRecurringCoreAsync(
		string name,
		string cron,
		string timeZone,
		CancellationToken cancellationToken
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		var zone = JobCron.GetTimeZone(timeZone);
		var expression = JobCron.Parse(cron);
		var next = expression.GetNextOccurrence(TimeProvider.GetUtcNow(), zone)
			?? throw new ArgumentException("The cron expression has no future occurrence.", nameof(cron));

		var recurringStorage = JobStorageCapabilityGuards.RequireRecurring(Storage);
		await recurringStorage.UpsertRecurringAsync(
			new()
			{
				Name = name,
				JobName = JobName,
				Cron = cron,
				TimeZone = timeZone,
				IsCodeDefined = false,
				NextRunAt = next,
			},
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <summary>
	/// 	Removes a dynamic recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The unique schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	A task that completes when the schedule has been removed.
	/// </returns>
	protected ValueTask RemoveRecurringCoreAsync(
		string name,
		CancellationToken cancellationToken
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		return JobStorageCapabilityGuards.RequireRecurring(Storage)
			.RemoveRecurringAsync(name, cancellationToken);
	}
}

internal static class JobCron
{
	public static CronExpression Parse(string cron)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cron);

		Span<Range> splits = stackalloc Range[7];
		var numSplits = cron.AsSpan().Split(splits, ' ');

		var format = numSplits switch
		{
			6 => CronFormat.IncludeSeconds,
			_ => CronFormat.Standard,
		};

		return CronExpression.Parse(cron, format);
	}

	public static TimeZoneInfo GetTimeZone(string timeZone)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);

		if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var tzi))
			throw new ArgumentException($"Unknown time-zone identifier '{timeZone}'.", nameof(timeZone));

		return tzi;
	}
}

internal static class TraceContextCapture
{
	public static (string? Parent, string? State) Current()
	{
		var activity = Activity.Current;
		return (activity?.Id, activity?.TraceStateString);
	}
}
