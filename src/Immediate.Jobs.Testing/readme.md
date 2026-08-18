# Immediate.Jobs.Testing

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.Testing.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.Testing/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

Deterministic testing tools for [Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/): a harness with fake
time, advance-and-drain helpers, capture-only typed schedulers, enqueue assertions, a single-job handler-pipeline runner,
and storage-provider conformance tests.

## Installation

```console
dotnet add package Immediate.Jobs
dotnet add package Immediate.Jobs.Testing
```

## Deterministic job tests

`JobTestHarness` hosts the production scheduling service with in-memory storage and a controllable clock, without
starting background threads. Register the generated jobs, handlers, and their dependencies in its service collection:

```csharp
await using var harness = new JobTestHarness(services =>
{
	services.AddMyAppJobs();
	services.AddMyAppHandlers();
	services.AddSingleton<IEmailSender, RecordingEmailSender>();
});

var scheduler = harness.Services.GetRequiredService<SendWelcomeEmail.Scheduler>();
var handle = await scheduler.ScheduleAsync(
	new(userId, "v2"),
	TimeSpan.FromHours(1));

var enqueued = await harness.AssertEnqueuedAsync<SendWelcomeEmail.Payload>(
	handle,
	JobState.Scheduled);

await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromHours(1));
```

Delayed work, scheduled occurrences, timeouts, and backoff tests do not need wall-clock sleeps. The harness also exposes
persisted-job queries and focused assertions for batches, continuations, and dependency cascades.

## Capture-only schedulers

Use `CaptureOnlyJobScheduler<TPayload>` when the subject under test only needs an `IJobScheduler<TPayload>` and no
handler execution or storage:

```csharp
var scheduler = new CaptureOnlyJobScheduler<SendWelcomeEmail.Payload>();
await scheduler.EnqueueAsync(new(userId, "v2"), cancellationToken);

var captured = scheduler.Last;
Assert.Equal(userId, captured?.Payload.UserId);
```

`Captures` preserves call order and records the generated ID, payload, absolute due time, and normalized fair-queue group
ID. `CaptureOnlyRecurringJobScheduler` provides the equivalent test double for named recurring schedules.

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
- [Storage conformance guide](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/docs/storage-tests.md)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
