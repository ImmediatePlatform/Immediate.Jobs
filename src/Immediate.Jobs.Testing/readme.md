# Immediate.Jobs.Testing

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.Testing.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.Testing/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/testing-jobs)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

Deterministic testing tools for [Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/): a harness with fake
time, advance-and-drain helpers, capturing in-memory storage, enqueue assertions, a single-job handler-pipeline runner,
and storage-provider conformance tests.

## Installation

```console
dotnet add package Immediate.Jobs --prerelease
dotnet add package Immediate.Jobs.Testing --prerelease
```

## Deterministic job tests

`JobTestHarness` hosts the production scheduling service with in-memory storage and a controllable clock, without
starting background threads. Register the generated jobs, handlers, and their dependencies in its service collection:

```csharp
await using var harness = new JobTestHarness(services =>
{
	services.AddMyAppHandlers();
	services.AddMyAppJobs();
	services.AddSingleton<IEmailSender, RecordingEmailSender>();
});

await using var scope = harness.Services.CreateAsyncScope();
var scheduler = scope.ServiceProvider.GetRequiredService<SendWelcomeEmail.Scheduler>();
var handle = await scheduler.ScheduleAsync(
	new(userId, "v2"),
	TimeSpan.FromMinutes(10),
	cancellationToken
);

var enqueued = await harness.AssertEnqueuedAsync<SendWelcomeEmail.Payload>(
	handle,
	JobState.Scheduled,
	cancellationToken
);
Assert.Equal(userId, enqueued.Payload.UserId);

await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromMinutes(10), cancellationToken);
Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(handle, cancellationToken)).State);
```

Delayed work, scheduled occurrences, timeouts, and backoff tests do not need wall-clock sleeps. The harness also exposes
persisted-job queries and focused assertions for batches, continuations, and dependency cascades. Register generated
jobs in the callback, but do not call `ConfigureStorage`; the harness installs its own in-memory provider and fake clock.

## Capturing scheduler calls

`JobTestHarness` installs `CapturingJobStorage` behind the production schedulers. Calls are recorded without preventing
jobs from being queried, cancelled, or executed normally:

```csharp
await using var harness = new JobTestHarness(services => services.AddMyAppJobs());
var scheduler = harness.Services.GetRequiredService<SendWelcomeEmail.Scheduler>();
var payload = new SendWelcomeEmail.Payload(userId, "v2");
var handle = await scheduler.EnqueueAsync(
	payload,
	groupId: "tenant-a",
	cancellationToken: cancellationToken
);

var captured = harness.Captures.FindJob(handle)!;
Assert.Equal(handle, captured.JobId);
Assert.Equal("tenant-a", captured.GroupId);

await scheduler.CancelAsync(handle, cancellationToken);
Assert.Equal(JobState.Cancelled, (await harness.GetJobAsync(handle, cancellationToken)).State);
```

The same `CapturingJobStorage` instance is available from `harness.Captures` and dependency injection. Its snapshots
preserve call order for jobs, continuations, batches, dynamic batch additions, recurring definitions, and recurring
materializations. `Clear()` resets only the capture log; it does not remove persisted jobs.

Use `Jobs`, `Continuations`, `Batches`, `BatchJobs`, `DynamicContinuations`, `RecurringSchedules`,
`RecurringOperations`, and `RecurringMaterializations` when a test needs the complete call history rather than one job.
Each property returns a snapshot, so assertions do not observe a collection changing underneath them.

## Storage-provider conformance

If you are implementing a new `IJobStorage` provider, use `JobStorageConformanceSuite` to verify its shared behavior.
Select the feature flags the provider supports, create a fresh service provider and backend for each case, and pass the
service provider to `RunAsync`.

```csharp
private const StorageCapabilities Capabilities =
	StorageCapabilities.Queue | StorageCapabilities.Recurring;

public static TheoryData<JobStorageConformanceTestCase> Cases =>
	[.. JobStorageConformanceSuite.GetCases(Capabilities)];

[Theory]
[MemberData(nameof(Cases))]
public async Task StorageConforms(JobStorageConformanceTestCase testCase)
{
	await using var services = await AcmeStorageFixture.CreateServiceProviderAsync();
	await testCase.RunAsync(services);
}
```

The fixture should use the provider's normal public registration method, register a `FakeTimeProvider` as
`TimeProvider`, and isolate its database, schema, or key prefix. See the
[storage-provider testing documentation](https://immediateplatform.dev/docs/Immediate.Jobs/testing-jobs#test-a-storage-provider)
for isolation requirements and capability selection.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Testing jobs documentation](https://immediateplatform.dev/docs/Immediate.Jobs/testing-jobs)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
