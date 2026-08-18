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

JobHandle scheduled = await scheduler.ScheduleAtAsync(
	payload,
	SystemClock.Instance.GetCurrentInstant() + Duration.FromHours(1),
	cancellationToken);

await recurringScheduler.AddOrUpdateRecurringAsync(
	"tenant-cleanup",
	"0 3 * * *",
	DateTimeZoneProviders.Tzdb["Europe/Vienna"],
	cancellationToken);
```

NodaTime overloads also cover fair-queue group IDs, batch due times, and delayed continuations.

## Payload serialization

Call `AddImmediateJobsNodaTime` after generated jobs registration when job payloads or captured contexts contain NodaTime
values:

```csharp
builder.Services.AddMyAppHandlers();
builder.Services.AddMyAppJobs(options => options.UseInMemory());
builder.Services.AddImmediateJobsNodaTime();
```

The default uses the TZDB time-zone provider. Supply another `IDateTimeZoneProvider` when required:

```csharp
IDateTimeZoneProvider timeZoneProvider = DateTimeZoneProviders.Tzdb;
builder.Services.AddSingleton(timeZoneProvider);
builder.Services.AddImmediateJobsNodaTime(timeZoneProvider);
```

Every worker that reads stored jobs must use a provider that recognizes the same time-zone IDs.

For application-owned serializer settings, construct `NodaTimeJobSerializer`; its constructor adds the NodaTime
converters:

```csharp
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
	WriteIndented = false,
};

IJobSerializer serializer = new NodaTimeJobSerializer(
	jsonOptions,
	DateTimeZoneProviders.Tzdb
);
```

The package uses NodaTime's System.Text.Json converters while retaining the generated metadata path used by
Immediate.Jobs for trimming and Native AOT. If replacing `IJobSerializer` yourself, register the configured serializer
after jobs registration. Call `JsonSerializerOptions.UseNodaTime(...)` when application-owned JSON options need the same
converters outside the jobs serializer.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [NodaTime integration documentation](https://immediateplatform.dev/docs/Immediate.Jobs/nodatime)
- [NodaTime documentation](https://nodatime.org/)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
