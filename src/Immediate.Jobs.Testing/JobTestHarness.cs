using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing;

/// <summary>
/// Hosts Immediate.Jobs with in-memory storage and a controllable clock without starting background threads.
/// </summary>
public sealed class JobTestHarness : IAsyncDisposable, IDisposable
{
	private readonly ServiceProvider _serviceProvider;
	private bool _disposed;

	/// <summary>Creates a harness at the Unix epoch.</summary>
	/// <param name="configureServices">
	/// Registers generated job definitions and the services used by their handlers. Calling the generated
	/// <c>AddImmediateJobs</c> method here is supported; the harness clock and in-memory provider remain authoritative.
	/// </param>
	public JobTestHarness(Action<IServiceCollection>? configureServices = null)
		: this(DateTimeOffset.UnixEpoch, configureServices)
	{
	}

	/// <summary>Creates a harness at a specified UTC instant.</summary>
	public JobTestHarness(
		DateTimeOffset start,
		Action<IServiceCollection>? configureServices = null
	)
	{
		TimeProvider = new(start);
		var services = new ServiceCollection();
		_ = services.AddSingleton<TimeProvider>(TimeProvider);
		_ = services.AddSingleton(TimeProvider);
		_ = services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
		_ = services.AddImmediateJobsCore(options =>
		{
			_ = options.UseInMemory();
			options.MaxParallelJobs = 1;
		});
		configureServices?.Invoke(services);
		_serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
		{
			ValidateScopes = true,
			ValidateOnBuild = true,
		});

		Services = _serviceProvider;
		Storage = _serviceProvider.GetRequiredService<IJobStorage>();
		Scheduler = _serviceProvider.GetRequiredService<JobSchedulerService>();
	}

	/// <summary>The controllable clock used by schedulers, storage, retries, and cron materialization.</summary>
	public FakeTimeProvider TimeProvider { get; }

	/// <summary>The harness service provider.</summary>
	public IServiceProvider Services { get; }

	/// <summary>The in-memory durable-state abstraction.</summary>
	public IJobStorage Storage { get; }

	/// <summary>The production scheduler runner hosted by the harness.</summary>
	public JobSchedulerService Scheduler { get; }

	/// <summary>Runs every invocation currently due and returns when the due queue is empty.</summary>
	public ValueTask DrainAsync(CancellationToken cancellationToken = default) =>
		Scheduler.DrainAsync(cancellationToken);

	/// <summary>Advances fake time and then runs every invocation that became due.</summary>
	public async ValueTask AdvanceTimeAndDrainAsync(
		TimeSpan amount,
		CancellationToken cancellationToken = default
	)
	{
		if (amount < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(amount), "Fake time cannot move backwards.");

		TimeProvider.Advance(amount);
		await DrainAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Advances fake time to an absolute instant and drains newly due work.</summary>
	public ValueTask AdvanceTimeAndDrainAsync(
		DateTimeOffset instant,
		CancellationToken cancellationToken = default
	)
	{
		var amount = instant - TimeProvider.GetUtcNow();
		if (amount < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(instant), "Fake time cannot move backwards.");
		return AdvanceTimeAndDrainAsync(amount, cancellationToken);
	}

	/// <summary>Returns persisted jobs matching a monitoring query.</summary>
	public ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery? query = null,
		CancellationToken cancellationToken = default
	) => Storage.QueryJobsAsync(query ?? new() { Take = 1000 }, cancellationToken);

	/// <summary>Finds an invocation by identifier, or throws a test assertion exception.</summary>
	public async ValueTask<JobRecord> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		var jobs = await Storage.QueryJobsAsync(new() { Take = 1000 }, cancellationToken).ConfigureAwait(false);
		return jobs.FirstOrDefault(job => job.Id == jobId)
			?? throw new JobTestAssertionException($"Expected job '{jobId}' to have been enqueued, but it was not found.");
	}

	/// <summary>Asserts and deserializes the invocation returned by a typed scheduler call.</summary>
	public async ValueTask<EnqueuedJob<TPayload>> AssertEnqueuedAsync<TPayload>(
		Guid jobId,
		JobState? expectedState = null,
		CancellationToken cancellationToken = default
	)
		where TPayload : IJobRequest
	{
		var job = await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
		if (expectedState is { } state && job.State != state)
		{
			throw new JobTestAssertionException(
				$"Expected job '{jobId}' to be {state}, but it was {job.State}."
			);
		}

		TPayload payload;
		try
		{
			payload = _serviceProvider.GetRequiredService<IJobSerializer>().Deserialize<TPayload>(job.Payload);
		}
		catch (Exception exception)
		{
			throw new JobTestAssertionException(
				$"Job '{jobId}' did not contain a valid {typeof(TPayload).FullName} payload: {exception.Message}"
			);
		}

		return new(job, payload);
	}

	/// <summary>Runs one generated invoker, including its compile-time behavior pipeline, outside durable state.</summary>
	public async ValueTask RunThroughPipelineAsync<TPayload>(
		JobDefinition definition,
		TPayload payload,
		CancellationToken cancellationToken = default
	)
		where TPayload : IJobRequest
	{
		ArgumentNullException.ThrowIfNull(definition);
		var now = TimeProvider.GetUtcNow();
		var record = new JobRecord
		{
			Id = Guid.NewGuid(),
			JobName = definition.Name,
			QueueName = definition.Queue.Name,
			Payload = _serviceProvider.GetRequiredService<IJobSerializer>().Serialize(payload),
			State = JobState.Active,
			DueAt = now,
			CreatedAt = now,
			Attempt = 1,
		};
		await using var scope = _serviceProvider.CreateAsyncScope();
		await definition.Invoker.InvokeAsync(
			scope.ServiceProvider,
			new(record, definition, cancellationToken)
		).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_serviceProvider.Dispose();
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;
		_disposed = true;
		await _serviceProvider.DisposeAsync().ConfigureAwait(false);
	}
}

/// <summary>A durable invocation paired with its strongly typed deserialized payload.</summary>
public sealed record EnqueuedJob<TPayload>(JobRecord Record, TPayload Payload)
	where TPayload : IJobRequest;
