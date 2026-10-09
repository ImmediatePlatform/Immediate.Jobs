using Microsoft.Extensions.Logging;
using System.Globalization;
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
		_ = await Database.ScriptEvaluateAsync(
			"redis.call('HSET', KEYS[1], 'paused', '1', 'status', '1', 'next', ''); return 1",
			[_root + "definitions:state:" + jobName.ToUpperInvariant()], []).WaitAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeJobAsync(string jobName, JobAcquisitionLimits limits, CancellationToken cancellationToken = default)
	{
		ResumeJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		_ = await EvaluateDefinitionAsync(jobName, limits, resume: true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, JobAcquisitionLimits limits, CancellationToken cancellationToken = default)
	{
		GetJobAcquisitionStateAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		return await EvaluateDefinitionAsync(jobName, limits, resume: false, cancellationToken);
	}

	private async ValueTask<JobAcquisitionState> EvaluateDefinitionAsync(string jobName, JobAcquisitionLimits limits, bool resume, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		limits.Validate();
		var now = _timeProvider.GetUtcNow();
		var values = new List<RedisValue> { _root, jobName.ToUpperInvariant(), Score(now), Ticks(now), resume ? 1 : 0 };
		AddDefinitionLimits(values, limits, now);
		var result = (RedisResult[])(await Database.ScriptEvaluateAsync(RedisScripts.DefinitionState, [LeasesKey], [.. values]).WaitAsync(cancellationToken))!;
		var next = (string)result[1]!;
		return new()
		{
			JobName = jobName,
			IsPaused = (long)result[2] == 1,
			ActiveCount = (int)(long)result[3],
			IsConcurrencyLimited = limits.MaxConcurrency > 0 && (long)result[3] >= limits.MaxConcurrency,
			AcquisitionStatus = (JobAcquisitionStatus)(long)result[0],
			NextEligibleAt = next.Length == 0 ? null : new DateTimeOffset(long.Parse(next, CultureInfo.InvariantCulture), TimeSpan.Zero),
		};
	}

	private static void AddDefinitionLimits(List<RedisValue> values, JobAcquisitionLimits limits, DateTimeOffset now)
	{
		values.Add(limits.SlidingWindowMax);
		values.Add((limits.SlidingWindowPeriod?.Ticks ?? 0).ToString("D19", CultureInfo.InvariantCulture));
		values.Add(limits.SlidingWindowPeriod is { } sliding ? Ticks(now - sliding) : "");
		values.Add(limits.FixedWindowMax);
		values.Add(limits.FixedWindowStart(now) is { } start ? Ticks(start) : "");
		values.Add(limits.FixedWindowStart(now) is { } fixedStart ? Ticks(fixedStart + limits.FixedWindowPeriod!.Value) : "");
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
