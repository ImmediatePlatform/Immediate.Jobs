using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable IDE0130
namespace Immediate.Jobs.Testing;

/// <summary>
/// 	A separately discoverable storage-provider conformance test.
/// </summary>
public sealed class JobStorageConformanceTestCase
{
	private readonly StorageCapabilities _advertisedCapabilities;
	private readonly JobStorageConformanceScenario _scenario;

	internal JobStorageConformanceTestCase(
		JobStorageConformanceCaseDefinition definition,
		StorageCapabilities advertisedCapabilities
	)
	{
		ArgumentNullException.ThrowIfNull(definition);

		Name = definition.Name;
		RequiredCapabilities = definition.RequiredCapabilities;
		_scenario = definition.Scenario;
		_advertisedCapabilities = advertisedCapabilities;
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

		IJobStorage storage;
		try
		{
			var storages = serviceProvider.GetServices<IJobStorage>().ToArray();
			if (storages.Length != 1)
			{
				throw new InvalidOperationException(
					$"Expected exactly one IJobStorage registration, but found {storages.Length}."
				);
			}

			storage = storages[0];
		}
		catch (Exception exception)
		{
			throw new JobTestAssertionException(
				ConformanceAssert.FormatFailure(
					Name,
					"the service provider must resolve exactly one IJobStorage registration"
				),
				exception
			);
		}

		var resolvedCapabilities = storage.GetCapabilities();
		ConformanceAssert.Equal(
			_advertisedCapabilities,
			resolvedCapabilities,
			Name,
			"the resolved IJobStorage capability interfaces must exactly match the flags passed to GetCases"
		);

		try
		{
			await _scenario(storage, serviceProvider, cancellationToken).ConfigureAwait(false);
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
