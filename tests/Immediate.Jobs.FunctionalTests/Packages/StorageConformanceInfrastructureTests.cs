using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Packages;

#pragma warning disable CS1591
public sealed class StorageConformanceInfrastructureTests
{
	private const StorageCapabilities InMemoryCapabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring |
		StorageCapabilities.Graph |
		StorageCapabilities.FairQueues |
		StorageCapabilities.Replica;

	[Fact]
	public void GetCasesAlwaysIncludesQueueAndRoutesOnlyAdvertisedOptionalSuites()
	{
		var queueCases = JobStorageConformanceSuite.GetCases(StorageCapabilities.Queue);
		var recurringCases = JobStorageConformanceSuite.GetCases(
			StorageCapabilities.Queue | StorageCapabilities.Recurring
		);
		var allCases = JobStorageConformanceSuite.GetCases(InMemoryCapabilities);

		Assert.Equal(15, queueCases.Count);
		Assert.All(queueCases, testCase => Assert.Equal(StorageCapabilities.Queue, testCase.RequiredCapabilities));
		Assert.Contains(recurringCases, testCase => testCase.RequiredCapabilities == StorageCapabilities.Recurring);
		Assert.DoesNotContain(recurringCases, testCase => testCase.RequiredCapabilities == StorageCapabilities.Graph);
		Assert.DoesNotContain(recurringCases, testCase => testCase.RequiredCapabilities == StorageCapabilities.FairQueues);
		Assert.DoesNotContain(recurringCases, testCase => testCase.RequiredCapabilities == StorageCapabilities.Replica);
		Assert.Contains(allCases, testCase => testCase.RequiredCapabilities == StorageCapabilities.Recurring);
		Assert.Contains(allCases, testCase => testCase.RequiredCapabilities == StorageCapabilities.Graph);
		Assert.Contains(allCases, testCase => testCase.RequiredCapabilities == StorageCapabilities.FairQueues);
		Assert.Contains(allCases, testCase => testCase.RequiredCapabilities == StorageCapabilities.Replica);
	}

	[Theory]
	[InlineData(StorageCapabilities.None)]
	[InlineData((StorageCapabilities)32)]
	public void GetCasesRejectsImpossibleCapabilityClaims(StorageCapabilities capabilities)
	{
		_ = Assert.ThrowsAny<ArgumentException>(() => JobStorageConformanceSuite.GetCases(capabilities));
	}

	[Fact]
	public void CasesHaveUniqueStableNamesUsedByToString()
	{
		var cases = JobStorageConformanceSuite.GetCases(InMemoryCapabilities);

		Assert.All(cases, testCase => Assert.Equal(testCase.Name, testCase.ToString()));
		Assert.Equal(cases.Count, cases.Select(testCase => testCase.Name).Distinct(StringComparer.Ordinal).Count());
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "Queue.Lifecycle.InitializesIdempotently", StringComparison.Ordinal));
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "Recurring.Capability.ResolvesAdvertisedStorage", StringComparison.Ordinal));
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "Graph.Capability.ResolvesAdvertisedStorage", StringComparison.Ordinal));
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "FairQueues.Capability.ResolvesAdvertisedStorage", StringComparison.Ordinal));
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "Replica.Capability.ResolvesAdvertisedStorage", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunAsyncReportsMissingStorageRegistrationAsConformanceFailure()
	{
		await using var services = new ServiceCollection().BuildServiceProvider();
		var testCase = GetCase(StorageCapabilities.Queue, "Queue.Lifecycle.InitializesIdempotently");

		var exception = await Assert.ThrowsAsync<JobTestAssertionException>(
			() => testCase.RunAsync(services, TestContext.Current.CancellationToken).AsTask()
		);

		Assert.Contains(testCase.Name, exception.Message, StringComparison.Ordinal);
		Assert.Contains("IJobStorage", exception.Message, StringComparison.Ordinal);
		_ = Assert.IsType<InvalidOperationException>(exception.InnerException);
	}

	[Fact]
	public async Task RunAsyncRejectsMismatchBeforeRunningScenario()
	{
		await using var services = CreateInMemoryServices();
		var testCase = GetCase(StorageCapabilities.Queue, "Queue.Lifecycle.InitializesIdempotently");

		var exception = await Assert.ThrowsAsync<JobTestAssertionException>(
			() => testCase.RunAsync(services, TestContext.Current.CancellationToken).AsTask()
		);

		Assert.Contains(testCase.Name, exception.Message, StringComparison.Ordinal);
		Assert.Contains("exactly match", exception.Message, StringComparison.Ordinal);
		Assert.Contains($"Expected: {StorageCapabilities.Queue}", exception.Message, StringComparison.Ordinal);
		Assert.Contains($"Actual: {InMemoryCapabilities}", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("Queue.Lifecycle.InitializesIdempotently")]
	[InlineData("Queue.Health.ReportsProvisionedBackendReachable")]
	[InlineData("Queue.Cancellation.ObservesPreCancelledOperation")]
	[InlineData("Recurring.Capability.ResolvesAdvertisedStorage")]
	[InlineData("Graph.Capability.ResolvesAdvertisedStorage")]
	[InlineData("FairQueues.Capability.ResolvesAdvertisedStorage")]
	[InlineData("Replica.Capability.ResolvesAdvertisedStorage")]
	public async Task RunAsyncExecutesSelectedCaseAgainstStorageResolvedFromContainer(string caseName)
	{
		await using var services = CreateInMemoryServices();
		var testCase = GetCase(InMemoryCapabilities, caseName);

		await testCase.RunAsync(services, TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task RunAsyncDoesNotWrapCancellationRequestedByRunner()
	{
		await using var services = CreateInMemoryServices();
		var testCase = GetCase(InMemoryCapabilities, "Queue.Lifecycle.InitializesIdempotently");
		using var cancellationSource = new CancellationTokenSource();
		await cancellationSource.CancelAsync();

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => testCase.RunAsync(services, cancellationSource.Token).AsTask()
		);
	}

	private static JobStorageConformanceTestCase GetCase(StorageCapabilities capabilities, string name) =>
		Assert.Single(
			JobStorageConformanceSuite.GetCases(capabilities),
			testCase => string.Equals(testCase.Name, name, StringComparison.Ordinal)
		);

	private static ServiceProvider CreateInMemoryServices()
	{
		var services = new ServiceCollection();
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddSingleton(timeProvider);
		_ = services.AddImmediateJobsCore(options => _ = options.UseInMemory());
		return services.BuildServiceProvider(validateScopes: true);
	}
}
