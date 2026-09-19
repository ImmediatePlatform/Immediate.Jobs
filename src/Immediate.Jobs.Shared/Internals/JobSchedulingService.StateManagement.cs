using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Internals;

public sealed partial class JobSchedulingService
{
	private JobWorkerSnapshot[] _workers;
	private JobWorkerSnapshot[] _inactiveWorkers;
	private readonly Lock _loopGate = new();
	private JobLoopSnapshot _acquisition = new();
	private JobLoopSnapshot _leaseRenewal = new();
	private int _activeWorkers;
	private long _startedAtTicks = -1;
	private long _lastHeartbeatTicks = -1;

	/// <summary>
	///	    Reports whether the current job scheduler is healthy
	/// </summary>
	/// <returns>
	///	    <see langword="true" /> if the scheduler is disabled or if the scheduler has started and has a recent
	///	    heartbeat; <see langword="false"/> otherwise.
	/// </returns>
	public bool IsHealthy()
	{
		if (!_options.IsJobSchedulingServiceEnabled)
			return true;

		return _startedAtTicks >= 0
			&& Volatile.Read(ref _lastHeartbeatTicks) is var ticks and >= 0
			&& _timeProvider.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero) <= _options.ServerTimeout;
	}

	/// <remarks>
	///		This method intentionally does not try to gate reads; this may mean that the data
	///		is slightly inconsistent. This is an acceptable choice to allow improved performance
	///		of this method and to reduce potential contention from writers.
	/// </remarks>
	private JobServerSnapshot TriggerHeartbeat(DateTimeOffset timestamp)
	{
		Volatile.Write(ref _lastHeartbeatTicks, timestamp.UtcTicks);

		return new JobServerSnapshot
		{
			WorkerId = _workerId,
			LastHeartbeat = timestamp,
			ActiveWorkers = _activeWorkers,
			MaxWorkers = _options.WorkerCount,
			ServerTimeout = _options.ServerTimeout,
			Workers = [.. _workers],
			Acquisition = _acquisition,
			LeaseRenewal = _leaseRenewal,
		};
	}

	[MemberNotNull(nameof(_workers))]
	[MemberNotNull(nameof(_inactiveWorkers))]
	private void InitializeState(int workerCount)
	{
		_inactiveWorkers = [.. Enumerable.Range(0, workerCount).Select(i => new JobWorkerSnapshot() { WorkerId = i })];
		_workers = [.. _inactiveWorkers];
	}

	private void MarkStarted(DateTimeOffset timestamp)
	{
		_startedAtTicks = timestamp.UtcTicks;
	}

	private void StartExecution(int workerId, JobRecord record, DateTimeOffset startedAt)
	{
		Volatile.Write(
			ref _workers[workerId],
			new JobWorkerSnapshot
			{
				WorkerId = workerId,
				JobHandle = record.JobHandle,
				Attempt = record.Attempt,
				StartedAt = startedAt,
			}
		);

		Interlocked.Increment(ref _activeWorkers);
	}

	private void FinishExecution(int workerId)
	{
		Volatile.Write(ref _workers[workerId], _inactiveWorkers[workerId]);
		Interlocked.Decrement(ref _activeWorkers);
	}

	private void StartAcquisition(DateTimeOffset timestamp)
	{
		lock (_loopGate)
		{
			_acquisition = _acquisition with
			{
				IsRunning = true,
				LastAttemptedAt = timestamp,
				ItemsSucceeded = 0,
			};
		}
	}

	private void FinishAcquisition(DateTimeOffset timestamp, bool succeeded, int jobsAcquired)
	{
		lock (_loopGate)
		{
			_acquisition = succeeded switch
			{
				true => _acquisition with
				{
					IsRunning = false,
					LastSucceededAt = timestamp,
					ItemsSucceeded = jobsAcquired,
					ConsecutiveFailures = 0,
				},

				false => _acquisition with
				{
					IsRunning = false,
					LastFailedAt = timestamp,
					ItemsSucceeded = 0,
					ConsecutiveFailures = _acquisition.ConsecutiveFailures + 1,
				},
			};
		}
	}

	private void StartLeaseRenewal(DateTimeOffset timestamp)
	{
		lock (_loopGate)
		{
			_leaseRenewal = _leaseRenewal with
			{
				IsRunning = true,
				LastAttemptedAt = timestamp,
				ItemsSucceeded = 0,
				ItemsFailed = 0,
			};
		}
	}

	private void FinishLeaseRenewal(DateTimeOffset timestamp, int succeeded, int failed)
	{
		lock (_loopGate)
		{
			_leaseRenewal = failed switch
			{
				0 => _leaseRenewal with
				{
					IsRunning = false,
					LastSucceededAt = timestamp,
					ConsecutiveFailures = 0,
					ItemsSucceeded = succeeded,
					ItemsFailed = 0,
				},

				_ => _leaseRenewal with
				{
					IsRunning = false,
					LastFailedAt = timestamp,
					ConsecutiveFailures = _leaseRenewal.ConsecutiveFailures + 1,
					ItemsSucceeded = succeeded,
					ItemsFailed = failed,
				},
			};
		}
	}
}
