using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class SingleServerRecoveryTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CancelledConcurrentInitializationDoesNotStartOrInterruptRecovery(bool failRecovery)
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		using var durable = new GatedRecoveryStorage(clock, failRecovery);
		var registrations = new ServiceCollection();
		_ = registrations.AddLogging();
		_ = registrations.AddSingleton<TimeProvider>(clock);
		_ = registrations.AddImmediateJobsCore().ConfigureStorage(options => _ = options.UseSingleServer(_ => durable));
		await using var services = registrations.BuildServiceProvider(validateScopes: true);
		var storage = services.GetRequiredService<IJobStorage>();
		var recovery = storage.InitializeAsync(token).AsTask();
		await durable.Entered.Task.WaitAsync(token);

		try
		{
			using var cancelled = new CancellationTokenSource();
			await cancelled.CancelAsync();
			_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => storage.InitializeAsync(cancelled.Token).AsTask());
			Assert.Equal(1, durable.InitializationCount);
		}
		finally
		{
			_ = durable.Release.TrySetResult();
		}

		if (failRecovery)
		{
			var firstFailure = await Assert.ThrowsAsync<ImmediateJobException>(() => recovery);
			var laterFailure = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.InitializeAsync(token).AsTask());
			Assert.Same(firstFailure, laterFailure);
		}
		else
		{
			await recovery;
			await storage.InitializeAsync(token);
			Assert.True(await storage.IsHealthyAsync(token));
		}

		Assert.Equal(1, durable.InitializationCount);
	}
}

file sealed class GatedRecoveryStorage(TimeProvider timeProvider, bool failRecovery) : CapturingJobStorage(timeProvider)
{
	private int _initializationCount;

	public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	public int InitializationCount => Volatile.Read(ref _initializationCount);

	public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		_ = Interlocked.Increment(ref _initializationCount);
		_ = Entered.TrySetResult();
		await Release.Task.WaitAsync(cancellationToken);
		if (failRecovery)
			throw new ImmediateJobException("Expected recovery failure.");
		await base.InitializeAsync(cancellationToken);
	}
}
