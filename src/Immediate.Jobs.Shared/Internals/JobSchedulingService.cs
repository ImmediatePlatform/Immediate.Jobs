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
				await ExecuteJobAsync(job, cancellationToken);
			}
		}
	}

	private async Task RunSchedulerIterationAsync(CancellationToken cancellationToken)
	{
		// The heartbeat runs first so that a failure in any later stage cannot make a polling scheduler
		// look dead to ImmediateJobsHealthCheck.
		var now = _timeProvider.GetUtcNow();

		await _storage
			.HeartbeatAsync(
				new JobServerSnapshot { WorkerId = _workerId, LastHeartbeat = now, ActiveWorkers = _state.ActiveWorkers, MaxWorkers = _options.WorkerCount },
				cancellationToken
			);

		_state.MarkHeartbeat(now);

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

	private async Task RunWorkerAsync(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var record in _channel.Reader.ReadAllAsync(cancellationToken))
			{
				try
				{
					await ExecuteJobAsync(record, cancellationToken);
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

		using var leaseTimer = _timeProvider.CreateTimer(
			// intentionally not waiting on the returned Task; will report it's own exceptions
			state => _ = RenewLeaseAsync((JobRecord)state!),
			record,
			TimeSpan.FromTicks(Math.Max(100_000, _options.LeaseDuration.Ticks / 3)),
			TimeSpan.FromTicks(Math.Max(100_000, _options.LeaseDuration.Ticks / 3))
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
			_state.DecrementActive();
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
			_options.MaxQueueLength - Volatile.Read(ref _reservations)
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
		_queueReservations.AddOrUpdate(record.QueueName, 1, static (_, count) => count + 1);
		_jobReservations.AddOrUpdate(record.JobName, 1, static (_, count) => count + 1);
	}

	private void Release(JobRecord record)
	{
		Interlocked.Decrement(ref _reservations);
		_queueReservations.AddOrUpdate(record.QueueName, 0, static (_, count) => Math.Max(0, count - 1));
		_jobReservations.AddOrUpdate(record.JobName, 0, static (_, count) => Math.Max(0, count - 1));
	}

	private async Task RenewLeaseAsync(JobRecord record)
	{
		// force our way off the timer thread
		await Task.Yield();

		try
		{
			await _storage.RenewLeaseAsync(
				record.JobHandle,
				record.Attempt,
				_workerId,
				_options.LeaseDuration,
				// explicitly non-cancellable
				cancellationToken: default
			);
		}
#pragma warning disable CA1031 // There is no catcher above us to safely report exceptions
		catch (Exception exception)
#pragma warning restore CA1031
		{
			LeaseRenewalFailed(exception, record.JobHandle, record.Attempt);
		}
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
						NextRunAt = GetNextOccurence(
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

	private static DateTimeOffset GetNextOccurence(DateTimeOffset from, string cron, string timeZone, string jobName)
	{
		var expression = JobCron.Parse(cron);
		var tzi = JobCron.GetTimeZone(timeZone);
		return expression.GetNextOccurrence(from, tzi, inclusive: false) switch
		{
			{ } next => next,
			_ => throw new ImmediateJobException($"Recurring schedule '{jobName}' has no future occurrence."),
		};
	}

	private async Task MaterializeRecurringScheduleAsync(
		IRecurringJobStorage recurringStorage,
		RecurringJobSchedule schedule,
		JobDefinition definition,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		// TODO: Add `MisfireHandlingMode` for specifying behavior during missed runs

		var next = GetNextOccurence(
			schedule.NextRunAt,
			schedule.Cron,
			schedule.TimeZone,
			schedule.Name
		);

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
				DueAt = schedule.NextRunAt,
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
							CompletedAt = now,
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
			next = GetNextOccurence(
				schedule.NextRunAt,
				schedule.Cron,
				schedule.TimeZone,
				schedule.Name
			);
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
		EventId = LibraryEventIds.SchedulerIterationFailed,
		EventName = "Immediate.Jobs.Shared.SchedulerIterationFailed",
		Level = LogLevel.Error, Message = "Immediate.Jobs scheduler iteration failed; polling will continue")]
	private partial void SchedulerIterationFailed(Exception exception);

	[LoggerMessage(
		EventId = LibraryEventIds.ShutdownDrainExceeded,
		EventName = "Immediate.Jobs.Shared.ShutdownDrainExceeded",
		Level = LogLevel.Warning, Message = "Immediate.Jobs shutdown drain exceeded {shutdownTimeout}")]
	private partial void ShutdownDrainExceeded(TimeSpan shutdownTimeout);

	[LoggerMessage(
		EventId = LibraryEventIds.UnhandledWorkerError,
		EventName = "Immediate.Jobs.Shared.UnhandledWorkerError",
		Level = LogLevel.Error, Message = "Unhandled worker error for job {jobHandle}; its lease will expire")]
	private partial void UnhandledWorkerError(Exception exception, JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.JobCompleted,
		EventName = "Immediate.Jobs.Shared.JobCompleted",
		Level = LogLevel.Information, Message = "Job completed in {durationMs} ms")]
	private partial void JobCompleted(double durationMs);

	[LoggerMessage(
		EventId = LibraryEventIds.JobWillRetry,
		EventName = "Immediate.Jobs.Shared.JobWillRetry",
		Level = LogLevel.Warning, Message = "Job failed and will retry at {nextRetryAt}")]
	private partial void JobWillRetry(Exception exception, DateTimeOffset? nextRetryAt);

	[LoggerMessage(
		EventId = LibraryEventIds.JobExhaustedAttempts,
		EventName = "Immediate.Jobs.Shared.JobExhaustedAttempts",
		Level = LogLevel.Error, Message = "Job exhausted all {maxAttempts} attempts")]
	private partial void JobExhaustedAttempts(Exception exception, int maxAttempts);

	[LoggerMessage(
		EventId = LibraryEventIds.GraphFeaturesDisabled,
		EventName = "Immediate.Jobs.Shared.GraphFeaturesDisabled",
		Level = LogLevel.Information,
		Message = "Batch & continuation features are disabled: the configured storage '{storageType}' implements the queue capability only. Configure a SQL provider to enable them."
	)]
	private partial void GraphFeaturesDisabled(string storageType);

	[LoggerMessage(
		EventId = LibraryEventIds.RecurringJobFeaturesDisabled,
		EventName = "Immediate.Jobs.Shared.RecurringJobFeaturesDisabled",
		Level = LogLevel.Information,
		Message = "Recurring job features are disabled: the configured storage '{storageType}' implements the queue capability only. Configure a SQL provider to enable them."
	)]
	private partial void RecurringJobFeaturesDisabled(string storageType);

	[LoggerMessage(
		EventId = LibraryEventIds.GroupedJobsAcquiredWithoutFairQueues,
		EventName = "Immediate.Jobs.Shared.GroupedJobsAcquiredWithoutFairQueues",
		Level = LogLevel.Warning,
		Message = "Grouped jobs were acquired while fair queues are disabled. Their group ids are persisted but do not affect dispatch order; call UseFairQueues() to enable fair acquisition."
	)]
	private partial void GroupedJobsAcquiredWithoutFairQueues();

	[LoggerMessage(
		EventId = LibraryEventIds.ExecutionTelemetryPersistenceFailed,
		EventName = "Immediate.Jobs.Shared.ExecutionTelemetryPersistenceFailed",
		Level = LogLevel.Warning,
		Message = "Could not persist execution telemetry; job invocation will continue"
	)]
	private partial void ExecutionTelemetryPersistenceFailed(Exception exception);

	[LoggerMessage(
		EventId = LibraryEventIds.LeaseRenewalFailed,
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
		EventId = LibraryEventIds.RecurringMaterializationFailed,
		EventName = "Immediate.Jobs.Shared.RecurringMaterializationFailed",
		Level = LogLevel.Error,
		Message = "Could not materialize recurring schedule {scheduleName}; other schedules and job acquisition will continue"
	)]
	private partial void RecurringMaterializationFailed(
		Exception exception,
		string scheduleName
	);
}
