using Immediate.Jobs.Analyzers;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class InvalidAddToBatchCallAnalyzerTests
{
	[Fact]
	public async Task AddToBatchDetachedShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<InvalidAddToBatchCallAnalyzer>(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class RegularJob
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct)
				{
					await regularJobScheduler.EnqueueAsync(
						request,
						request.JobDetails!,
						{|IJOB0015:ContinuationOptions.Detached|}
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task AddToBatchDetachedWithDelayAndGroupIdShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<InvalidAddToBatchCallAnalyzer>(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class RegularJob
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct)
				{
					await regularJobScheduler.ScheduleAsync(
						request,
						request.JobDetails!,
						TimeSpan.Zero,
						"test",
						{|IJOB0015:ContinuationOptions.Detached|}
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task AddToBatchNoParameterShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<InvalidAddToBatchCallAnalyzer>(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class RegularJob
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct)
				{
					await regularJobScheduler.EnqueueAsync(
						request,
						request.JobDetails!
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task AddToBatchBeforeContinuationsShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<InvalidAddToBatchCallAnalyzer>(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class RegularJob
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct)
				{
					await regularJobScheduler.EnqueueAsync(
						request,
						request.JobDetails!,
						ContinuationOptions.BeforeContinuations
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task AddToBatchBesideContinuationsShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<InvalidAddToBatchCallAnalyzer>(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class RegularJob
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct)
				{
					await regularJobScheduler.EnqueueAsync(
						request,
						request.JobDetails!,
						ContinuationOptions.BesideContinuations
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task AddToBatchBeforeContinuationsWithGroupIdShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<InvalidAddToBatchCallAnalyzer>(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class RegularJob
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct)
				{
					await regularJobScheduler.EnqueueAsync(
						request,
						request.JobDetails!,
						"test",
						ContinuationOptions.BeforeContinuations
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task AddToBatchBeforeContinuationsWithDelayShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<InvalidAddToBatchCallAnalyzer>(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class RegularJob
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct)
				{
					await regularJobScheduler.ScheduleAsync(
						request,
						request.JobDetails!,
						TimeSpan.Zero,
						ContinuationOptions.BeforeContinuations
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task AddToBatchBeforeContinuationsWithDelayAndGroupIdShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<InvalidAddToBatchCallAnalyzer>(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class RegularJob
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken ct)
				{
					await regularJobScheduler.ScheduleAsync(
						request,
						request.JobDetails!,
						TimeSpan.Zero,
						"test",
						ContinuationOptions.BeforeContinuations
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
