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
	private readonly IJobGraphStorage _graphStorage;
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
		_graphStorage = Storage as IJobGraphStorage
			?? throw new NotSupportedException(
				"Batches & continuations require a graph-capable storage provider (a SQL database). " +
				"The configured provider implements the queue capability only."
			);
		Batches = new JobBatchScheduler(
			Storage,
			TimeProvider,
			_serviceProvider.GetRequiredService<IIdGenerator>()
		);
		Scheduler = _serviceProvider.GetRequiredService<JobSchedulerService>();
	}

	/// <summary>The controllable clock used by schedulers, storage, retries, and cron materialization.</summary>
	public FakeTimeProvider TimeProvider { get; }

	/// <summary>The harness service provider.</summary>
	public IServiceProvider Services { get; }

	/// <summary>The in-memory durable-state abstraction.</summary>
	public IJobStorage Storage { get; }

	/// <summary>Builds atomic batches against the harness storage and fake clock.</summary>
	public IJobBatchScheduler Batches { get; }

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
	public async ValueTask<JobRecord> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
	{
		var jobs = await Storage.QueryJobsAsync(new() { Take = 1000 }, cancellationToken).ConfigureAwait(false);
		return jobs.FirstOrDefault(job => job.Id == jobId)
			?? throw new JobTestAssertionException($"Expected job '{jobId}' to have been enqueued, but it was not found.");
	}

	/// <summary>Finds an invocation returned by a typed scheduler call.</summary>
	public ValueTask<JobRecord> GetJobAsync(JobHandle job, CancellationToken cancellationToken = default) =>
		GetJobAsync(job.Id, cancellationToken);

	/// <summary>Asserts and deserializes the invocation returned by a typed scheduler call.</summary>
	public async ValueTask<EnqueuedJob<TPayload>> AssertEnqueuedAsync<TPayload>(
		string jobId,
		JobState? expectedState = null,
		CancellationToken cancellationToken = default
	)
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

	/// <summary>Asserts and deserializes the invocation returned by a typed scheduler call.</summary>
	public ValueTask<EnqueuedJob<TPayload>> AssertEnqueuedAsync<TPayload>(
		JobHandle job,
		JobState? expectedState = null,
		CancellationToken cancellationToken = default
	) => AssertEnqueuedAsync<TPayload>(job.Id, expectedState, cancellationToken);

	/// <summary>Asserts that a committed batch and exactly the expected number of members are visible together.</summary>
	public async ValueTask AssertBatchCommittedAtomicallyAsync(
		BatchHandle batch,
		int expectedMembers,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(batch);
		var status = await _graphStorage.GetBatchStatusAsync(batch.Id, cancellationToken).ConfigureAwait(false)
			?? throw new JobTestAssertionException($"Expected batch '{batch.Id}' to be committed, but it was not found.");
		var members = await _graphStorage.QueryBatchMembersAsync(
			batch.Id,
			new() { Take = Math.Max(1, expectedMembers + 1) },
			cancellationToken
		).ConfigureAwait(false);
		if (status.Total != expectedMembers || members.Count != expectedMembers)
		{
			throw new JobTestAssertionException(
				$"Expected batch '{batch.Id}' to contain {expectedMembers} jobs, but its header reports {status.Total} and {members.Count} members were found."
			);
		}
	}

	/// <summary>Asserts that the child has a persisted dependency on the supplied parent.</summary>
	public async ValueTask AssertContinuationReleasedAfterAsync(
		JobHandle parent,
		JobHandle child,
		CancellationToken cancellationToken = default
	)
	{
		var childStatus = await Storage.GetJobStatusAsync(child.Id, cancellationToken).ConfigureAwait(false)
			?? throw new JobTestAssertionException($"Expected continuation '{child.Id}', but it was not found.");
		if (!childStatus.DependsOn.Any(edge => edge.ParentJobId == parent.Id))
		{
			throw new JobTestAssertionException(
				$"Expected job '{child.Id}' to depend on '{parent.Id}', but no such edge was persisted."
			);
		}

		if (childStatus.State == JobState.AwaitingContinuation)
			throw new JobTestAssertionException($"Expected continuation '{child.Id}' to be released, but it is still waiting.");
	}

	/// <summary>Asserts that every supplied invocation was cancelled by a dependency cascade.</summary>
	public async ValueTask AssertCascadeCancelledAsync(
		IReadOnlyCollection<JobHandle> subtree,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(subtree);
		foreach (var handle in subtree)
		{
			var job = await GetJobAsync(handle, cancellationToken).ConfigureAwait(false);
			if (job.State != JobState.Cancelled)
				throw new JobTestAssertionException($"Expected job '{handle.Id}' to be cascade-cancelled, but it was {job.State}.");
		}
	}

	/// <summary>Runs one generated invoker, including its compile-time behavior pipeline, outside durable state.</summary>
	public async ValueTask RunThroughPipelineAsync<TPayload>(
		JobDefinition definition,
		TPayload payload,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(definition);
		var now = TimeProvider.GetUtcNow();
		var record = new JobRecord
		{
			Id = _serviceProvider.GetRequiredService<IIdGenerator>().CreateId(IdKind.Job),
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
public sealed record EnqueuedJob<TPayload>(JobRecord Record, TPayload Payload);
