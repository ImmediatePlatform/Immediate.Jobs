namespace Immediate.Jobs.Shared;

/// <summary>
/// A best-effort, non-durable, single-node provider intended for development and tests.
/// </summary>
public sealed class InMemoryJobStorage(TimeProvider timeProvider) : IJobStorage, IJobStorageReplica
{
#if NET9_0_OR_GREATER
	private readonly Lock _gate = new();
#else
	private readonly object _gate = new();
#endif
	private readonly Dictionary<Guid, JobRecord> _jobs = [];
	private readonly Dictionary<string, RecurringJobSchedule> _recurring = new(StringComparer.Ordinal);
	private readonly Dictionary<string, JobServerSnapshot> _servers = new(StringComparer.Ordinal);
	private readonly HashSet<string> _recurringKeys = new(StringComparer.Ordinal);

	/// <inheritdoc />
	public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(job);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_jobs.TryAdd(job.Id, job))
				throw new InvalidOperationException($"Job '{job.Id}' already exists.");
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(request);
		cancellationToken.ThrowIfCancellationRequested();
		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			foreach (var expired in _jobs.Values.Where(x => x.State == JobState.Active && x.LeaseExpiresAt <= now).ToArray())
			{
				_jobs[expired.Id] = expired with
				{
					State = JobState.Pending,
					WorkerId = null,
					LeaseExpiresAt = null,
				};
			}

			var acquired = new List<JobRecord>(request.BatchSize);
			foreach (var queue in request.Queues)
			{
				var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
				if (queueCapacity <= 0)
					continue;

				var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
				foreach (var candidate in _jobs.Values
					.Where(job => job.QueueName == queue.QueueName &&
						jobCapacities.ContainsKey(job.JobName) &&
						job.State is JobState.Pending or JobState.Scheduled && job.DueAt <= now)
					.OrderBy(job => job.DueAt)
					.ThenBy(job => job.CreatedAt)
					.ThenBy(job => job.Id))
				{
					if (queueCapacity == 0)
						break;
					if (jobCapacities[candidate.JobName] <= 0)
						continue;

					var job = candidate with
					{
						State = JobState.Active,
						Attempt = candidate.Attempt + 1,
						WorkerId = request.WorkerId,
						LeaseExpiresAt = now + request.Lease,
					};
					_jobs[job.Id] = job;
					acquired.Add(job);
					jobCapacities[job.JobName]--;
					queueCapacity--;
				}
			}

			return ValueTask.FromResult<IReadOnlyList<JobRecord>>(acquired);
		}
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<Guid> jobIds,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(jobIds);
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
		cancellationToken.ThrowIfCancellationRequested();
		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			var acquired = new List<JobRecord>(jobIds.Count);
			foreach (var id in jobIds)
			{
				if (!_jobs.TryGetValue(id, out var job))
					continue;
				if (job.State == JobState.Active && job.LeaseExpiresAt <= now)
				{
					job = job with { State = JobState.Pending, WorkerId = null, LeaseExpiresAt = null };
					_jobs[id] = job;
				}

				if (job.State is not (JobState.Pending or JobState.Scheduled) || job.DueAt > now)
					continue;

				job = job with
				{
					State = JobState.Active,
					Attempt = job.Attempt + 1,
					WorkerId = workerId,
					LeaseExpiresAt = now + lease,
				};
				_jobs[id] = job;
				acquired.Add(job);
			}

			return ValueTask.FromResult<IReadOnlyList<JobRecord>>(acquired);
		}
	}

	/// <inheritdoc />
	public ValueTask RenewLeaseAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var job = GetOwnedActive(jobId, workerId);
			_jobs[jobId] = job with { LeaseExpiresAt = timeProvider.GetUtcNow() + lease };
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var job = GetOwnedActive(jobId, workerId);
			_jobs[jobId] = job with
			{
				State = JobState.Succeeded,
				WorkerId = null,
				LeaseExpiresAt = null,
				CompletedAt = timeProvider.GetUtcNow(),
			};
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask FailAsync(
		Guid jobId,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var job = GetOwnedActive(jobId, workerId);
			_jobs[jobId] = job with
			{
				State = nextRetryAt.HasValue ? JobState.Scheduled : JobState.Failed,
				DueAt = nextRetryAt ?? job.DueAt,
				WorkerId = null,
				LeaseExpiresAt = null,
				LastError = error,
				CompletedAt = nextRetryAt.HasValue ? null : timeProvider.GetUtcNow(),
			};
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (_recurring.TryGetValue(schedule.Name, out var current))
			{
				schedule = schedule with
				{
					IsPaused = current.IsPaused,
					LastRunAt = current.LastRunAt,
				};
			}

			_recurring[schedule.Name] = schedule;
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (_recurring.TryGetValue(name, out var schedule) && schedule.IsCodeDefined)
				throw new InvalidOperationException("Code-defined recurring schedules cannot be deleted.");

			_ = _recurring.Remove(name);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default) =>
		SetRecurringPaused(name, true, cancellationToken);

	/// <inheritdoc />
	public ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default) =>
		SetRecurringPaused(name, false, cancellationToken);

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			return ValueTask.FromResult<IReadOnlyList<RecurringJobSchedule>>(
				[.. _recurring.Values.Where(x => !x.IsPaused && x.NextRunAt <= now).OrderBy(x => x.NextRunAt).Take(batchSize)]
			);
		}
	}

	/// <inheritdoc />
	public ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		ArgumentNullException.ThrowIfNull(job);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_recurring.TryGetValue(schedule.Name, out var current) || current.NextRunAt != schedule.NextRunAt)
				return ValueTask.FromResult(false);

			var inserted = job.RecurringKey is null || _recurringKeys.Add(job.RecurringKey);
			if (inserted)
				_jobs[job.Id] = job;
			_recurring[schedule.Name] = current with { LastRunAt = schedule.NextRunAt, NextRunAt = nextRunAt };
			return ValueTask.FromResult(inserted);
		}
	}

	/// <inheritdoc />
	public ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var counts = Enum.GetValues<JobState>().ToDictionary(state => state, state => _jobs.Values.LongCount(x => x.State == state));
			var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromMinutes(2);
			IReadOnlyList<JobServerSnapshot> servers = [.. _servers.Values.Where(x => x.LastHeartbeat >= cutoff)];
			return ValueTask.FromResult(new JobMonitoringSnapshot(timeProvider.GetUtcNow(), counts, [.. _recurring.Values], servers));
		}
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(query);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var jobs = _jobs.Values.AsEnumerable();
			if (query.Id is { } id)
				jobs = jobs.Where(x => x.Id == id);
			if (query.State is { } state)
				jobs = jobs.Where(x => x.State == state);
			if (!string.IsNullOrWhiteSpace(query.QueueName))
				jobs = jobs.Where(x => x.QueueName == query.QueueName);

			if (!string.IsNullOrWhiteSpace(query.Search))
				jobs = jobs.Where(x => x.JobName.Contains(query.Search, StringComparison.OrdinalIgnoreCase));

			return ValueTask.FromResult<IReadOnlyList<JobRecord>>(
				[.. jobs.OrderByDescending(x => x.CreatedAt).Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 1000))]
			);
		}
	}

	/// <inheritdoc />
	public ValueTask RetryAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job) || job.State != JobState.Failed)
				throw new InvalidOperationException("Only failed jobs can be retried.");

			_jobs[jobId] = job with { State = JobState.Pending, DueAt = timeProvider.GetUtcNow(), CompletedAt = null };
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (_jobs.TryGetValue(jobId, out var job) && job.State is JobState.Active or JobState.Pending or JobState.Scheduled)
				throw new InvalidOperationException("Only terminal jobs can be deleted.");

			_ = _jobs.Remove(jobId);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask PurgeAsync(TimeSpan succeededRetention, TimeSpan failedRetention, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			foreach (var id in _jobs.Values
				.Where(x => x.CompletedAt is { } completed &&
					(x.State == JobState.Succeeded && completed < now - succeededRetention ||
					 x.State is JobState.Failed or JobState.Cancelled && completed < now - failedRetention))
				.Select(x => x.Id)
				.ToArray())
			{
				_ = _jobs.Remove(id);
			}
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
			_servers[server.WorkerId] = server;
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult(true);
	}

	private JobRecord GetOwnedActive(Guid jobId, string workerId)
	{
		if (!_jobs.TryGetValue(jobId, out var job) || job.State != JobState.Active || job.WorkerId != workerId)
			throw new InvalidOperationException($"Worker '{workerId}' does not own active job '{jobId}'.");
		return job;
	}

	private ValueTask SetRecurringPaused(string name, bool isPaused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_recurring.TryGetValue(name, out var schedule))
				throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");

			_recurring[name] = schedule with { IsPaused = isPaused };
		}

		return ValueTask.CompletedTask;
	}
}
