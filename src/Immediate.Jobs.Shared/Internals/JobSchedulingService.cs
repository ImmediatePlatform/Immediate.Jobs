using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Coordinates recurring schedules, durable leases, and the bounded worker pool.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed partial class JobSchedulingService : BackgroundService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IJobStorage _storage;
	private readonly ImmediateJobsOptions _options;
	private readonly FairQueueOptions _fairQueueOptions;
	private readonly TimeProvider _timeProvider;
	private readonly IIdGenerator _idGenerator;
	private readonly ILogger<JobSchedulingService> _logger;
	private readonly JobSchedulerState _state;

	private readonly Dictionary<string, JobDefinition> _definitions;

	/// <summary>
	///		Complex structure used to simplify repeated access in <see cref="BuildAcquisitionRequest"/>.
	/// </summary>
	private readonly List<
		KeyValuePair<
			int,
			Queue<
				KeyValuePair<
					JobQueueDefinition,
					List<JobDefinition>
				>
			>
		>
	> _queuesByPriority;

	private readonly ConcurrentDictionary<string, int> _queueReservations = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, int> _jobReservations = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<(JobHandle Handle, int Attempt), OpenLease> _openLeases = [];
	private readonly SemaphoreSlim _scheduleInitialization = new(1, 1);
	private readonly CancellationTokenSource _workerCancellation = new();
	private readonly string _workerId = string.Create(CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{DateTimeOffset.UtcNow.Ticks}");
	private readonly Channel<JobRecord> _channel;
	private int _reservations;
	private int _fairQueuesDisabledWarningLogged;
	private long _nextPurgeTimestamp;
	private bool _initialized;

	/// <summary>
	/// 	Creates the hosted scheduler from generated definitions.
	/// </summary>
	/// <param name="scopeFactory">
	/// 	The factory used to create a dependency injection scope for each execution.
	/// </param>
	/// <param name="storage">
	/// 	The durable job storage provider.
	/// </param>
	/// <param name="definitions">
	/// 	The generated job definitions available to the scheduler.
	/// </param>
	/// <param name="options">
	/// 	The scheduler runtime options.
	/// </param>
	/// <param name="fairQueueOptions">
	/// 	The fair queue policy options.
	/// </param>
	/// <param name="timeProvider">
	/// 	The clock used for scheduling, leases, and timestamps.
	/// </param>
	/// <param name="idGenerator">
	/// 	The generator used to create job identifiers.
	/// </param>
	/// <param name="logger">
	/// 	The scheduler logger.
	/// </param>
	/// <param name="state">
	/// 	The service that tracks scheduler runtime state.
	/// </param>
	public JobSchedulingService(
		IServiceScopeFactory scopeFactory,
		IJobStorage storage,
		IEnumerable<JobDefinition> definitions,
		IOptions<ImmediateJobsOptions> options,
		IOptions<FairQueueOptions> fairQueueOptions,
		TimeProvider timeProvider,
		IIdGenerator idGenerator,
		ILogger<JobSchedulingService> logger,
		JobSchedulerState state
	)
	{
		ArgumentNullException.ThrowIfNull(scopeFactory);
		ArgumentNullException.ThrowIfNull(storage);
		ArgumentNullException.ThrowIfNull(definitions);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(fairQueueOptions);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(idGenerator);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(state);

		_scopeFactory = scopeFactory;
		_storage = storage;
		_options = options.Value;
		_fairQueueOptions = fairQueueOptions.Value;
		_timeProvider = timeProvider;
		_idGenerator = idGenerator;
		_logger = logger;
		_state = state;

#pragma warning disable CA1851 // `definitions` is backed by a list
		_definitions = definitions
			.ToDictionary(x => x.Name, StringComparer.Ordinal);

		_queuesByPriority = definitions
			.GroupBy(d => d.Queue)
			.GroupBy(
				g => g.Key.Priority,
				(priority, g) => KeyValuePair.Create(
					priority,
					new Queue<KeyValuePair<JobQueueDefinition, List<JobDefinition>>>(
						g
							.OrderBy(d => d.Key.Name, StringComparer.Ordinal)
							.Select(d => KeyValuePair.Create(
								d.Key,
								d.ToList()
							))
					)
				)
			)
			.OrderByDescending(d => d.Key)
			.ToList();
#pragma warning restore CA1851

		// Reservation accounting in BuildAcquisitionRequest is the admission control, so the channel is
		// only a handoff buffer. A bounded channel would add a second, redundant limit whose sole effect
		// is to block the scheduler loop -- and with it the heartbeat -- if the two ever disagree.
		_channel = Channel.CreateUnbounded<JobRecord>(new UnboundedChannelOptions
		{
			SingleWriter = true,
			SingleReader = _options.WorkerCount == 1,
		});

		if (storage is not IJobGraphStorage)
			GraphFeaturesDisabled(storage.GetType().Name);
		if (storage is not IRecurringJobStorage)
			RecurringJobFeaturesDisabled(storage.GetType().Name);
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!_options.IsJobSchedulingServiceEnabled)
			return;

		await _storage.InitializeAsync(stoppingToken);
		await InitializeAsync(stoppingToken);
		_state.MarkStarted(_timeProvider.GetUtcNow());

		// Workers observe _workerCancellation rather than stoppingToken: shutdown completes the channel so
		// buffered records still drain, and only an exceeded drain deadline cancels a running job.
		var workers = Enumerable.Range(0, _options.WorkerCount)
			.Select(workerId => RunWorkerAsync(workerId, _workerCancellation.Token))
			.ToList();
		var pollingLoop = RunPollingLoopAsync(stoppingToken);
		var heartbeatLoop = RunHeartbeatLoopAsync(stoppingToken);
		var leaseRenewalLoop = RunLeaseRenewalLoopAsync(_workerCancellation.Token);

		try
		{
			await Task.WhenAll(pollingLoop, heartbeatLoop);
		}
		finally
		{
			_channel.Writer.TryComplete();

			try
			{
				// stoppingToken is already cancelled here; forwarding it would abort the drain immediately.
				await Task.WhenAll(workers)
					.WaitAsync(_options.ShutdownTimeout, _timeProvider, CancellationToken.None);
			}
			catch (TimeoutException)
			{
				ShutdownDrainExceeded(_options.ShutdownTimeout);
			}
			finally
			{
				await _workerCancellation.CancelAsync();
				await leaseRenewalLoop;
			}
		}
	}

	/// <summary>
	///	    Materializes and executes all work currently due, returning when the due queue is empty. Delayed work is
	///     left in storage. This method is intended for deterministic test harnesses.
	/// </summary>
	/// <param name="cancellationToken">
	///     A token that can cancel draining.
	/// </param>
	/// <returns>
	///     A task that completes when no currently due work remains.
	/// </returns>
	public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await _storage.InitializeAsync(cancellationToken);
		await InitializeAsync(cancellationToken);

		while (true)
		{
			await MaterializeRecurringAsync(cancellationToken);

			var request = BuildAcquisitionRequest();
			if (request is null)
				return;

			var jobs = await _storage.AcquireDueJobsAsync(request, cancellationToken);
			if (jobs.Count == 0)
				return;

			WarnIfGroupedJobsAreInert(jobs);

			foreach (var job in jobs)
			{
				Reserve(job);
				await ExecuteJobAsync(0, job, cancellationToken);
			}
		}
	}

	private async Task RunPollingIterationAsync(CancellationToken cancellationToken)
	{
		await MaterializeRecurringAsync(cancellationToken);

		var acquired = BuildAcquisitionRequest() switch
		{
			{ } request => await _storage.AcquireDueJobsAsync(request, cancellationToken),
			_ => [],
		};

		WarnIfGroupedJobsAreInert(acquired);

		foreach (var job in acquired)
		{
			Reserve(job);
			try
			{
				JobTelemetry.Acquired();
				await _channel.Writer.WriteAsync(job, cancellationToken);
			}
			catch
			{
				Release(job);
				throw;
			}
		}

		if (_timeProvider.GetUtcNow().Ticks >= _nextPurgeTimestamp)
		{
			await _storage.PurgeJobsAsync(
				_options.SucceededRetention,
				_options.FailedRetention,
				cancellationToken
			);

			if (_storage is IJobGraphStorage graphStorage)
			{
				await graphStorage.PurgeBatchesAsync(
					_options.BatchSucceededRetention,
					_options.BatchFailedRetention,
					cancellationToken
				);
			}

			_nextPurgeTimestamp = _timeProvider.GetUtcNow().Ticks + _options.PurgeInterval.Ticks;
		}
	}

	private async Task RunHeartbeatIterationAsync(CancellationToken cancellationToken)
	{
		var now = _timeProvider.GetUtcNow();
		await _storage.HeartbeatAsync(new JobServerSnapshot
		{
			WorkerId = _workerId,
			LastHeartbeat = now,
			ActiveWorkers = _state.ActiveWorkers,
			MaxWorkers = _options.WorkerCount,
			ServerTimeout = _options.ServerTimeout,
			Workers = _state.Workers,
			Acquisition = _state.Acquisition,
			LeaseRenewal = _state.LeaseRenewal,
		}, cancellationToken);
		_state.MarkHeartbeat(now);
	}

	private async Task<(int Succeeded, int Failed)> RunLeaseRenewalIterationAsync(CancellationToken cancellationToken)
	{
		var now = _timeProvider.GetUtcNow();
		var succeeded = 0;
		var failed = 0;
		foreach (var lease in _openLeases.Values.Where(lease => lease.NextRenewal <= now).OrderBy(lease => lease.NextRenewal))
		{
			if (await RenewLeaseAsync(lease.Record, cancellationToken))
			{
				lease.NextRenewal = _timeProvider.GetUtcNow() + LeaseRenewalInterval;
				succeeded++;
			}
			else
			{
				lease.NextRenewal = _timeProvider.GetUtcNow() + TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, LeaseRenewalInterval.Ticks / 4));
				failed++;
			}
		}

		return (succeeded, failed);
	}

	private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			_state.StartAcquisition(_timeProvider.GetUtcNow());
			try
			{
				await RunPollingIterationAsync(cancellationToken);
				_state.FinishAcquisition(_timeProvider.GetUtcNow(), succeeded: true);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				_state.StopAcquisition();
				break;
			}

#pragma warning disable CA1031 // An iteration failure must not terminate its independent scheduler loop.
			catch (Exception exception)
#pragma warning restore CA1031
			{
				_state.FinishAcquisition(_timeProvider.GetUtcNow(), succeeded: false);
				SchedulerIterationFailed(exception, "Acquisition");
			}

			try { await Task.Delay(_options.PollingInterval, _timeProvider, cancellationToken); }
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
		}
	}

	private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
	{
		var nextDue = _timeProvider.GetUtcNow();
		while (!cancellationToken.IsCancellationRequested)
		{
			var delay = nextDue - _timeProvider.GetUtcNow();
			if (delay > TimeSpan.Zero)
			{
				try { await Task.Delay(delay, _timeProvider, cancellationToken); }
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
			}

			try { await RunHeartbeatIterationAsync(cancellationToken); }
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }

#pragma warning disable CA1031 // An iteration failure must not terminate its independent scheduler loop.
			catch (Exception exception) { SchedulerIterationFailed(exception, "Heartbeat"); }
#pragma warning restore CA1031
			nextDue += HeartbeatInterval;
			if (nextDue < _timeProvider.GetUtcNow())
				nextDue = _timeProvider.GetUtcNow() + HeartbeatInterval;
		}
	}

	private async Task RunLeaseRenewalLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			var now = _timeProvider.GetUtcNow();
			var nextDue = _openLeases.Values.Min(lease => (DateTimeOffset?)lease.NextRenewal) ?? now + LeaseRenewalInterval;
			var delay = nextDue - now;
			if (delay > TimeSpan.Zero)
			{
				try { await Task.Delay(delay, _timeProvider, cancellationToken); }
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
			}

			var examined = _openLeases.Values.Count(lease => lease.NextRenewal <= _timeProvider.GetUtcNow());
			_state.StartLeaseRenewal(_timeProvider.GetUtcNow(), examined);
			try
			{
				var (succeeded, failed) = await RunLeaseRenewalIterationAsync(cancellationToken);
				_state.FinishLeaseRenewal(_timeProvider.GetUtcNow(), succeeded, failed);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				_state.StopLeaseRenewal();
				break;
			}
		}
	}

	private async Task RunWorkerAsync(int workerId, CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var record in _channel.Reader.ReadAllAsync(cancellationToken))
			{
				try
				{
					await ExecuteJobAsync(workerId, record, cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					break;
				}
#pragma warning disable CA1031 // A failed job must not terminate its worker loop.
				catch (Exception exception)
#pragma warning restore CA1031
				{
					UnhandledWorkerError(exception, record.JobHandle);
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The drain deadline expired. Records still buffered stay Active until their lease expires,
			// and the worker completes normally so a drained shutdown is not reported as an error.
		}
	}

	private async ValueTask ExecuteJobAsync(
		int workerId,
		JobRecord record,
		CancellationToken stoppingToken
	)
	{
		if (!_definitions.TryGetValue(record.JobName, out var definition))
		{
			try
			{
				await _storage
					.FailAsync(
						record.JobHandle,
						record.Attempt,
						_workerId,
						$"No generated job definition exists for '{record.JobName}'.",
						nextRetryAt: null,
						stoppingToken
					);
			}
			finally
			{
				Release(record);
			}

			return;
		}

		var started = _timeProvider.GetTimestamp();
		var startedAt = _timeProvider.GetUtcNow();

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

		using var timeoutTimer = _timeProvider.CreateTimer(
			static state => ((CancellationTokenSource)state!).Cancel(),
			timeout,
			definition.Timeout is { } timeoutValue ? timeoutValue : Timeout.InfiniteTimeSpan,
			Timeout.InfiniteTimeSpan
		);

		using var activity = JobTelemetry.ActivitySource.StartActivity(
			$"job {record.JobName}",
			ActivityKind.Consumer,
			default(ActivityContext),
			tags:
			[
				new("job.name", record.JobName),
				new("job.queue", record.QueueName),
				new("job.id", record.JobHandle),
				new("job.attempt", record.Attempt),
			],
			links: record.TraceParent switch
			{
				{ } when ActivityContext.TryParse(record.TraceParent, record.TraceState, isRemote: true, out var parent) =>
					[new(parent)],

				_ => null,
			}
		);

		using var logScope = _logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
		{
			["JobName"] = record.JobName,
			["QueueName"] = record.QueueName,
			["JobHandle"] = record.JobHandle,
			["Attempt"] = record.Attempt,
		});

		// Paired with FinishExecution/ExecutionFinished in the finally below, so nothing that can throw
		// may sit between this and the try.
		_state.StartExecution(workerId, record, startedAt);
		JobTelemetry.ExecutionStarted();

		try
		{
			try
			{
				await _storage.SetExecutionTelemetryAsync(
					record.JobHandle,
					record.Attempt,
					_workerId,
					activity?.TraceId.ToString(),
					activity?.SpanId.ToString(),
					startedAt,
					stoppingToken
				);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				throw;
			}
#pragma warning disable CA1031 // Telemetry persistence is best-effort and must not consume a job attempt.
			catch (Exception exception)
#pragma warning restore CA1031
			{
				ExecutionTelemetryPersistenceFailed(exception);
			}

			await using var scope = _scopeFactory.CreateAsyncScope();

			var executionBuffer = new JobExecutionBuffer();

			await definition.Invoker.InvokeAsync(
				scope.ServiceProvider,
				new JobExecution { Record = record, Definition = definition, CancellationToken = timeout.Token, Buffer = executionBuffer }
			);

			if (_storage is IJobGraphStorage graphStorage)
			{
				await graphStorage.CompleteWithContinuationsAsync(
					record.JobHandle,
					record.Attempt,
					_workerId,
					executionBuffer.SealAndSnapshot(),
					stoppingToken
				);
			}
			else
			{
				await _storage.CompleteAsync(record.JobHandle, record.Attempt, _workerId, stoppingToken);
			}

			var duration = _timeProvider.GetElapsedTime(started);
			JobTelemetry.Succeeded(record.JobName, record.QueueName, duration);
			activity?.SetStatus(ActivityStatusCode.Ok);
			JobCompleted(duration.TotalMilliseconds);
		}
		catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
		{
			var retry = record.Attempt < definition.MaxAttempts;

			var nextRetryAt = retry
				? _timeProvider.GetUtcNow() + GetRetryDelay(definition, record.Attempt)
				: default(DateTimeOffset?);

			await _storage.FailAsync(
				record.JobHandle,
				record.Attempt,
				_workerId,
				exception.ToString(),
				nextRetryAt,
				stoppingToken
			);

			var duration = _timeProvider.GetElapsedTime(started);
			JobTelemetry.Failed(record.JobName, record.QueueName, duration);
			activity?.SetStatus(ActivityStatusCode.Error, exception.Message);

			if (retry)
			{
				JobTelemetry.Retried(record.JobName, record.QueueName);
				JobWillRetry(exception, nextRetryAt);
			}
			else
			{
				JobExhaustedAttempts(exception, definition.MaxAttempts);
			}
		}
		finally
		{
			_state.FinishExecution(workerId);
			JobTelemetry.ExecutionFinished();
			Release(record);
		}
	}

	/// <remarks>
	///	    NB: If higher-priority jobs complete while this method is running, lower priority jobs may get requested
	///	    before the higher-priority ones; this is a known race-condition, and effect should be rare enough to be
	///	    acceptable. This is a future research point if effect is more pronounced than currently envisioned.
	/// </remarks>
	private JobAcquisitionRequest? BuildAcquisitionRequest()
	{
		var capacity = Math.Min(
			_options.AcquisitionBatchSize,
			_options.MaxAcquisitionCount - Volatile.Read(ref _reservations)
		);

		if (capacity <= 0)
			return null;

		var queues = new List<JobQueueAcquisition>();

		foreach (var (priority, priorityQueues) in _queuesByPriority)
		{
			foreach (var (queue, definitions) in priorityQueues)
			{
				var queueCapacity = queue.Concurrency switch
				{
					0 => capacity,
					_ => queue.Concurrency - _queueReservations.GetValueOrDefault(queue.Name),
				};

				if (queueCapacity <= 0)
					continue;

				var jobCapacities = definitions
					.Select(definition => new
					{
						definition.Name,
						Capacity = GetJobAcquisitionCapacity(definition, capacity),
					})
					.Where(item => item.Capacity > 0)
					.ToDictionary(item => item.Name, item => item.Capacity, StringComparer.Ordinal);

				if (jobCapacities.Count == 0)
					continue;

				queues.Add(new()
				{
					QueueName = queue.Name,
					Capacity = (int)Math.Min(queueCapacity, jobCapacities.Values.Sum(static value => (long)value)),
					JobCapacities = jobCapacities,
				});
			}

			// rotate to ensure fairness across queues at same priority
			priorityQueues.Enqueue(priorityQueues.Dequeue());
		}

		if (queues.Count == 0)
			return null;

		return new JobAcquisitionRequest()
		{
			WorkerId = _workerId,
			Lease = _options.LeaseDuration,
			BatchSize = capacity,
			Queues = queues,
			FairQueues = _fairQueueOptions.ToPolicy(),
		};
	}

	private int GetJobAcquisitionCapacity(JobDefinition definition, int availableCapacity)
	{
		// we trust MaterializeRecurringAsync to ensure queue and skip to have only one outstanding
		return Math.Max(
			definition.MaxConcurrency switch
			{
				0 => availableCapacity,
				_ => Math.Min(definition.MaxConcurrency - _jobReservations.GetValueOrDefault(definition.Name), availableCapacity),
			},
			0
		);
	}

	private void WarnIfGroupedJobsAreInert(IReadOnlyList<JobRecord> acquired)
	{
		if (_fairQueueOptions.Enabled
			|| Volatile.Read(ref _fairQueuesDisabledWarningLogged) != 0
			|| !acquired.Any(static job => job.GroupId is not null)
			|| Interlocked.Exchange(ref _fairQueuesDisabledWarningLogged, 1) != 0)
		{
			return;
		}

		GroupedJobsAcquiredWithoutFairQueues();
	}

	private void Reserve(JobRecord record)
	{
		Interlocked.Increment(ref _reservations);
		_openLeases[(record.JobHandle, record.Attempt)] = new(record, _timeProvider.GetUtcNow() + LeaseRenewalInterval);
		_queueReservations.AddOrUpdate(record.QueueName, 1, static (_, count) => count + 1);
		_jobReservations.AddOrUpdate(record.JobName, 1, static (_, count) => count + 1);
	}

	private void Release(JobRecord record)
	{
		Interlocked.Decrement(ref _reservations);
		_ = _openLeases.TryRemove((record.JobHandle, record.Attempt), out _);
		_queueReservations.AddOrUpdate(record.QueueName, 0, static (_, count) => Math.Max(0, count - 1));
		_jobReservations.AddOrUpdate(record.JobName, 0, static (_, count) => Math.Max(0, count - 1));
	}

	private async Task<bool> RenewLeaseAsync(JobRecord record, CancellationToken cancellationToken)
	{
		try
		{
			await _storage.RenewLeaseAsync(
				record.JobHandle,
				record.Attempt,
				_workerId,
				_options.LeaseDuration,
				cancellationToken
			);
			return true;
		}
#pragma warning disable CA1031 // There is no catcher above us to safely report exceptions
		catch (Exception exception)
#pragma warning restore CA1031
		{
			LeaseRenewalFailed(exception, record.JobHandle, record.Attempt);
			return false;
		}
	}

	private TimeSpan LeaseRenewalInterval =>
		TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, _options.LeaseDuration.Ticks / 3));

	private TimeSpan HeartbeatInterval =>
		TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, _options.ServerTimeout.Ticks / 3));

	private sealed record OpenLease(JobRecord Record, DateTimeOffset InitialRenewal)
	{
		public DateTimeOffset NextRenewal { get; set; } = InitialRenewal;
	}

	private async Task InitializeAsync(CancellationToken cancellationToken)
	{
		if (_initialized)
			return;

		if (_storage is not IRecurringJobStorage recurringStorage)
		{
			_initialized = true;
			return;
		}

		await _scheduleInitialization.WaitAsync(cancellationToken);
		try
		{
			if (_initialized)
				return;

			var now = _timeProvider.GetUtcNow();

			await recurringStorage.MergeRecurringSchedulesListAsync(
				_definitions.Values
					.Where(d => d.Cron is not null)
					.Select(d => new RecurringJobSchedule
					{
						Name = d.Name,
						JobName = d.Name,
						QueueName = d.Queue.Name,
						Cron = d.Cron!,
						TimeZone = d.TimeZone,
						IsCodeDefined = true,
						NextRunAt = GetNextOccurrence(
							now,
							d.Cron!,
							d.TimeZone,
							d.Name
						),
					})
					.ToList(),
				cancellationToken
			);

			_initialized = true;
		}
		finally
		{
			_scheduleInitialization.Release();
		}
	}

	private async Task MaterializeRecurringAsync(CancellationToken cancellationToken)
	{
		if (_storage is not IRecurringJobStorage recurringStorage)
			return;

		var now = _timeProvider.GetUtcNow();
		var schedules = await recurringStorage.GetDueRecurringAsync(now, _options.AcquisitionBatchSize, cancellationToken);

		foreach (var schedule in schedules)
		{
			if (!_definitions.TryGetValue(schedule.JobName, out var definition))
				continue;

			try
			{
				await MaterializeRecurringScheduleAsync(
					recurringStorage,
					schedule,
					definition,
					now,
					cancellationToken
				);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
#pragma warning disable CA1031 // One malformed schedule must not block unrelated schedules or job acquisition.
			catch (Exception exception)
#pragma warning restore CA1031
			{
				RecurringMaterializationFailed(exception, schedule.Name);
			}
		}
	}

	private static DateTimeOffset GetNextOccurrence(DateTimeOffset from, string cron, string timeZone, string jobName)
	{
		var expression = JobCron.Parse(cron);
		var tzi = JobCron.GetTimeZone(timeZone);
		return expression.GetNextOccurrence(from, tzi, inclusive: false) switch
		{
			{ } next => next,
			_ => throw new ImmediateJobException($"Recurring schedule '{jobName}' has no future occurrence."),
		};
	}

	private static List<DateTimeOffset> GetNextOccurrences(
		RecurringJobSchedule schedule,
		MisfireHandlingMode misfireHandlingMode,
		DateTimeOffset now
	)
	{
		var recurrenceTimes = new List<DateTimeOffset> { schedule.NextRunAt };

		if (misfireHandlingMode == MisfireHandlingMode.EnqueueAll)
		{
			while (recurrenceTimes[^1] <= now)
			{
				recurrenceTimes.Add(
					GetNextOccurrence(
						recurrenceTimes[^1],
						schedule.Cron,
						schedule.TimeZone,
						schedule.Name
					)
				);
			}
		}
		else
		{
			var next = schedule.NextRunAt;

			while (next < now)
			{
				next = GetNextOccurrence(
					next,
					schedule.Cron,
					schedule.TimeZone,
					schedule.Name
				);
			}

			recurrenceTimes.Add(next);

			if (next == now)
			{
				recurrenceTimes.Add(
					GetNextOccurrence(
						next,
						schedule.Cron,
						schedule.TimeZone,
						schedule.Name
					)
				);
			}
		}

		return recurrenceTimes;
	}

	private async Task MaterializeRecurringScheduleAsync(
		IRecurringJobStorage recurringStorage,
		RecurringJobSchedule schedule,
		JobDefinition definition,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var recurrenceTimes = GetNextOccurrences(schedule, definition.MisfireHandlingMode, now);

		var nextIndex = 1;
		var next = recurrenceTimes[nextIndex];
		var dueAt = schedule.NextRunAt;

		if (schedule.NextRunAt < now)
		{
			var missedCount = recurrenceTimes[^2] == now
				? recurrenceTimes.Count - 2
				: recurrenceTimes.Count - 1;

			var lastMissedAt = recurrenceTimes[missedCount - 1];
			var nextAfterMisfires = recurrenceTimes[^1];

			RecurringOccurrencesMissed(
				schedule.Name,
				missedCount,
				schedule.NextRunAt,
				lastMissedAt,
				definition.MisfireHandlingMode
			);

			switch (definition.MisfireHandlingMode)
			{
				case MisfireHandlingMode.EnqueueNone:
				{
					if (recurrenceTimes[^2] == now)
						goto case MisfireHandlingMode.EnqueueOne;

					schedule = schedule with
					{
						NextRunAt = nextAfterMisfires,
					};

					await recurringStorage.UpsertRecurringAsync(schedule, cancellationToken);

					return;
				}

				case MisfireHandlingMode.EnqueueOne:
				{
					dueAt = now;
					nextIndex = recurrenceTimes.Count - 1;
					next = nextAfterMisfires;
					break;
				}

				case MisfireHandlingMode.EnqueueAll:
				default:
					break;
			}
		}

		while (true)
		{
			var (traceParent, traceState) = Activity.Current;
			var record = new JobRecord
			{
				JobHandle = JobHandle.FromString(_idGenerator.CreateId(IdKind.Job)),
				JobName = schedule.JobName,
				QueueName = definition.Queue.Name,
				Payload = "{}",
				State = JobState.Pending,
				DueAt = dueAt,
				CreatedAt = now,
				RecurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{schedule.NextRunAt.UtcTicks}"),
				TraceParent = traceParent,
				TraceState = traceState,
			};

			switch (definition.OverlapPolicy)
			{
				case OverlapPolicy.Skip:
				{
					var jobs = await _storage.QueryNonCompletedJobsAsync(definition.Name, cancellationToken);

					if (jobs.Count != 0)
						record = record with { State = JobState.Skipped, CompletedAt = now };

					if (await recurringStorage.MaterializeRecurringAsync(schedule, record, next, dependencies: null, cancellationToken)
						&& record.State == JobState.Pending)
					{
						JobTelemetry.Enqueued(record.JobName, record.QueueName);
					}

					break;
				}

				case OverlapPolicy.Queue:
				{
					if (recurringStorage is not IJobGraphStorage)
						throw new ImmediateJobException("Unable to queue recurring job without graph support.");

					var jobs = await _storage.QueryNonCompletedJobsAsync(definition.Name, cancellationToken);

					if (jobs.Count != 0)
					{
						record = record with
						{
							State = JobState.AwaitingContinuation,
							CompletedAt = null,
							RemainingDependencies = 1,
						};
					}

					var dependency = jobs
						.OrderByDescending(j => j.DueAt)
						.Take(1)
						.Select(d => new JobContinuationEdge
						{
							ChildJobHandle = record.JobHandle,
							ParentJobHandle = d.JobHandle,
							Delay = TimeSpan.Zero,
						})
						.ToList();

					if (await recurringStorage.MaterializeRecurringAsync(
							schedule,
							record,
							next,
							dependency,
							cancellationToken
						)
						&& record.State == JobState.Pending)
					{
						JobTelemetry.Enqueued(record.JobName, record.QueueName);
					}

					break;
				}

				case OverlapPolicy.Concurrent:
				default:
				{

					if (await recurringStorage.MaterializeRecurringAsync(schedule, record, next, dependencies: null, cancellationToken))
						JobTelemetry.Enqueued(record.JobName, record.QueueName);

					break;
				}
			}

			if (next > now)
				break;

			schedule = schedule with { NextRunAt = next };
			dueAt = schedule.NextRunAt;
			next = recurrenceTimes[++nextIndex];
		}
	}

	private static TimeSpan GetRetryDelay(JobDefinition definition, int attempt)
	{
		if (definition.Backoff == BackoffStrategy.Fixed)
			return definition.BackoffBase;

		var exponent = Math.Min(30, Math.Max(0, attempt - 1));
		var ticks = definition.BackoffBase.Ticks * Math.Pow(2, exponent);
		if (definition.Backoff == BackoffStrategy.ExponentialJitter)
			ticks *= 0.5 + Random.Shared.NextDouble();

		// long.MaxValue converts to 2^63 as a double, which is one past the representable range, so the
		// bound has to be tested before the cast rather than clamped with Math.Min after it.
		return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
	}

	/// <inheritdoc />
	public override void Dispose()
	{
		_scheduleInitialization.Dispose();
		_workerCancellation.Dispose();
		base.Dispose();
	}

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingSchedulerIterationFailed,
		EventName = "Immediate.Jobs.Shared.SchedulerIterationFailed",
		Level = LogLevel.Error, Message = "Immediate.Jobs {loopName} loop iteration failed; the loop will continue")]
	private partial void SchedulerIterationFailed(Exception exception, string loopName);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingShutdownDrainExceeded,
		EventName = "Immediate.Jobs.Shared.ShutdownDrainExceeded",
		Level = LogLevel.Warning, Message = "Immediate.Jobs shutdown drain exceeded {shutdownTimeout}")]
	private partial void ShutdownDrainExceeded(TimeSpan shutdownTimeout);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingUnhandledWorkerError,
		EventName = "Immediate.Jobs.Shared.UnhandledWorkerError",
		Level = LogLevel.Error, Message = "Unhandled worker error for job {jobHandle}; its lease will expire")]
	private partial void UnhandledWorkerError(Exception exception, JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingJobCompleted,
		EventName = "Immediate.Jobs.Shared.JobCompleted",
		Level = LogLevel.Information, Message = "Job completed in {durationMs} ms")]
	private partial void JobCompleted(double durationMs);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingJobWillRetry,
		EventName = "Immediate.Jobs.Shared.JobWillRetry",
		Level = LogLevel.Warning, Message = "Job failed and will retry at {nextRetryAt}")]
	private partial void JobWillRetry(Exception exception, DateTimeOffset? nextRetryAt);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingJobExhaustedAttempts,
		EventName = "Immediate.Jobs.Shared.JobExhaustedAttempts",
		Level = LogLevel.Error, Message = "Job exhausted all {maxAttempts} attempts")]
	private partial void JobExhaustedAttempts(Exception exception, int maxAttempts);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingGraphFeaturesDisabled,
		EventName = "Immediate.Jobs.Shared.GraphFeaturesDisabled",
		Level = LogLevel.Information,
		Message = "Batch & continuation features are disabled: the configured storage '{storageType}' implements the queue capability only. Configure a SQL provider to enable them."
	)]
	private partial void GraphFeaturesDisabled(string storageType);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingRecurringJobFeaturesDisabled,
		EventName = "Immediate.Jobs.Shared.RecurringJobFeaturesDisabled",
		Level = LogLevel.Information,
		Message = "Recurring job features are disabled: the configured storage '{storageType}' implements the queue capability only. Configure a SQL provider to enable them."
	)]
	private partial void RecurringJobFeaturesDisabled(string storageType);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingGroupedJobsAcquiredWithoutFairQueues,
		EventName = "Immediate.Jobs.Shared.GroupedJobsAcquiredWithoutFairQueues",
		Level = LogLevel.Warning,
		Message = "Grouped jobs were acquired while fair queues are disabled. Their group ids are persisted but do not affect dispatch order; call UseFairQueues() to enable fair acquisition."
	)]
	private partial void GroupedJobsAcquiredWithoutFairQueues();

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingExecutionTelemetryPersistenceFailed,
		EventName = "Immediate.Jobs.Shared.ExecutionTelemetryPersistenceFailed",
		Level = LogLevel.Warning,
		Message = "Could not persist execution telemetry; job invocation will continue"
	)]
	private partial void ExecutionTelemetryPersistenceFailed(Exception exception);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingLeaseRenewalFailed,
		EventName = "Immediate.Jobs.Shared.LeaseRenewalFailed",
		Level = LogLevel.Warning,
		Message = "Could not renew the lease for job {jobHandle} execution {executionNumber}; renewal will be retried until the attempt finishes"
	)]
	private partial void LeaseRenewalFailed(
		Exception exception,
		JobHandle jobHandle,
		int executionNumber
	);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingRecurringMaterializationFailed,
		EventName = "Immediate.Jobs.Shared.RecurringMaterializationFailed",
		Level = LogLevel.Error,
		Message = "Could not materialize recurring schedule {scheduleName}; other schedules and job acquisition will continue"
	)]
	private partial void RecurringMaterializationFailed(
		Exception exception,
		string scheduleName
	);

	[LoggerMessage(
		EventId = LibraryEventIds.JobSchedulingRecurringOccurrencesMissed,
		EventName = "Immediate.Jobs.Shared.RecurringOccurrencesMissed",
		Level = LogLevel.Information,
		Message = "Recurring schedule {scheduleName} missed {missedCount} occurrences from {firstMissedAt} through {lastMissedAt}; applying {misfireHandlingMode}"
	)]
	private partial void RecurringOccurrencesMissed(
		string scheduleName,
		int missedCount,
		DateTimeOffset firstMissedAt,
		DateTimeOffset lastMissedAt,
		MisfireHandlingMode misfireHandlingMode
	);
}
