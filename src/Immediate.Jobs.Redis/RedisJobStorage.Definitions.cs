using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;
using Immediate.Jobs.Shared.Storage;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	/// <inheritdoc />
	public async ValueTask PauseJobAsync(string jobName, CancellationToken cancellationToken = default)
	{
		PauseJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		var name = jobName.ToUpperInvariant();
		var result = await Database.ScriptEvaluateAsync(
			"if redis.call('HEXISTS', KEYS[1], ARGV[1]) == 0 then return 0 end; redis.call('HSET', KEYS[2], 'paused', '1', 'status', '1', 'slidingOldest', '', 'fixedNext', ''); return 1",
			[DefinitionMetadataKey, _root + "definitions:state:" + name], [name]).WaitAsync(cancellationToken);
		if ((long)result == 0)
			throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");
	}

	/// <inheritdoc />
	public async ValueTask ResumeJobAsync(string jobName, CancellationToken cancellationToken = default)
	{
		ResumeJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EvaluateDefinitionAsync(jobName, resume: true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, CancellationToken cancellationToken = default)
	{
		GetJobAcquisitionStateAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		return await EvaluateDefinitionAsync(jobName, resume: false, cancellationToken);
	}

	private async ValueTask<JobAcquisitionState> EvaluateDefinitionAsync(string jobName, bool resume, CancellationToken cancellationToken)
	{
		var name = jobName.ToUpperInvariant();
		var refresh = false;
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var definitions = await ReadDefinitionSnapshotsAsync([name], refresh, cancellationToken);
			if (!definitions.TryGetValue(name, out var snapshot))
				throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");
			var limits = snapshot.Limits;
			var now = _timeProvider.GetUtcNow();
			var values = new List<RedisValue> { _root, name, Score(now), Timestamp(now), resume ? 1 : 0, snapshot.Metadata };
			AddDefinitionLimits(values, limits, now);
			var response = await Database.ScriptEvaluateAsync(RedisScripts.DefinitionState, [DefinitionMetadataKey], [.. values]).WaitAsync(cancellationToken);
			// Re-read if the definition changed before the script could evaluate its limits.
			if (response.IsNull)
			{
				refresh = true;
				continue;
			}

			return ToAcquisitionState(snapshot.Definition.Name, limits, (RedisResult[])response!);
		}
	}

	private async ValueTask<Dictionary<string, DefinitionSnapshot>> ReadDefinitionSnapshotsAsync(
		List<string> names,
		bool refresh,
		CancellationToken cancellationToken
	)
	{
		var definitions = new Dictionary<string, DefinitionSnapshot>(names.Count, StringComparer.Ordinal);
		if (!refresh)
		{
			foreach (var name in names)
				if (_definitionSnapshots.TryGetValue(name, out var snapshot))
					definitions.Add(name, snapshot);
			if (definitions.Count == names.Count)
				return definitions;
			definitions.Clear();
		}

		RedisValue[] keys = [.. names.Select(static name => (RedisValue)name)];
		var metadata = await Database.HashGetAsync(DefinitionMetadataKey, keys).WaitAsync(cancellationToken);
		for (var index = 0; index < names.Count; index++)
		{
			var name = names[index];
			if (metadata[index].IsNull)
			{
				_definitionSnapshots.TryRemove(name, out _);
				continue;
			}

			if (!_definitionSnapshots.TryGetValue(name, out var snapshot) || snapshot.Metadata != metadata[index])
			{
				var definition = JsonSerializer.Deserialize((byte[])metadata[index]!, RedisJsonSerializerContext.Default.JobDefinitionRecord)
					?? throw new ImmediateJobException($"Job definition '{name}' contains invalid data.");
				snapshot = new(metadata[index], definition);
				_definitionSnapshots[name] = snapshot;
			}

			definitions.Add(name, snapshot);
		}

		// Missing definitions are never cached: a later registration must be visible on the next request.
		return definitions;
	}

	private sealed record DefinitionSnapshot(RedisValue Metadata, JobDefinitionRecord Definition)
	{
		public JobAcquisitionLimits Limits { get; } = Definition.AcquisitionLimits;
	}

	private static JobAcquisitionState ToAcquisitionState(string jobName, JobAcquisitionLimits limits, RedisResult[] result)
	{
		var slidingOldest = FromNullableTimestamp((string?)result[1]);
		var fixedNext = FromNullableTimestamp((string?)result[2]);
		var next = slidingOldest + limits.SlidingWindowPeriod;
		if (fixedNext is { } boundary && (next is null || boundary > next))
			next = boundary;

		return new()
		{
			JobName = jobName,
			IsPaused = (long)result[3] == 1,
			ActiveCount = (int)(long)result[4],
			SlidingWindowCount = (int)(long)result[5],
			FixedWindowCount = (int)(long)result[6],
			IsConcurrencyLimited = limits.MaxConcurrency > 0 && (long)result[4] >= limits.MaxConcurrency,
			AcquisitionStatus = (JobAcquisitionStatus)(long)result[0],
			NextEligibleAt = next,
		};
	}

	private static void AddDefinitionLimits(List<RedisValue> values, JobAcquisitionLimits limits, DateTimeOffset now)
	{
		values.Add(limits.SlidingWindowMax);
		values.Add(limits.SlidingWindowPeriod is { } sliding ? Timestamp(now - sliding) : "");
		values.Add(limits.FixedWindowMax);
		var fixedStart = limits.FixedWindowStart(now);
		values.Add(NullableTimestamp(fixedStart));
		values.Add(fixedStart is { } start ? Timestamp(start + limits.FixedWindowPeriod!.Value) : "");
		values.Add(limits.MaxConcurrency);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.PauseJobAsyncCalled,
		EventName = "Immediate.Jobs.Redis.PauseJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseJobAsync called (JobName={JobName})"
	)]
	private partial void PauseJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.ResumeJobAsyncCalled,
		EventName = "Immediate.Jobs.Redis.ResumeJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeJobAsync called (JobName={JobName})"
	)]
	private partial void ResumeJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobAcquisitionStateAsyncCalled,
		EventName = "Immediate.Jobs.Redis.GetJobAcquisitionStateAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobAcquisitionStateAsync called (JobName={JobName})"
	)]
	private partial void GetJobAcquisitionStateAsyncCalled(string jobName);
}
