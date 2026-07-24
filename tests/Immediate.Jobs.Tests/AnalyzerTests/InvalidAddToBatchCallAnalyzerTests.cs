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
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(NoPayload request, CancellationToken ct)
				{
					await regularJobScheduler.AddToBatchAsync(
						request.JobDetails!,
						request,
						{|IJOB0020:ContinuationOptions.Detached|}
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task AddToBatchDetachedOutOfOrderShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<InvalidAddToBatchCallAnalyzer>(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class RegularJob
			{
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(NoPayload request, CancellationToken ct)
				{
					await regularJobScheduler.AddToBatchAsync(
						{|IJOB0020:options: ContinuationOptions.Detached|},
						current: request.JobDetails!,
						payload: request
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
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(NoPayload request, CancellationToken ct)
				{
					await regularJobScheduler.AddToBatchAsync(
						request.JobDetails!,
						request
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
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job]
			public sealed partial class BatchedJob(
				RegularJob.Scheduler regularJobScheduler
			)
			{
				private async ValueTask HandleAsync(NoPayload request, CancellationToken ct)
				{
					await regularJobScheduler.AddToBatchAsync(
						request.JobDetails!,
						request,
						ContinuationOptions.BeforeContinuations
					);
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
