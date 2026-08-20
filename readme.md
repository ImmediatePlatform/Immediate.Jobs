# Immediate.Jobs

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs/)
[![GitHub release](https://img.shields.io/github/release/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/releases/)
[![GitHub license](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)
[![GitHub issues](https://img.shields.io/github/issues/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/issues/)
[![GitHub issues closed](https://img.shields.io/github/issues-closed/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/issues?q=is%3Aissue+is%3Aclosed)
[![GitHub Actions](https://github.com/ImmediatePlatform/Immediate.Jobs/actions/workflows/build.yml/badge.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/actions)
[![Coverage Status](https://coveralls.io/repos/github/ImmediatePlatform/Immediate.Jobs/badge.svg)](https://coveralls.io/github/ImmediatePlatform/Immediate.Jobs)
[![Docs](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)

Immediate.Jobs is a reflection-free background job scheduler for .NET 8+ built on
[Immediate.Handlers](https://github.com/ImmediatePlatform/Immediate.Handlers). A job is a `[Handler]` whose request can
also be durably enqueued; a Roslyn source generator emits its typed scheduler, payload metadata, and dependency-injection
registrations at compile time.

> [!IMPORTANT]
> Immediate.Jobs provides **at-least-once delivery**. Every handler that performs externally visible work must be
> idempotent. The in-memory provider is single-node, non-durable, and intended only for development, tests, and
> non-critical work.

## Quick start

Install the core package:

```console
dotnet add package Immediate.Jobs --prerelease
```

Define a job using the same handler model as Immediate.Handlers, then inject its generated scheduler:

```csharp
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

[Handler, Job(Name = "send-welcome-email", MaxAttempts = 5)]
public sealed partial class SendWelcomeEmail(IEmailSender sender)
{
	public sealed record Payload(Guid UserId, string Template);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		new(sender.SendAsync(payload.UserId, payload.Template, cancellationToken));
}

public sealed class SignupService(SendWelcomeEmail.Scheduler welcomeEmail)
{
	public ValueTask<JobHandle> EnqueueAsync(Guid userId, CancellationToken cancellationToken) =>
		welcomeEmail.EnqueueAsync(new(userId, "v2"), cancellationToken);
}
```

Register the generated handlers and jobs methods in `Program.cs`, in that order. For an assembly named `MyApp`, these
are `AddMyAppHandlers()` and `AddMyAppJobs()`.

## Packages

Each package has focused installation and configuration guidance:

| Package | Purpose |
|---|---|
| [Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/) | Core scheduler, source generator, execution engine, and in-memory provider |
| [Immediate.Jobs.EntityFrameworkCore](src/Immediate.Jobs.EntityFrameworkCore/readme.md) | Durable EF Core storage for PostgreSQL, SQLite, and SQL Server |
| [Immediate.Jobs.LinqToDB](src/Immediate.Jobs.LinqToDB/readme.md) | Durable LinqToDB storage for PostgreSQL, SQLite, and SQL Server |
| [Immediate.Jobs.Redis](src/Immediate.Jobs.Redis/readme.md) | Distributed Redis queue and recurring storage |
| [Immediate.Jobs.Dashboard](src/Immediate.Jobs.Dashboard/readme.md) | Embedded monitoring dashboard and HTTP API |
| [Immediate.Jobs.Testing](src/Immediate.Jobs.Testing/readme.md) | Deterministic test harness, test doubles, assertions, and provider conformance tests |
| [Immediate.Jobs.NodaTime](src/Immediate.Jobs.NodaTime/readme.md) | NodaTime scheduling overloads and job payload serialization |

The SQL providers support batches, continuations, and fair scheduling between tenant groups. Redis does not support
those features in the current release. See
[Queues and fairness](https://immediateplatform.dev/docs/Immediate.Jobs/queues-and-fairness) and
[Batches and continuations](https://immediateplatform.dev/docs/Immediate.Jobs/batches-and-continuations) for details.

## Samples and documentation

The [online documentation](https://immediateplatform.dev/docs/Immediate.Jobs/introduction) covers the complete API.
The [Aspire sample](samples/Aspire/readme.md) runs the EF Core provider against an Aspire-managed PostgreSQL container,
exports logs, traces, metrics, and health status, and exposes the Immediate.Jobs dashboard.

## Benchmarks

The repository includes BenchmarkDotNet comparisons with TickerQ, Hangfire MemoryStorage, and Quartz.NET. In addition
to enqueue, direct dispatch, and startup, the suite covers concurrent throughput, cron expressions, delegate invocation,
job creation, serialization, and startup registration. These are microbenchmarks of deliberately different framework
APIs—not end-to-end durability or worker-latency measurements—so run them on the deployment target before drawing
conclusions.

### Results

The tables below are the historical `ShortRun` results from 21 July 2026: BenchmarkDotNet 0.15.8, .NET 8.0.22 Arm64
RyuJIT, Apple M3 Pro with 12 cores, macOS 26.5. Each result uses one launch, three warmup iterations, and three
measurement iterations. Ratios use Immediate.Jobs as the baseline. The expanded TickerQ suite targets .NET 10 and does
not yet have checked-in results.

#### EnqueueAsync

| Framework | Mean | Ratio | Allocated | Allocation ratio |
|---|---:|---:|---:|---:|
| Immediate.Jobs | 3.796 μs | 1.00 | 5.07 KB | 1.00 |
| Hangfire | 16.122 μs | 4.25 | 14.49 KB | 2.86 |
| Quartz.NET | 17.288 μs | 4.56 | 6.34 KB | 1.25 |

#### Direct dispatch

| Framework | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Immediate.Jobs | 0.9994 ns | 1.00 | 0 B |
| Hangfire | 28.0701 ns | 28.09 | 32 B |
| Quartz.NET | 0.0521 ns | 0.05 | 0 B |

The Immediate.Jobs and Quartz.NET dispatch operations are effectively below the benchmark's reliable measurement floor.
Treat their sub-nanosecond values as "no measurable dispatch overhead" rather than literal timing precision.

#### Scheduler construction

| Framework | Mean | Ratio | Allocated | Allocation ratio |
|---|---:|---:|---:|---:|
| Immediate.Jobs | 393.60 ns | 1.00 | 649 B | 1.00 |
| Hangfire | 7,765.75 ns | 19.77 | 3,104 B | 4.78 |
| Quartz.NET | 11.67 ns | 0.03 | 136 B | 0.21 |

The checked-in reports are available for
[enqueue](BenchmarkDotNet.Artifacts/results/Immediate.Jobs.Benchmarks.EnqueueBenchmarks-report-github.md),
[direct dispatch](BenchmarkDotNet.Artifacts/results/Immediate.Jobs.Benchmarks.DispatchBenchmarks-report-github.md), and
[scheduler construction](BenchmarkDotNet.Artifacts/results/Immediate.Jobs.Benchmarks.StartupBenchmarks-report-github.md).

Run the complete suite with:

```console
dotnet run --project benchmarks/Immediate.Jobs.Benchmarks -c Release -- --filter '*'
```

## License

Immediate.Jobs is licensed under the [MIT License](license.txt).
