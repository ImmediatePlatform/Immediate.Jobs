using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

/// <summary>
/// 	A separately discoverable storage-provider conformance test.
/// </summary>
public sealed class JobStorageConformanceTestCase
{
	private readonly JobStorageConformanceScenario _scenario;

	internal JobStorageConformanceTestCase(
		string name,
		StorageCapabilities requiredCapabilities,
		JobStorageConformanceScenario scenario,
		PersistedJobState? persistedJobState = null
	)
	{
		Name = name;
		RequiredCapabilities = requiredCapabilities;
		_scenario = scenario;

		if (!requiredCapabilities.HasFlag(StorageCapabilities.Graph)
			&& persistedJobState is { Batches.Count: > 0 } or { Edges.Count: > 0 })
		{
			throw new ImmediateJobException("Cannot have pre-configured batches or edges on non-graph storages.");
		}

		PersistedJobState =
			persistedJobState
			?? new()
			{
				Jobs = [],
				Batches = [],
				Edges = [],
				RecurringSchedules = [],
			};
	}

	/// <summary>
	/// 	Gets the stable, behavior-oriented identifier displayed by a parameterized test runner.
	/// </summary>
	public string Name { get; }

	/// <summary>
	/// 	Gets the capabilities required by this case.
	/// </summary>
	public StorageCapabilities RequiredCapabilities { get; }

	/// <summary>
	///	    Represents the data that should be pre-loaded to the durable storage before the test runs.
	/// </summary>
	/// <remarks>
	///	    This is frequently implemented as part of the storage itself, in order to use the convenience methods that
	///	    already exist in the storage. See <see cref="InMemoryJobStorage.LoadPersistedJobState"/> for example.
	/// </remarks>
	public PersistedJobState PersistedJobState { get; }

	/// <summary>
	/// 	Resolves the registered storage, verifies its advertised capabilities, and runs this case.
	/// </summary>
	/// <param name="serviceProvider">
	/// 	The provider-specific, isolated service provider configured by the test fixture.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the conformance test.
	/// </param>
	/// <returns>A value task that represents the conformance test.</returns>
	/// <exception cref="JobTestAssertionException">
	/// 	The storage cannot be resolved, its capabilities do not match the advertised flags, or the invariant fails.
	/// </exception>
	public async ValueTask RunAsync(
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(serviceProvider);

		var storage = serviceProvider.GetServices<IJobStorage>().ToList() switch
		{
			[] => throw new JobTestAssertionException(
				ConformanceAssert.FormatFailure(
					Name,
					"the service provider must resolve exactly one IJobStorage registration"
				)
			),

			[{ } x] => x,

			var list => throw new JobTestAssertionException(
				ConformanceAssert.FormatFailure(
					Name,
					string.Create(provider: null, $"Expected exactly one IJobStorage registration, but found {list.Count}.")
				)
			),
		};

		var timeProvider = ConformanceAssert.IsAssignableFrom<FakeTimeProvider>(
			serviceProvider.GetRequiredService<TimeProvider>(),
			Name,
			"time-dependent conformance cases require a FakeTimeProvider registered as TimeProvider"
		);

		var resolvedCapabilities = storage.GetCapabilities();

		ConformanceAssert.True(
			resolvedCapabilities.HasFlag(RequiredCapabilities),
			Name,
			"the resolved IJobStorage capability interfaces must contain the flags required to run the test"
		);

		try
		{
			await storage.InitializeAsync(cancellationToken);
			await _scenario(storage, timeProvider, cancellationToken);
		}
		catch (JobTestAssertionException)
		{
			throw;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new JobTestAssertionException(
				ConformanceAssert.FormatFailure(Name, "the provider must complete the conformance scenario without an unexpected failure"),
				exception
			);
		}
	}

	/// <inheritdoc />
	public override string ToString() => Name;
}
