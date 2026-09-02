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
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Coordinates recurring schedules, durable leases, and the bounded worker pool.
/// </summary>
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

		_definitions = definitions
			.ToDictionary(x => x.Name, StringComparer.Ordinal);

		_queues = _definitions
			.Select(d => d.Value.Queue)
			.Distinct()
			.ToDictionary(
				static group => group.Name,
				StringComparer.Ordinal
			);

		// Reservation accounting in BuildAcquisitionRequest is the admission control, so the channel is
		// only a handoff buffer. A bounded channel would add a second, redundant limit whose sole effect
		// is to block the scheduler loop -- and with it the heartbeat -- if the two ever disagree.
		_channel = Channel.CreateUnbounded<JobRecord>(new UnboundedChannelOptions
		{
			SingleWriter = true,
			SingleReader = _options.MaxParallelJobs == 1,
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
		await EnsureCodeSchedulesAsync(stoppingToken);
		_state.MarkStarted(_timeProvider.GetUtcNow());

		// Workers observe _workerCancellation rather than stoppingToken: shutdown completes the channel so
		// buffered records still drain, and only an exceeded drain deadline cancels a running job.
		var workers = Enumerable.Range(0, _options.MaxParallelJobs)
			.Select(_ => RunWorkerAsync(_workerCancellation.Token))
			.ToList();

		try
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await RunSchedulerIterationAsync(stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
#pragma warning disable CA1031 // A scheduler iteration failure must not terminate the hosted service.
				catch (Exception exception)
#pragma warning restore CA1031
				{
					SchedulerIterationFailed(exception);
				}

				await Task.Delay(_options.PollingInterval, _timeProvider, stoppingToken);
			}
		}
		finally
		{
			_ = _channel.Writer.TryComplete();
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
	public async ValueTask ExecuteSingleAsync(JobRecord record, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(record);
		await ExecuteJobAsync(record, cancellationToken);
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
		await TaskScheduler.Yield();
		await _storage.InitializeAsync(cancellationToken);
		await EnsureCodeSchedulesAsync(cancellationToken);
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
				await ExecuteJobAsync(job, cancellationToken, releaseReservation: true);
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
		);
		_state.MarkHeartbeat(now);

		await MaterializeRecurringAsync(cancellationToken);
		var request = BuildAcquisitionRequest();
		var acquired = request is null
			? []
			: await _storage.AcquireDueJobsAsync(request, cancellationToken);
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

		if (_timeProvider.GetTimestamp() >= Interlocked.Read(ref _nextPurgeTimestamp))
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

			_ = Interlocked.Exchange(ref _nextPurgeTimestamp, _timeProvider.GetTimestamp() + ToTimestampTicks(_options.PurgeInterval));
		}
	}

	private async Task RunWorkerAsync(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var record in _channel.Reader.ReadAllAsync(cancellationToken))
			{
				try
				{
					await ExecuteJobAsync(record, cancellationToken, releaseReservation: true);
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
		var leaseTask = RenewLeaseLoopAsync(record.JobHandle, record.Attempt, leaseCancellation.Token);

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
				new("job.id", record.JobHandle),
				new("job.attempt", record.Attempt),
			],
			links: links
		);
		using var logScope = _logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
		{
			["JobName"] = record.JobName,
			["QueueName"] = record.QueueName,
			["JobHandle"] = record.JobHandle,
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
			_ = activity?.SetStatus(ActivityStatusCode.Ok);
			JobCompleted(duration.TotalMilliseconds);
		}
		catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
		{
			var retry = record.Attempt < definition.MaxAttempts;
			DateTimeOffset? nextRetryAt = retry ? _timeProvider.GetUtcNow() + GetRetryDelay(definition, record.Attempt) : null;
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
			_ = activity?.SetStatus(ActivityStatusCode.Error, exception.Message);

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
			timeoutTimer?.Dispose();
			await leaseCancellation.CancelAsync();
			try
			{
				await leaseTask;
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
			var priorityQueues = priorityGroup.OrderBy(static queue => queue.Name, StringComparer.Ordinal).ToList();
			var offset = _priorityOffsets.GetValueOrDefault(priorityGroup.Key) % priorityQueues.Count;
			for (var index = 0; index < priorityQueues.Count; index++)
			{
				var queue = priorityQueues[(index + offset) % priorityQueues.Count];
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
			: new JobAcquisitionRequest()
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
		var limit = definition.OverlapPolicy == OverlapPolicy.Queue ? 1 : definition.MaxConcurrency;
		return limit == 0
			? availableCapacity
			: limit - _jobReservations.GetValueOrDefault(definition.Name);
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

	private async Task RenewLeaseLoopAsync(JobHandle jobHandle, int executionNumber, CancellationToken cancellationToken)
	{
		var interval = TimeSpan.FromTicks(Math.Max(1, _options.LeaseDuration.Ticks / 3));
		while (true)
		{
			await Task.Delay(interval, _timeProvider, cancellationToken);
			try
			{
				await _storage.RenewLeaseAsync(
					jobHandle,
					executionNumber,
					_workerId,
					_options.LeaseDuration,
					cancellationToken
				);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
#pragma warning disable CA1031 // A transient renewal failure must not stop later renewals or the job outcome.
			catch (Exception exception)
#pragma warning restore CA1031
			{
				LeaseRenewalFailed(exception, jobHandle, executionNumber);
			}
		}
	}

	private async Task AssertCodeSchedulesAsync(CancellationToken cancellationToken)
	{
		if (_storage is not IRecurringJobStorage recurringStorage)
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
					NextRunAt = JobCron.Parse(d.Cron!).GetNextOccurrence(now, JobCron.GetTimeZone(d.TimeZone))
						?? throw new ImmediateJobException($"Cron for '{d.Name}' has no future occurrence."),
				})
				.ToList(),
			cancellationToken
		);
	}

	private async Task EnsureCodeSchedulesAsync(CancellationToken cancellationToken)
	{
		if (_state.CodeSchedulesAsserted)
			return;
		if (_storage is not IRecurringJobStorage)
		{
			_state.MarkCodeSchedulesAsserted();
			return;
		}

		await _scheduleInitialization.WaitAsync(cancellationToken);
		try
		{
			if (_state.CodeSchedulesAsserted)
				return;
			await AssertCodeSchedulesAsync(cancellationToken);
			_state.MarkCodeSchedulesAsserted();
		}
		finally
		{
			_ = _scheduleInitialization.Release();
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
		var (traceParent, traceState) = Activity.Current;
		var record = new JobRecord
		{
			JobHandle = JobHandle.FromString(_idGenerator.CreateId(IdKind.Job)),
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
			);
			var pending = await _storage.QueryJobsAsync(
				new() { State = JobState.Pending, JobName = definition.Name, Take = 1 },
				cancellationToken
			);
			if (active.Count != 0 || pending.Count != 0)
				record = record with { State = JobState.Skipped, CompletedAt = now };
		}

		if (await recurringStorage.MaterializeRecurringAsync(schedule, record, next, cancellationToken)
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

	[LoggerMessage(Level = LogLevel.Error, Message = "Immediate.Jobs scheduler iteration failed; polling will continue")]
	private partial void SchedulerIterationFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Immediate.Jobs shutdown drain exceeded {shutdownTimeout}")]
	private partial void ShutdownDrainExceeded(TimeSpan shutdownTimeout);

	[LoggerMessage(Level = LogLevel.Error, Message = "Unhandled worker error for job {jobHandle}; its lease will expire")]
	private partial void UnhandledWorkerError(Exception exception, JobHandle jobHandle);

	[LoggerMessage(Level = LogLevel.Information, Message = "Job completed in {durationMs} ms")]
	private partial void JobCompleted(double durationMs);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job failed and will retry at {nextRetryAt}")]
	private partial void JobWillRetry(Exception exception, DateTimeOffset? nextRetryAt);

	[LoggerMessage(Level = LogLevel.Error, Message = "Job exhausted all {maxAttempts} attempts")]
	private partial void JobExhaustedAttempts(Exception exception, int maxAttempts);

	[LoggerMessage(
		Level = LogLevel.Information,
		Message = "Batch & continuation features are disabled: the configured storage '{storageType}' implements the queue capability only. Configure a SQL provider to enable them."
	)]
	private partial void GraphFeaturesDisabled(string storageType);

	[LoggerMessage(
		Level = LogLevel.Information,
		Message = "Recurring job features are disabled: the configured storage '{storageType}' implements the queue capability only. Configure a SQL provider to enable them."
	)]
	private partial void RecurringJobFeaturesDisabled(string storageType);

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "Grouped jobs were acquired while fair queues are disabled. Their group ids are persisted but do not affect dispatch order; call UseFairQueues() to enable fair acquisition."
	)]
	private partial void GroupedJobsAcquiredWithoutFairQueues();

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "Could not persist execution telemetry; job invocation will continue"
	)]
	private partial void ExecutionTelemetryPersistenceFailed(Exception exception);

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "Could not renew the lease for job {jobHandle} execution {executionNumber}; renewal will be retried until the attempt finishes"
	)]
	private partial void LeaseRenewalFailed(
		Exception exception,
		JobHandle jobHandle,
		int executionNumber
	);

	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "Could not materialize recurring schedule {scheduleName}; other schedules and job acquisition will continue"
	)]
	private partial void RecurringMaterializationFailed(
		Exception exception,
		string scheduleName
	);
}
