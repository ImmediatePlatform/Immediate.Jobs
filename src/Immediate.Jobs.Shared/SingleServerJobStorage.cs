namespace Immediate.Jobs.Shared;

/// <summary>
/// A single-server storage topology that executes against an authoritative in-process store while
/// synchronously replicating changes to durable storage and restoring them when the process starts.
/// </summary>
public sealed class SingleServerJobStorage : IJobStorage, IAsyncDisposable, IDisposable
{
	private const int RecoveryBatchSize = 1000;
	private readonly TimeProvider _timeProvider;
	private readonly SemaphoreSlim _initialization = new(1, 1);
	private InMemoryJobStorage _primary;
	private bool _initialized;
	private bool _disposed;

	/// <summary>Creates a memory-primary store backed by the supplied durable replica.</summary>
	public SingleServerJobStorage(IJobStorage durableStorage, TimeProvider timeProvider)
	{
		ArgumentNullException.ThrowIfNull(durableStorage);
		ArgumentNullException.ThrowIfNull(timeProvider);
		if (durableStorage is SingleServerJobStorage)
			throw new ArgumentException("A single-server store cannot be used as its own durable replica.", nameof(durableStorage));
		if (durableStorage is not IJobStorageReplica)
			throw new ArgumentException("Single-server durable storage must implement IJobStorageReplica.", nameof(durableStorage));

		DurableStorage = durableStorage;
		_timeProvider = timeProvider;
		_primary = new(timeProvider);
	}

	/// <summary>The in-process authoritative store.</summary>
	public IJobStorage PrimaryStorage => _primary;

	/// <summary>The durable write-through replica.</summary>
	public IJobStorage DurableStorage { get; }

	/// <inheritdoc />
	public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => EnsureInitializedAsync(cancellationToken);

	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
		await _primary.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		var acquired = await _primary.AcquireDueJobsAsync(request, cancellationToken).ConfigureAwait(false);
		if (acquired.Count == 0)
			return acquired;

		var replica = (IJobStorageReplica)DurableStorage;
		var replicated = await replica.AcquireJobsAsync(
			[.. acquired.Select(x => x.Id)],
			request.WorkerId,
			request.Lease,
			cancellationToken
		).ConfigureAwait(false);
		if (acquired.Count != replicated.Count ||
			!acquired.Select(x => x.Id).ToHashSet(StringComparer.Ordinal).SetEquals(replicated.Select(x => x.Id)))
		{
			throw new InvalidOperationException(
				"The durable job replica has drifted from the authoritative in-memory queue. " +
				"Single-server mode must not be used by multiple scheduler processes."
			);
		}

		return acquired;
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		string jobId,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RenewLeaseAsync(jobId, workerId, lease, cancellationToken).ConfigureAwait(false);
		await _primary.RenewLeaseAsync(jobId, workerId, lease, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask CompleteAsync(string jobId, string workerId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.CompleteAsync(jobId, workerId, cancellationToken).ConfigureAwait(false);
		await _primary.CompleteAsync(jobId, workerId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask FailAsync(
		string jobId,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.FailAsync(jobId, workerId, error, nextRetryAt, cancellationToken).ConfigureAwait(false);
		await _primary.FailAsync(jobId, workerId, error, nextRetryAt, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.UpsertRecurringAsync(schedule, cancellationToken).ConfigureAwait(false);
		await _primary.UpsertRecurringAsync(schedule, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken)
			.ConfigureAwait(false);
		await _primary.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RemoveRecurringAsync(name, cancellationToken).ConfigureAwait(false);
		await _primary.RemoveRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.PauseRecurringAsync(name, cancellationToken).ConfigureAwait(false);
		await _primary.PauseRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.ResumeRecurringAsync(name, cancellationToken).ConfigureAwait(false);
		await _primary.ResumeRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetDueRecurringAsync(now, batchSize, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		if (!await DurableStorage.MaterializeRecurringAsync(schedule, job, nextRunAt, cancellationToken).ConfigureAwait(false))
			return false;
		return await _primary.MaterializeRecurringAsync(schedule, job, nextRunAt, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.QueryJobsAsync(query, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RetryAsync(jobId, cancellationToken).ConfigureAwait(false);
		await _primary.RetryAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
		await _primary.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask PurgeAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.PurgeAsync(succeededRetention, failedRetention, cancellationToken).ConfigureAwait(false);
		await _primary.PurgeAsync(succeededRetention, failedRetention, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _primary.HeartbeatAsync(server, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await DurableStorage.IsHealthyAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		if (DurableStorage is IDisposable disposable)
			disposable.Dispose();
		else if (DurableStorage is IAsyncDisposable asyncDisposable)
			asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_initialization.Dispose();
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;
		_disposed = true;
		if (DurableStorage is IAsyncDisposable asyncDisposable)
			await asyncDisposable.DisposeAsync().ConfigureAwait(false);
		else if (DurableStorage is IDisposable disposable)
			disposable.Dispose();
		_initialization.Dispose();
	}

	private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (Volatile.Read(ref _initialized))
			return;

		await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (Volatile.Read(ref _initialized))
				return;

			await DurableStorage.InitializeAsync(cancellationToken).ConfigureAwait(false);
			var recoveredPrimary = new InMemoryJobStorage(_timeProvider);
			await recoveredPrimary.InitializeAsync(cancellationToken).ConfigureAwait(false);

			foreach (var state in Enum.GetValues<JobState>())
			{
				var skip = 0;
				while (true)
				{
					var jobs = await DurableStorage.QueryJobsAsync(
						new() { State = state, Skip = skip, Take = RecoveryBatchSize },
						cancellationToken
					).ConfigureAwait(false);
					foreach (var job in jobs)
						await recoveredPrimary.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
					if (jobs.Count < RecoveryBatchSize)
						break;
					skip += jobs.Count;
				}
			}

			var snapshot = await DurableStorage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
			foreach (var schedule in snapshot.Recurring)
				await recoveredPrimary.UpsertRecurringAsync(schedule, cancellationToken).ConfigureAwait(false);

			_primary = recoveredPrimary;
			Volatile.Write(ref _initialized, true);
		}
		finally
		{
			_ = _initialization.Release();
		}
	}
}
