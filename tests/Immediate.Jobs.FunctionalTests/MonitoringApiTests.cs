using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests;

public sealed class MonitoringApiTests
{
	[Fact]
	public async Task MonitorForwardsBulkSnapshotAndMetadataReadsWithoutIndividualStateQueries()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		using var storage = new MonitoringStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [new() { Name = "remote" }] }, token);
		var monitor = new JobMonitor(storage, clock, new FixedIdGenerator());
		Assert.Equal("remote", Assert.Single((await monitor.GetSnapshotAsync(token)).DefinitionStatuses).JobName);
		Assert.Equal("remote", Assert.Single((await monitor.GetDefinitionsAsync(token)).Jobs).Name);
		Assert.Equal(1, storage.Snapshots);
		Assert.Equal(1, storage.DefinitionReads);
	}

	[Fact]
	public async Task ManualRecurringTriggerChecksConfigurationAndCancellationStatus()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		using var storage = new MonitoringStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [new() { Name = "Remote", QueueName = "current" }] }, token);
		await storage.UpsertRecurringAsync(new()
		{
			Name = "schedule",
			JobName = "remote",
			QueueName = "old",
			Cron = "* * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = clock.GetUtcNow().AddHours(1),
		}, token);
		var monitor = new JobMonitor(storage, clock, new FixedIdGenerator());
		await monitor.TriggerRecurringAsync("schedule", token);
		var job = Assert.Single(await storage.QueryJobsAsync(new(), token));
		Assert.Equal("Remote", job.JobName);
		Assert.Equal("current", job.QueueName);
		Assert.Equal(1, storage.Snapshots);
		Assert.Equal(1, storage.DefinitionReads);
	}

	[Fact]
	public async Task CanceledRecurringScheduleCannotBeTriggeredEvenAfterItsDefinitionReturns()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		using var storage = new MonitoringStorage(clock);
		var registration = new JobDefinitionRegistration { Definitions = [new() { Name = "Remote" }] };
		await storage.MergeJobDefinitionsListAsync(registration, token);
		await storage.UpsertRecurringAsync(new()
		{
			Name = "schedule",
			JobName = "Remote",
			Cron = "* * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = clock.GetUtcNow(),
		}, token);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [] }, token);
		await storage.MergeJobDefinitionsListAsync(registration, token);
		var monitor = new JobMonitor(storage, clock, new FixedIdGenerator());
		var status = Assert.Single((await monitor.GetSnapshotAsync(token)).Recurring);
		Assert.True(status.IsPaused);
		Assert.Equal(JobDefinitionReconciliation.RemovalCancellationReason, status.CancellationReason);
		Assert.Single((await monitor.GetDefinitionsAsync(token)).Recurring);
		await Assert.ThrowsAsync<ImmediateJobException>(() => monitor.TriggerRecurringAsync("schedule", token).AsTask());
		await Assert.ThrowsAsync<ImmediateJobException>(() => monitor.ResumeRecurringAsync("schedule", token).AsTask());
		Assert.Empty(await storage.QueryJobsAsync(new(), token));
	}

	private sealed class FixedIdGenerator : IIdGenerator
	{
		public string CreateId(IdKind kind) => "monitor-trigger";
	}

	private sealed class MonitoringStorage(TimeProvider clock) : CapturingJobStorage(clock)
	{
		public int Snapshots { get; private set; }
		public int DefinitionReads { get; private set; }
		public override ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("Monitoring must not request individual acquisition states.");
		public override ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
		{
			Snapshots++;
			return base.GetMonitoringSnapshotAsync(cancellationToken);
		}
		public override ValueTask<JobMonitoringDefinitions> GetMonitoringDefinitionsAsync(CancellationToken cancellationToken = default)
		{
			DefinitionReads++;
			return base.GetMonitoringDefinitionsAsync(cancellationToken);
		}
	}
}
