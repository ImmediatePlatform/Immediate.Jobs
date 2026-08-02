using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Coordinates recurring schedules, durable leases, and the bounded worker pool.
/// </summary>
public sealed partial class JobSchedulingService : BackgroundService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IJobStorage _storage;
	private readonly IRecurringJobStorage? _recurringStorage;
	private readonly IJobGraphStorage? _graphStorage;
	private readonly ImmediateJobsOptions _options;
	private readonly FairQueuePolicy? _fairQueuePolicy;
	private readonly TimeProvider _timeProvider;
	private readonly IIdGenerator _idGenerator;
	private readonly ILogger<JobSchedulingService> _logger;
	private readonly JobSchedulerState _state;
	private readonly IReadOnlyDictionary<string, JobDefinition> _definitions;
	private readonly IReadOnlyDictionary<string, JobQueueDefinition> _queues;
	private readonly ConcurrentDictionary<string, int> _queueReservations = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, int> _jobReservations = new(StringComparer.Ordinal);
	private readonly Dictionary<int, int> _priorityOffsets = [];
	private readonly SemaphoreSlim _scheduleInitialization = new(1, 1);
	private readonly CancellationTokenSource _workerCancellation = new();
	private readonly string _workerId = string.Create(CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");
	private readonly Channel<JobRecord> _channel;
	private int _reservations;
	private int _fairQueuesDisabledWarningLogged;
	private long _nextPurgeTimestamp;

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
	/// <param name="queueDefinitions">
	/// 	The configured queue definitions.
	/// </param>
	/// <param name="options">
	/// 	The scheduler runtime options.
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
		IEnumerable<JobQueueDefinition> queueDefinitions,
		ImmediateJobsOptions options,
		TimeProvider timeProvider,
		IIdGenerator idGenerator,
		ILogger<JobSchedulingService> logger,
		JobSchedulerState state
	)
	{
		ArgumentNullException.ThrowIfNull(scopeFactory);
		ArgumentNullException.ThrowIfNull(storage);
		ArgumentNullException.ThrowIfNull(definitions);
		ArgumentNullException.ThrowIfNull(queueDefinitions);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(idGenerator);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(state);

		_scopeFactory = scopeFactory;
		_storage = storage;
		_recurringStorage = storage as IRecurringJobStorage;
		_graphStorage = storage as IJobGraphStorage;
		_options = options;
		_fairQueuePolicy = options.FairQueues?.ToPolicy();
		_timeProvider = timeProvider;
		_idGenerator = idGenerator;
		_logger = logger;
		_state = state;
		if (_graphStorage is null)
			GraphFeaturesDisabled(_logger, storage.GetType().Name);
		_definitions = definitions.ToDictionary(x => x.Name, StringComparer.Ordinal);
		_queues = queueDefinitions
			.Concat(_definitions.Values.Select(static definition => definition.Queue))
			.Append(JobQueueDefinition.Default)
			.GroupBy(static queue => queue.Name, StringComparer.Ordinal)
			.ToDictionary(
				static group => group.Key,
				static group => group.Distinct().Single(),
				StringComparer.Ordinal
			);
		// Reservation accounting in BuildAcquisitionRequest is the admission control, so the channel is
		// only a handoff buffer. A bounded channel would add a second, redundant limit whose sole effect
		// is to block the scheduler loop -- and with it the heartbeat -- if the two ever disagree.
		_channel = Channel.CreateUnbounded<JobRecord>(new UnboundedChannelOptions
		{
			SingleWriter = true,
			SingleReader = options.MaxParallelJobs == 1,
		});
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await _storage.InitializeAsync(stoppingToken).ConfigureAwait(false);
		await EnsureCodeSchedulesAsync(stoppingToken).ConfigureAwait(false);
		_state.MarkStarted(_timeProvider.GetUtcNow());

		// Workers observe _workerCancellation rather than stoppingToken: shutdown completes the channel so
		// buffered records still drain, and only an exceeded drain deadline cancels a running job.
		var workers = Enumerable.Range(0, _options.MaxParallelJobs)
			.Select(_ => RunWorkerAsync(_workerCancellation.Token))
			.ToArray();

		try
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await RunSchedulerIterationAsync(stoppingToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
#pragma warning disable CA1031 // A scheduler iteration failure must not terminate the hosted service.
				catch (Exception exception)
#pragma warning restore CA1031
				{
					SchedulerIterationFailed(_logger, exception);
				}

				await Task.Delay(_options.PollingInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
			}
		}
		finally
		{
			_ = _channel.Writer.TryComplete();
			try
			{
				// stoppingToken is already cancelled here; forwarding it would abort the drain immediately.
				await Task.WhenAll(workers)
					.WaitAsync(_options.ShutdownTimeout, _timeProvider, CancellationToken.None)
					.ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				ShutdownDrainExceeded(_logger, _options.ShutdownTimeout);
			}
			finally
			{
				await _workerCancellation.CancelAsync().ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// 	Executes one already-acquired record. Intended for deterministic test harnesses.
	/// </summary>
	/// <param name="record">
	/// 	The acquired job record to execute.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel execution.
	/// </param>
	/// <returns>
	/// 	A task that completes when the job attempt finishes.
	/// </returns>
	public ValueTask ExecuteSingleAsync(JobRecord record, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(record);
		return ExecuteJobAsync(record, cancellationToken);
	}

	/// <summary>
	/// Materializes and executes all work currently due, returning when the due queue is empty.
	/// Delayed work is left in storage. This method is intended for deterministic test harnesses.
	/// 
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel draining.
	/// </param>
	/// <returns>
	/// 	A task that completes when no currently due work remains.
	/// </returns>
	public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
	{
		await _storage.InitializeAsync(cancellationToken).ConfigureAwait(false);
		await EnsureCodeSchedulesAsync(cancellationToken).ConfigureAwait(false);
		while (true)
		{
			await MaterializeRecurringAsync(cancellationToken).ConfigureAwait(false);
			var request = BuildAcquisitionRequest();
			if (request is null)
				return;
			var jobs = await _storage.AcquireDueJobsAsync(request, cancellationToken).ConfigureAwait(false);
			if (jobs.Count == 0)
				return;
			WarnIfGroupedJobsAreInert(jobs);

			foreach (var job in jobs)
			{
				Reserve(job);
				await ExecuteJobAsync(job, cancellationToken, releaseReservation: true).ConfigureAwait(false);
			}
		}
	}

	private async Task RunSchedulerIterationAsync(CancellationToken cancellationToken)
	{
		// The heartbeat runs first so that a failure in any later stage cannot make a polling scheduler
		// look dead to ImmediateJobsHealthCheck.
		var now = _timeProvider.GetUtcNow();
		await _storage.HeartbeatAsync(
			new JobServerSnapshot { WorkerId = _workerId, LastHeartbeat = now, ActiveWorkers = _state.ActiveWorkers, MaxWorkers = _options.MaxParallelJobs },
			cancellationToken
		).ConfigureAwait(false);
		_state.MarkHeartbeat(now);

		await MaterializeRecurringAsync(cancellationToken).ConfigureAwait(false);
		var request = BuildAcquisitionRequest();
		var acquired = request is null
			? []
			: await _storage.AcquireDueJobsAsync(request, cancellationToken).ConfigureAwait(false);
		WarnIfGroupedJobsAreInert(acquired);

		foreach (var job in acquired)
		{
			Reserve(job);
			try
			{
				JobTelemetry.Acquired();
				await _channel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				Release(job);
				throw;
			}
		}

		if (_timeProvider.GetTimestamp() >= Interlocked.Read(ref _nextPurgeTimestamp))
		{
			await _storage.PurgeJobsAsync(
				_options.SucceededRetention,
				_options.FailedRetention,
				cancellationToken
			).ConfigureAwait(false);
			if (_graphStorage is not null)
			{
				await _graphStorage.PurgeBatchesAsync(
					_options.BatchSucceededRetention,
					_options.BatchFailedRetention,
					cancellationToken
				).ConfigureAwait(false);
			}

			_ = Interlocked.Exchange(ref _nextPurgeTimestamp, _timeProvider.GetTimestamp() + ToTimestampTicks(_options.PurgeInterval));
		}
	}

	private async Task RunWorkerAsync(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var record in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
			{
				try
				{
					await ExecuteJobAsync(record, cancellationToken, releaseReservation: true).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					break;
				}
#pragma warning disable CA1031 // A failed job must not terminate its worker loop.
				catch (Exception exception)
#pragma warning restore CA1031
				{
					UnhandledWorkerError(_logger, exception, record.Id);
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
		JobRecord record,
		CancellationToken stoppingToken,
		bool releaseReservation = false
	)
	{
		if (!_definitions.TryGetValue(record.JobName, out var definition))
		{
			try
			{
				await _storage
					.FailAsync(
						record.Id,
						record.Attempt,
						_workerId,
						$"No generated job definition exists for '{record.JobName}'.",
						nextRetryAt: null,
						stoppingToken
					)
					.ConfigureAwait(false);
			}
			finally
			{
				if (releaseReservation)
					Release(record);
			}

			return;
		}

		var started = _timeProvider.GetTimestamp();
		var startedAt = _timeProvider.GetUtcNow();
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
		var timeoutTimer = definition.Timeout is { } timeoutValue
			? _timeProvider.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), timeout, timeoutValue, Timeout.InfiniteTimeSpan)
			: null;
		using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
		var leaseTask = RenewLeaseLoopAsync(record.Id, record.Attempt, leaseCancellation.Token);

		var parent = default(ActivityContext);
		if (record.TraceParent is not null)
			_ = ActivityContext.TryParse(record.TraceParent, record.TraceState, isRemote: true, out parent);
		IEnumerable<ActivityLink>? links = parent != default ? [new(parent)] : null;
		using var activity = JobTelemetry.ActivitySource.StartActivity(
			$"job {record.JobName}",
			ActivityKind.Consumer,
			default(ActivityContext),
			tags:
			[
				new("job.name", record.JobName),
				new("job.queue", record.QueueName),
				new("job.id", record.Id),
				new("job.attempt", record.Attempt),
			],
			links: links
		);
		using var logScope = _logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
		{
			["JobName"] = record.JobName,
			["QueueName"] = record.QueueName,
			["JobId"] = record.Id,
			["Attempt"] = record.Attempt,
		});

		// Paired with DecrementActive/ExecutionFinished in the finally below, so nothing that can throw
		// may sit between this and the try.
		_state.IncrementActive();
		JobTelemetry.ExecutionStarted();

		try
		{
			try
			{
				await _storage.SetExecutionTelemetryAsync(
					record.Id,
					record.Attempt,
					_workerId,
					activity?.TraceId.ToString(),
					activity?.SpanId.ToString(),
					startedAt,
					stoppingToken
				).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				throw;
			}
#pragma warning disable CA1031 // Telemetry persistence is best-effort and must not consume a job attempt.
			catch (Exception exception)
#pragma warning restore CA1031
			{
				ExecutionTelemetryPersistenceFailed(_logger, exception);
			}

			await using var scope = _scopeFactory.CreateAsyncScope();

			var executionBuffer = new JobExecutionBuffer();
			await definition.Invoker.InvokeAsync(
				scope.ServiceProvider,
				new JobExecution { Record = record, Definition = definition, CancellationToken = timeout.Token, Buffer = executionBuffer }
			).ConfigureAwait(false);
			if (_graphStorage is not null)
			{
				await _graphStorage.CompleteWithContinuationsAsync(
					record.Id,
					record.Attempt,
					_workerId,
					executionBuffer.SealAndSnapshot(),
					stoppingToken
				).ConfigureAwait(false);
			}
			else
			{
				await _storage.CompleteAsync(record.Id, record.Attempt, _workerId, stoppingToken).ConfigureAwait(false);
			}

			var duration = _timeProvider.GetElapsedTime(started);
			JobTelemetry.Succeeded(record.JobName, record.QueueName, duration);
			_ = activity?.SetStatus(ActivityStatusCode.Ok);
			JobCompleted(_logger, duration.TotalMilliseconds);
		}
		catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
		{
			var retry = record.Attempt < definition.MaxAttempts;
			DateTimeOffset? nextRetryAt = retry ? _timeProvider.GetUtcNow() + GetRetryDelay(definition, record.Attempt) : null;
			await _storage.FailAsync(
				record.Id,
				record.Attempt,
				_workerId,
				exception.ToString(),
				nextRetryAt,
				stoppingToken
			).ConfigureAwait(false);
			var duration = _timeProvider.GetElapsedTime(started);
			JobTelemetry.Failed(record.JobName, record.QueueName, duration);
			_ = activity?.SetStatus(ActivityStatusCode.Error, exception.Message);

			if (retry)
			{
				JobTelemetry.Retried(record.JobName, record.QueueName);
				JobWillRetry(_logger, exception, nextRetryAt);
			}
			else
			{
				JobExhaustedAttempts(_logger, exception, definition.MaxAttempts);
			}
		}
		finally
		{
			timeoutTimer?.Dispose();
			await leaseCancellation.CancelAsync().ConfigureAwait(false);
			try
			{
				await leaseTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}

			_state.DecrementActive();
			JobTelemetry.ExecutionFinished();
			if (releaseReservation)
				Release(record);
		}
	}

	private JobAcquisitionRequest? BuildAcquisitionRequest()
	{
		var capacity = Math.Min(
			_options.AcquisitionBatchSize,
			_options.MaxParallelJobs - Volatile.Read(ref _reservations)
		);
		if (capacity <= 0)
			return null;

		var queues = new List<JobQueueAcquisition>();
		foreach (var priorityGroup in _queues.Values
			.GroupBy(static queue => queue.Priority)
			.OrderByDescending(static group => group.Key))
		{
			var priorityQueues = priorityGroup.OrderBy(static queue => queue.Name, StringComparer.Ordinal).ToArray();
			var offset = _priorityOffsets.GetValueOrDefault(priorityGroup.Key) % priorityQueues.Length;
			for (var index = 0; index < priorityQueues.Length; index++)
			{
				var queue = priorityQueues[(index + offset) % priorityQueues.Length];
				var queueCapacity = queue.Concurrency == 0
					? capacity
					: queue.Concurrency - _queueReservations.GetValueOrDefault(queue.Name);
				if (queueCapacity <= 0)
					continue;

				var jobCapacities = _definitions.Values
					.Select(definition => new
					{
						definition.Name,
						Capacity = GetJobAcquisitionCapacity(definition, capacity),
					})
					.Where(static item => item.Capacity > 0)
					.ToDictionary(static item => item.Name, static item => item.Capacity, StringComparer.Ordinal);
				if (jobCapacities.Count == 0)
					continue;

				queues.Add(new()
				{
					QueueName = queue.Name,
					Capacity = (int)Math.Min(queueCapacity, jobCapacities.Values.Sum(static value => (long)value)),
					JobCapacities = jobCapacities,
				});
			}

			_priorityOffsets[priorityGroup.Key] = offset + 1;
		}

		return queues.Count == 0
			? null
			: new()
			{
				WorkerId = _workerId,
				Lease = _options.LeaseDuration,
				BatchSize = capacity,
				Queues = queues,
				FairQueues = _fairQueuePolicy,
			};
	}

	private int GetJobAcquisitionCapacity(JobDefinition definition, int availableCapacity)
	{
		var limit = definition.OverlapPolicy == OverlapPolicy.Queue ? 1 : definition.MaxConcurrency;
		return limit == 0
			? availableCapacity
			: limit - _jobReservations.GetValueOrDefault(definition.Name);
	}

	private void WarnIfGroupedJobsAreInert(IReadOnlyList<JobRecord> acquired)
	{
		if (_fairQueuePolicy is not null
			|| Volatile.Read(ref _fairQueuesDisabledWarningLogged) != 0
			|| !acquired.Any(static job => job.GroupId is not null)
			|| Interlocked.Exchange(ref _fairQueuesDisabledWarningLogged, 1) != 0)
		{
			return;
		}

		GroupedJobsAcquiredWithoutFairQueues(_logger);
	}

	private void Reserve(JobRecord record)
	{
		_ = Interlocked.Increment(ref _reservations);
		_ = _queueReservations.AddOrUpdate(record.QueueName, 1, static (_, count) => count + 1);
		_ = _jobReservations.AddOrUpdate(record.JobName, 1, static (_, count) => count + 1);
	}

	private void Release(JobRecord record)
	{
		_ = Interlocked.Decrement(ref _reservations);
		_ = _queueReservations.AddOrUpdate(record.QueueName, 0, static (_, count) => Math.Max(0, count - 1));
		_ = _jobReservations.AddOrUpdate(record.JobName, 0, static (_, count) => Math.Max(0, count - 1));
	}

	private async Task RenewLeaseLoopAsync(string jobId, int executionNumber, CancellationToken cancellationToken)
	{
		var interval = TimeSpan.FromTicks(Math.Max(1, _options.LeaseDuration.Ticks / 3));
		while (true)
		{
			await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
			try
			{
				await _storage.RenewLeaseAsync(
					jobId,
					executionNumber,
					_workerId,
					_options.LeaseDuration,
					cancellationToken
				).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
#pragma warning disable CA1031 // A transient renewal failure must not stop later renewals or the job outcome.
			catch (Exception exception)
#pragma warning restore CA1031
			{
				LeaseRenewalFailed(_logger, exception, jobId, executionNumber);
			}
		}
	}

	private async Task AssertCodeSchedulesAsync(CancellationToken cancellationToken)
	{
		var recurringStorage = _recurringStorage;
		if (recurringStorage is null)
			return;

		var now = _timeProvider.GetUtcNow();
		var codeDefinitions = _definitions.Values.Where(static definition => definition.Cron is not null).ToArray();
		var persisted = codeDefinitions.Length == 0
			? []
			: (await _storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false))
				.Recurring
				.ToDictionary(static schedule => schedule.Name, StringComparer.Ordinal);
		foreach (var definition in codeDefinitions)
		{
			var zone = JobCron.GetTimeZone(definition.TimeZone);
			// An unchanged schedule keeps the occurrence it is already waiting for, even when that
			// occurrence is already due: recomputing from now would silently drop an occurrence that
			// fell between shutdown and restart. Only a changed cron or time zone recomputes.
			var next = persisted.GetValueOrDefault(definition.Name) is { } current
				&& string.Equals(current.Cron, definition.Cron, StringComparison.Ordinal)
				&& string.Equals(current.TimeZone, definition.TimeZone, StringComparison.Ordinal)
					? current.NextRunAt
					: JobCron.Parse(definition.Cron!).GetNextOccurrence(now, zone)
						?? throw new ImmediateJobException($"Cron for '{definition.Name}' has no future occurrence.");
			await recurringStorage.UpsertRecurringAsync(
				new()
				{
					Name = definition.Name,
					JobName = definition.Name,
					Cron = definition.Cron!,
					TimeZone = definition.TimeZone,
					IsCodeDefined = true,
					NextRunAt = next,
				},
				cancellationToken
			).ConfigureAwait(false);
		}

		var activeScheduleNames = codeDefinitions.Select(static definition => definition.Name).ToArray();
		await recurringStorage.RemoveObsoleteCodeDefinedRecurringAsync(
			activeScheduleNames,
			cancellationToken
		).ConfigureAwait(false);
	}

	private async Task EnsureCodeSchedulesAsync(CancellationToken cancellationToken)
	{
		if (_state.CodeSchedulesAsserted)
			return;
		if (_recurringStorage is null)
		{
			_state.MarkCodeSchedulesAsserted();
			return;
		}

		await _scheduleInitialization.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_state.CodeSchedulesAsserted)
				return;
			await AssertCodeSchedulesAsync(cancellationToken).ConfigureAwait(false);
			_state.MarkCodeSchedulesAsserted();
		}
		finally
		{
			_ = _scheduleInitialization.Release();
		}
	}

	private async Task MaterializeRecurringAsync(CancellationToken cancellationToken)
	{
		var recurringStorage = _recurringStorage;
		if (recurringStorage is null)
			return;

		var now = _timeProvider.GetUtcNow();
		var schedules = await recurringStorage.GetDueRecurringAsync(now, _options.AcquisitionBatchSize, cancellationToken).ConfigureAwait(false);
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
				).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
#pragma warning disable CA1031 // One malformed schedule must not block unrelated schedules or job acquisition.
			catch (Exception exception)
#pragma warning restore CA1031
			{
				RecurringMaterializationFailed(_logger, exception, schedule.Name);
			}
		}
	}

	private async Task MaterializeRecurringScheduleAsync(
		IRecurringJobStorage recurringStorage,
		RecurringJobSchedule schedule,
		JobDefinition definition,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var expression = JobCron.Parse(schedule.Cron);
		var next = expression.GetNextOccurrence(schedule.NextRunAt, JobCron.GetTimeZone(schedule.TimeZone))
			?? throw new ImmediateJobException($"Recurring schedule '{schedule.Name}' has no future occurrence.");
		var (traceParent, traceState) = TraceContextCapture.Current();
		var record = new JobRecord
		{
			Id = _idGenerator.CreateId(IdKind.Job),
			JobName = schedule.JobName,
			QueueName = definition.Queue.Name,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = schedule.NextRunAt,
			CreatedAt = now,
			RecurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{schedule.NextRunAt.UtcTicks}"),
			TraceParent = traceParent,
			TraceState = traceState,
		};

		if (definition.OverlapPolicy == OverlapPolicy.Skip)
		{
			var active = await _storage.QueryJobsAsync(
				new() { State = JobState.Active, JobName = definition.Name, Take = 1 },
				cancellationToken
			).ConfigureAwait(false);
			var pending = await _storage.QueryJobsAsync(
				new() { State = JobState.Pending, JobName = definition.Name, Take = 1 },
				cancellationToken
			).ConfigureAwait(false);
			if (active.Count != 0 || pending.Count != 0)
				record = record with { State = JobState.Skipped, CompletedAt = now };
		}

		if (await recurringStorage.MaterializeRecurringAsync(schedule, record, next, cancellationToken).ConfigureAwait(false)
			&& record.State == JobState.Pending)
		{
			JobTelemetry.Enqueued(record.JobName, record.QueueName);
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

	private long ToTimestampTicks(TimeSpan duration) => (long)(duration.TotalSeconds * _timeProvider.TimestampFrequency);

	/// <inheritdoc />
	public override void Dispose()
	{
		_scheduleInitialization.Dispose();
		_workerCancellation.Dispose();
		base.Dispose();
	}

	[LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Immediate.Jobs scheduler iteration failed; polling will continue")]
	private static partial void SchedulerIterationFailed(ILogger logger, Exception exception);

	[LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Immediate.Jobs shutdown drain exceeded {shutdownTimeout}")]
	private static partial void ShutdownDrainExceeded(ILogger logger, TimeSpan shutdownTimeout);

	[LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Unhandled worker error for job {jobId}; its lease will expire")]
	private static partial void UnhandledWorkerError(ILogger logger, Exception exception, string jobId);

	[LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Job completed in {durationMs} ms")]
	private static partial void JobCompleted(ILogger logger, double durationMs);

	[LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Job failed and will retry at {nextRetryAt}")]
	private static partial void JobWillRetry(ILogger logger, Exception exception, DateTimeOffset? nextRetryAt);

	[LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Job exhausted all {maxAttempts} attempts")]
	private static partial void JobExhaustedAttempts(ILogger logger, Exception exception, int maxAttempts);

	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Information,
		Message = "Batch & continuation features are disabled: the configured storage '{storageType}' implements the queue capability only. Configure a SQL provider to enable them."
	)]
	private static partial void GraphFeaturesDisabled(ILogger logger, string storageType);

	[LoggerMessage(
		EventId = 8,
		Level = LogLevel.Warning,
		Message = "Grouped jobs were acquired while fair queues are disabled. Their group ids are persisted but do not affect dispatch order; call UseFairQueues() to enable fair acquisition."
	)]
	private static partial void GroupedJobsAcquiredWithoutFairQueues(ILogger logger);

	[LoggerMessage(
		EventId = 9,
		Level = LogLevel.Warning,
		Message = "Could not persist execution telemetry; job invocation will continue"
	)]
	private static partial void ExecutionTelemetryPersistenceFailed(ILogger logger, Exception exception);

	[LoggerMessage(
		EventId = 10,
		Level = LogLevel.Warning,
		Message = "Could not renew the lease for job {jobId} execution {executionNumber}; renewal will be retried until the attempt finishes"
	)]
	private static partial void LeaseRenewalFailed(
		ILogger logger,
		Exception exception,
		string jobId,
		int executionNumber
	);

	[LoggerMessage(
		EventId = 11,
		Level = LogLevel.Error,
		Message = "Could not materialize recurring schedule {scheduleName}; other schedules and job acquisition will continue"
	)]
	private static partial void RecurringMaterializationFailed(
		ILogger logger,
		Exception exception,
		string scheduleName
	);
}
