using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Immediate.Jobs.Testing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class StorageConformanceInfrastructureTests
{
	private const StorageCapabilities AllCapabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Graph;

	[Fact]
	public void GetCasesAlwaysIncludesQueueTierAndRoutesOnlyAdvertisedOptionalSuites()
	{
		var queueCases = JobStorageConformanceSuite.GetCases(StorageCapabilities.Queue);
		var graphCases = JobStorageConformanceSuite.GetCases(AllCapabilities);
		var replicaCases = JobStorageConformanceSuite.GetCases(AllCapabilities, includeSingleServerReplicaCases: true);

		Assert.Equal(60, queueCases.Count);
		Assert.All(queueCases, testCase => Assert.Equal(StorageCapabilities.Queue, testCase.RequiredCapabilities));
		Assert.Contains(queueCases, testCase => testCase.Name.StartsWith("Recurring.", StringComparison.Ordinal));
		Assert.Contains(queueCases, testCase => testCase.Name.StartsWith("FairQueues.", StringComparison.Ordinal));
		Assert.Contains(graphCases, testCase => testCase.RequiredCapabilities == StorageCapabilities.Graph);
		Assert.DoesNotContain(graphCases, testCase => testCase.Name.StartsWith("Replica.", StringComparison.Ordinal));
		Assert.Contains(replicaCases, testCase => testCase.Name.StartsWith("Replica.", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(StorageCapabilities.None)]
	[InlineData((StorageCapabilities)2)]
	[InlineData((StorageCapabilities)32)]
	public void GetCasesRejectsImpossibleCapabilityClaims(StorageCapabilities capabilities)
	{
		_ = Assert.ThrowsAny<ArgumentException>(() => JobStorageConformanceSuite.GetCases(capabilities));
	}

	[Fact]
	public void GetCasesRejectsReplicaCasesWithoutGraph()
	{
		_ = Assert.Throws<ArgumentException>(
			() => JobStorageConformanceSuite.GetCases(StorageCapabilities.Queue, includeSingleServerReplicaCases: true)
		);
	}

	[Fact]
	public void CasesHaveUniqueStableNamesUsedByToString()
	{
		var cases = JobStorageConformanceSuite.GetCases(AllCapabilities, includeSingleServerReplicaCases: true);

		Assert.All(cases, testCase => Assert.Equal(testCase.Name, testCase.ToString()));
		Assert.Equal(cases.Count, cases.Select(testCase => testCase.Name).Distinct(StringComparer.Ordinal).Count());
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "Queue.Lifecycle.InitializesIdempotently", StringComparison.Ordinal));
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "Recurring.Lifecycle.UpdatesPausesResumesAndRemovesDynamicSchedule", StringComparison.Ordinal));
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "Graph.Capability.ResolvesAdvertisedStorage", StringComparison.Ordinal));
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "FairQueues.Disabled.PreservesOrdinaryDueOrder", StringComparison.Ordinal));
		Assert.Contains(cases, testCase => string.Equals(testCase.Name, "Replica.Acquisition.ClaimsExactlyTheRequestedDueJobs", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RunAsyncReportsMissingStorageRegistrationAsConformanceFailure()
	{
		await using var services = new ServiceCollection().BuildServiceProvider();
		var testCase = JobStorageConformanceSuite.AllCasesByName["Queue.Lifecycle.InitializesIdempotently"];

		var exception = await Assert.ThrowsAsync<JobTestAssertionException>(
			() => testCase.RunAsync(services, TestContext.Current.CancellationToken).AsTask()
		);

		Assert.Contains(testCase.Name, exception.Message, StringComparison.Ordinal);
		Assert.Contains("IJobStorage", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunAsyncReportsMultipleStorageRegistrationsAsConformanceFailure()
	{
		await using var storageServices = CreateInMemoryServices();
		var storage = storageServices.GetRequiredService<IJobStorage>();
		await using var services = new ServiceCollection()
			.AddSingleton(storage)
			.AddSingleton(storage)
			.BuildServiceProvider();
		var testCase = JobStorageConformanceSuite.AllCasesByName["Queue.Lifecycle.InitializesIdempotently"];

		var exception = await Assert.ThrowsAsync<JobTestAssertionException>(
			() => testCase.RunAsync(services, TestContext.Current.CancellationToken).AsTask()
		);

		Assert.Contains(testCase.Name, exception.Message, StringComparison.Ordinal);
		Assert.Contains("exactly one IJobStorage", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("Queue.Lifecycle.InitializesIdempotently")]
	[InlineData("Queue.Health.ReportsProvisionedBackendReachable")]
	[InlineData("Queue.Cancellation.ObservesPreCancelledOperation")]
	[InlineData("Recurring.Lifecycle.UpdatesPausesResumesAndRemovesDynamicSchedule")]
	[InlineData("Graph.Capability.ResolvesAdvertisedStorage")]
	[InlineData("FairQueues.Disabled.PreservesOrdinaryDueOrder")]
	public async Task RunAsyncExecutesSelectedCaseAgainstStorageResolvedFromContainer(string caseName)
	{
		await using var services = CreateInMemoryServices();
		var testCase = JobStorageConformanceSuite.AllCasesByName[caseName];

		await testCase.RunAsync(services, TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task RunAsyncDoesNotWrapCancellationRequestedByRunner()
	{
		await using var services = CreateInMemoryServices();
		var testCase = JobStorageConformanceSuite.AllCasesByName["Queue.Lifecycle.InitializesIdempotently"];
		using var cancellationSource = new CancellationTokenSource();
		await cancellationSource.CancelAsync();

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => testCase.RunAsync(services, cancellationSource.Token).AsTask()
		);
	}

	private static ServiceProvider CreateInMemoryServices()
	{
		var services = new ServiceCollection();
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddSingleton(timeProvider);
		_ = services.AddImmediateJobsCore().ConfigureStorage(options => _ = options.UseInMemory());
		return services.BuildServiceProvider(validateScopes: true);
	}
}
