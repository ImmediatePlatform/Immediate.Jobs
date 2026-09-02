using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
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

	/// <summary>Creates a harness at the Unix epoch with customized worker options.</summary>
	/// <param name="configureServices">Registers job definitions and handler services.</param>
	/// <param name="configureWorkers">Customizes the production scheduler's worker options.</param>
	public JobTestHarness(
		Action<IServiceCollection>? configureServices,
		Action<ImmediateJobsOptions>? configureWorkers
	) : this(DateTimeOffset.UnixEpoch, configureServices, configureWorkers)
	{
	}

	/// <summary>Creates a harness at a specified UTC instant.</summary>
	/// <param name="start">The initial UTC time exposed by the controllable clock.</param>
	/// <param name="configureServices">
	/// Registers generated job definitions and the services used by their handlers. Calling the generated
	/// <c>AddImmediateJobs</c> method here is supported; the harness clock and in-memory provider remain authoritative.
	/// </param>
	/// <param name="configureWorkers">Optionally customizes the production scheduler's worker options.</param>
	public JobTestHarness(
		DateTimeOffset start,
		Action<IServiceCollection>? configureServices = null,
		Action<ImmediateJobsOptions>? configureWorkers = null
	)
	{
		TimeProvider = new(start);
		var services = new ServiceCollection();
		configureServices?.Invoke(services);

		_ = services.AddSingleton<TimeProvider>(TimeProvider);
		_ = services.AddSingleton(TimeProvider);
		_ = services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
		_ = services.AddSingleton<CapturingJobStorage>();

		_ = services.AddImmediateJobsCore()
			.ConfigureWorkers(o =>
			{
				o.MaxParallelJobs = 1;
				configureWorkers?.Invoke(o);
			})
			.ConfigureStorage(o => o
				.UseStorage(static provider => provider.GetRequiredService<CapturingJobStorage>())
				.UseDistributed());

		_serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
		{
			ValidateScopes = true,
			ValidateOnBuild = true,
		});

		Services = _serviceProvider;
		Storage = _serviceProvider.GetRequiredService<CapturingJobStorage>();
		Batches = new BatchScheduler(
			Storage,
			TimeProvider,
			_serviceProvider.GetRequiredService<IIdGenerator>()
		);
		Scheduler = _serviceProvider.GetRequiredService<JobSchedulingService>();
	}

	/// <summary>The controllable clock used by schedulers, storage, retries, and cron materialization.</summary>
	/// <value>The harness clock.</value>
	public FakeTimeProvider TimeProvider { get; }

	/// <summary>The harness service provider.</summary>
	/// <value>The service provider created for the harness.</value>
	public IServiceProvider Services { get; }

	/// <summary>The storage capture log used by the harness.</summary>
	/// <value>The capturing storage instance, also available from <see cref="Services"/>.</value>
	public CapturingJobStorage Storage { get; }

	/// <summary>Builds atomic batches against the harness storage and fake clock.</summary>
	/// <value>The batch scheduler configured for the harness.</value>
	public IBatchScheduler Batches { get; }

	/// <summary>The production scheduler runner hosted by the harness.</summary>
	/// <value>The scheduler service configured for the harness.</value>
	public JobSchedulingService Scheduler { get; }

	/// <summary>Runs every invocation currently due and returns when the due queue is empty.</summary>
	/// <param name="cancellationToken">A token that can cancel draining.</param>
	/// <returns>A task that completes when no currently due work remains.</returns>
	public ValueTask DrainAsync(CancellationToken cancellationToken = default) =>
		Scheduler.DrainAsync(cancellationToken);

	/// <summary>Advances fake time and then runs every invocation that became due.</summary>
	/// <param name="amount">The amount by which to advance the clock.</param>
	/// <param name="cancellationToken">A token that can cancel draining.</param>
	/// <returns>A task that completes when no newly due work remains.</returns>
	public async ValueTask AdvanceTimeAndDrainAsync(
		TimeSpan amount,
		CancellationToken cancellationToken = default
	)
	{
		if (amount < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(amount), "Fake time cannot move backwards.");

		TimeProvider.Advance(amount);
		await DrainAsync(cancellationToken);
	}

	/// <summary>Advances fake time to an absolute instant and drains newly due work.</summary>
	/// <param name="instant">The absolute time to which the clock is advanced.</param>
	/// <param name="cancellationToken">A token that can cancel draining.</param>
	/// <returns>A task that completes when no newly due work remains.</returns>
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
	/// <param name="query">The optional monitoring query. When omitted, up to 1,000 jobs are returned.</param>
	/// <param name="cancellationToken">A token that can cancel the query.</param>
	/// <returns>The persisted jobs that match the query.</returns>
	public ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery? query = null,
		CancellationToken cancellationToken = default
	) => Storage.QueryJobsAsync(query ?? new() { Take = 1000 }, cancellationToken);

	/// <summary>Finds an invocation by identifier, or throws a test assertion exception.</summary>
	/// <param name="jobHandle">The invocation identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the query.</param>
	/// <returns>The persisted job record.</returns>
	public async ValueTask<JobRecord> GetJobAsync(string jobHandle, CancellationToken cancellationToken = default)
	{
		var jobs = await Storage.QueryJobsAsync(new() { Take = 1000 }, cancellationToken);
		return jobs.FirstOrDefault(job => string.Equals(job.JobHandle.Value, jobHandle, StringComparison.Ordinal))
			?? throw new JobTestAssertionException($"Expected job '{jobHandle}' to have been enqueued, but it was not found.");
	}

	/// <summary>Finds an invocation returned by a typed scheduler call.</summary>
	/// <param name="job">The handle returned by the scheduler.</param>
	/// <param name="cancellationToken">A token that can cancel the query.</param>
	/// <returns>The persisted job record.</returns>
	public ValueTask<JobRecord> GetJobAsync(JobHandle job, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(job);
		return GetJobAsync(job.Value, cancellationToken);
	}

	/// <summary>Asserts and deserializes the invocation returned by a typed scheduler call.</summary>
	/// <typeparam name="TPayload">The expected payload type.</typeparam>
	/// <param name="jobHandle">The invocation identifier.</param>
	/// <param name="expectedState">The expected durable state, or <see langword="null"/> to accept any state.</param>
	/// <param name="cancellationToken">A token that can cancel the query.</param>
	/// <returns>The durable record paired with its deserialized payload.</returns>
	public async ValueTask<EnqueuedJob<TPayload>> AssertEnqueuedAsync<TPayload>(
		string jobHandle,
		JobState? expectedState = null,
		CancellationToken cancellationToken = default
	)
	{
		var job = await GetJobAsync(jobHandle, cancellationToken);
		if (expectedState is { } state && job.State != state)
		{
			throw new JobTestAssertionException(
				$"Expected job '{jobHandle}' to be {state}, but it was {job.State}."
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
				$"Job '{jobHandle}' did not contain a valid {typeof(TPayload).FullName} payload: {exception.Message}"
			);
		}

		return new(job, payload);
	}

	/// <summary>Asserts and deserializes the invocation returned by a typed scheduler call.</summary>
	/// <typeparam name="TPayload">The expected payload type.</typeparam>
	/// <param name="job">The handle returned by the scheduler.</param>
	/// <param name="expectedState">The expected durable state, or <see langword="null"/> to accept any state.</param>
	/// <param name="cancellationToken">A token that can cancel the query.</param>
	/// <returns>The durable record paired with its deserialized payload.</returns>
	public ValueTask<EnqueuedJob<TPayload>> AssertEnqueuedAsync<TPayload>(
		JobHandle job,
		JobState? expectedState = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(job);
		return AssertEnqueuedAsync<TPayload>(job.Value, expectedState, cancellationToken);
	}

	/// <summary>Asserts that a committed batch and exactly the expected number of members are visible together.</summary>
	/// <param name="batch">The committed batch handle.</param>
	/// <param name="expectedMembers">The expected number of committed batch members.</param>
	/// <param name="cancellationToken">A token that can cancel the assertion query.</param>
	/// <returns>A task that completes when the assertion succeeds.</returns>
	public async ValueTask AssertBatchCommittedAtomicallyAsync(
		BatchHandle batch,
		int expectedMembers,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(batch);
		var status = await Storage.GetBatchStatusAsync(batch, cancellationToken)
			?? throw new JobTestAssertionException($"Expected batch '{batch}' to be committed, but it was not found.");
		var members = await Storage.QueryBatchMembersAsync(
			batch,
			new() { Take = Math.Max(1, expectedMembers + 1) },
			cancellationToken
		);
		if (status.Total != expectedMembers || members.Count != expectedMembers)
		{
			throw new JobTestAssertionException(
				string.Create(CultureInfo.InvariantCulture, $"Expected batch '{batch}' to contain {expectedMembers} jobs, but its header reports {status.Total} and {members.Count} members were found.")
			);
		}
	}

	/// <summary>Asserts that the child has a persisted dependency on the supplied parent.</summary>
	/// <param name="parent">The expected parent invocation.</param>
	/// <param name="child">The expected child invocation.</param>
	/// <param name="cancellationToken">A token that can cancel the assertion query.</param>
	/// <returns>A task that completes when the assertion succeeds.</returns>
	public async ValueTask AssertContinuationReleasedAfterAsync(
		JobHandle parent,
		JobHandle child,
		CancellationToken cancellationToken = default
	)
	{
		var childStatus = await Storage.GetJobStatusAsync(child, cancellationToken)
			?? throw new JobTestAssertionException($"Expected continuation '{child}', but it was not found.");
		if (!childStatus.DependsOn.Any(edge => edge.ParentJobHandle == parent))
		{
			throw new JobTestAssertionException(
				$"Expected job '{child}' to depend on '{parent}', but no such edge was persisted."
			);
		}

		if (childStatus.State is JobState.Cancelled or JobState.Skipped)
			throw new JobTestAssertionException($"Expected continuation '{child}' to be released, but it was {childStatus.State}.");
		if (childStatus.State == JobState.AwaitingContinuation)
			throw new JobTestAssertionException($"Expected continuation '{child}' to be released, but it is still waiting.");
	}

	/// <summary>Asserts that every supplied invocation was skipped by a dependency cascade.</summary>
	/// <param name="subtree">The invocations expected to be cascade-skipped.</param>
	/// <param name="cancellationToken">A token that can cancel the assertion query.</param>
	/// <returns>A task that completes when the assertion succeeds.</returns>
	public async ValueTask AssertCascadeSkippedAsync(
		IReadOnlyCollection<JobHandle> subtree,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(subtree);
		foreach (var handle in subtree)
		{
			var job = await GetJobAsync(handle, cancellationToken);
			if (job.State != JobState.Skipped)
				throw new JobTestAssertionException($"Expected job '{handle}' to be cascade-skipped, but it was {job.State}.");
		}
	}

	/// <summary>Compatibility alias for <see cref="AssertCascadeSkippedAsync"/>.</summary>
	/// <param name="subtree">The invocations expected to be cascade-skipped.</param>
	/// <param name="cancellationToken">A token that can cancel the assertion query.</param>
	/// <returns>A task that completes when the assertion succeeds.</returns>
	public ValueTask AssertCascadeCancelledAsync(
		IReadOnlyCollection<JobHandle> subtree,
		CancellationToken cancellationToken = default
	) => AssertCascadeSkippedAsync(subtree, cancellationToken);

	/// <summary>Runs one generated invoker, including its compile-time behavior pipeline, outside durable state.</summary>
	/// <typeparam name="TPayload">The payload type accepted by the generated invoker.</typeparam>
	/// <param name="definition">The generated job definition to invoke.</param>
	/// <param name="payload">The payload supplied to the invoker.</param>
	/// <param name="cancellationToken">A token that can cancel invocation.</param>
	/// <returns>A task that completes when the behavior pipeline finishes.</returns>
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
			JobHandle = JobHandle.FromString(_serviceProvider.GetRequiredService<IIdGenerator>().CreateId(IdKind.Job)),
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
			new JobExecution { Record = record, Definition = definition, CancellationToken = cancellationToken }
		);
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
		await _serviceProvider.DisposeAsync();
	}
}

/// <summary>A durable invocation paired with its strongly typed deserialized payload.</summary>
/// <typeparam name="TPayload">The deserialized payload type.</typeparam>
/// <param name="Record">The persisted job record.</param>
/// <param name="Payload">The deserialized payload.</param>
public sealed record EnqueuedJob<TPayload>(JobRecord Record, TPayload Payload);
