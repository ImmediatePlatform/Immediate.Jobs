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
		JobStorageConformanceScenario scenario
	)
	{
		Name = name;
		RequiredCapabilities = requiredCapabilities;
		_scenario = scenario;
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
			await _scenario(storage, timeProvider, cancellationToken).ConfigureAwait(false);
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
