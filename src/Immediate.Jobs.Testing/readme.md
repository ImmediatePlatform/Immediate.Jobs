# Immediate.Jobs.Testing

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.Testing.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.Testing/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/testing-jobs)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

Deterministic testing tools for [Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/): a harness with fake
time, advance-and-drain helpers, capture-only typed schedulers, enqueue assertions, a single-job handler-pipeline runner,
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
	services.AddMyAppJobs(options => options.UseInMemory());
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
persisted-job queries and focused assertions for batches, continuations, and dependency cascades.

## Capture-only schedulers

Use `CaptureOnlyJobScheduler<TPayload>` when the subject under test only needs an `IJobScheduler<TPayload>` and no
handler execution or storage:

```csharp
var scheduler = new CaptureOnlyJobScheduler<SendWelcomeEmail.Payload>();
var payload = new SendWelcomeEmail.Payload(userId, "v2");
var handle = await scheduler.EnqueueAsync(
	payload,
	groupId: "tenant-a",
	cancellationToken: cancellationToken
);

var captured = scheduler.Last!;
Assert.Equal(handle.Id, captured.Id);
Assert.Equal(payload, captured.Payload);
Assert.Equal("tenant-a", captured.GroupId);

await scheduler.CancelAsync(handle, cancellationToken);
Assert.Contains(handle.Id, scheduler.CancelledIds);
```

`Captures` preserves call order and records the generated ID, payload, absolute due time, and normalized fair-queue group
ID. `CancelledIds` records cancellations of captured handles, and `Clear()` resets both collections.
`CaptureOnlyRecurringJobScheduler` provides the equivalent test double for named recurring schedules.

## Storage-provider conformance

Storage-provider authors can run the framework-neutral conformance catalog through their public DI registration path.
Give the catalog the exact capabilities implemented by the registered `IJobStorage`, create a fresh isolated service
provider per case, and pass that provider to `RunAsync`:

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
[storage conformance guide](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/docs/storage-tests.md) for
lifecycle requirements, NUnit adaptation, the built-in provider matrix, and stable case names.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Testing jobs documentation](https://immediateplatform.dev/docs/Immediate.Jobs/testing-jobs)
- [Storage conformance guide](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/docs/storage-tests.md)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
