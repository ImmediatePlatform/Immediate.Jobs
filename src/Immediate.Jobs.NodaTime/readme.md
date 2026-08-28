# Immediate.Jobs.NodaTime

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.NodaTime.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.NodaTime/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/nodatime)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

NodaTime scheduling overloads and job payload serialization for
[Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/).

## Installation

```console
dotnet add package Immediate.Jobs --prerelease
dotnet add package Immediate.Jobs.NodaTime --prerelease
```

Immediate.Jobs reports diagnostic `IJOB0004` when a generated job payload uses NodaTime types without this package.

## Scheduling with NodaTime

Import `Immediate.Jobs.NodaTime` to schedule with `Duration`, `Instant`, and `DateTimeZone`:

```csharp
using Immediate.Jobs.NodaTime;
using NodaTime;

JobHandle delayed = await scheduler.ScheduleAsync(
	payload,
	Duration.FromMinutes(15),
	cancellationToken);

JobHandle scheduled = await scheduler.ScheduleAsync(
	payload,
	SystemClock.Instance.GetCurrentInstant() + Duration.FromHours(1),
	cancellationToken);

JobHandle continuation = await scheduler.ScheduleAfterAsync(
	payload,
	scheduled,
	Duration.FromMinutes(5),
	cancellationToken: cancellationToken);

await recurringScheduler.AddOrUpdateRecurringAsync(
	"tenant-cleanup",
	"0 3 * * *",
	DateTimeZoneProviders.Tzdb["Europe/Vienna"],
	cancellationToken);
```

The same method family covers fair-queue group IDs and workflow construction. Inside an open batch, `Schedule` accepts
an `Instant` or `Duration`; `ScheduleAfter` applies a `Duration` after one or several parent jobs reach the selected
outcome:

```csharp
await using var batch = batches.Begin();

var root = scheduler.Schedule(payload, batch, SystemClock.Instance.GetCurrentInstant());
var child = scheduler.ScheduleAfter(
	payload,
	root,
	Duration.FromMinutes(10));

_ = await batch.CommitAsync(cancellationToken);
```

NodaTime overloads also accept `JobDetails` for work added by a running job. They follow the core scheduler's
`ContinuationOptions` behavior.

## Payload serialization

Immediate.Jobs has its own `IJobSerializer`; it does not use ASP.NET Core's JSON options. When an application owns its
JSON settings, create dedicated `JsonSerializerOptions` and register a `NodaTimeJobSerializer` before
`AddMyAppJobs()`:

```csharp
using System.Text.Json;
using Immediate.Jobs.NodaTime;
using Immediate.Jobs.Shared.Interfaces;
using NodaTime;

var jobJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
	WriteIndented = false,
	PropertyNameCaseInsensitive = true,
};

builder.Services.AddMyAppHandlers();

builder.Services.AddSingleton<IJobSerializer>(
	new NodaTimeJobSerializer(
		jobJsonOptions,
		DateTimeZoneProviders.Tzdb
	)
);

builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage.UseInMemory());
```

`NodaTimeJobSerializer` adds NodaTime's System.Text.Json converters to the supplied options. Generated schedulers and
invokers build their AOT-safe payload metadata from those same options, so custom naming, converters, and other JSON
settings apply consistently when a job is written and read.

Register the serializer before `AddMyAppJobs()`. Jobs registers its default serializer only when no `IJobSerializer`
has already been registered.

If web JSON defaults and the TZDB time-zone provider are sufficient, `AddImmediateJobsNodaTime()` is a shorter
convenience registration:

```csharp
builder.Services.AddMyAppHandlers();
builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage.UseInMemory());
builder.Services.AddImmediateJobsNodaTime();
```

The convenience method replaces the default `IJobSerializer`; do not combine it with the explicit serializer
registration above. Pass an `IDateTimeZoneProvider` to either `NodaTimeJobSerializer` or
`AddImmediateJobsNodaTime(...)` when the application does not use TZDB.

Every worker that reads stored jobs must use the same serializer options and a time-zone provider that recognizes the
same IDs. Treat these settings as part of the durable payload contract: changing converters or JSON names while jobs are
still queued can make their stored payloads unreadable.

Call `JsonSerializerOptions.UseNodaTime(...)` only when JSON options outside the Jobs serializer need the same NodaTime
converters.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [NodaTime integration documentation](https://immediateplatform.dev/docs/Immediate.Jobs/nodatime)
- [NodaTime documentation](https://nodatime.org/)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
