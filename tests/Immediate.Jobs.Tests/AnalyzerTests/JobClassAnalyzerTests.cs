using Immediate.Jobs.Analyzers;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class JobClassAnalyzerTests
{
	[Fact]
	public async Task ValidConfigurationShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			[Handler, Job(
				MaxAttempts = 3,
				MaxConcurrency = 1,
				Backoff = BackoffStrategy.Fixed,
				BackoffBase = "00:00:05",
				OverlapPolicy = OverlapPolicy.Skip,
				Timeout = "00:01:00"

			)]
			public sealed partial class GetUsersQuery
			{
				public record Query;

				private async ValueTask Handle(Query _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task InvalidConfigurationShouldTriggerRepeatedly() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			[Handler, Job(
				MaxAttempts = -1,
				MaxConcurrency = -1,
				Backoff = (BackoffStrategy)5,
				BackoffBase = "-00:00:05",
				OverlapPolicy = (OverlapPolicy)5,
				Timeout = "-00:01:00"
			)]
			public sealed partial class {|IJOB0005:{|IJOB0005:{|IJOB0005:{|IJOB0005:{|IJOB0005:{|IJOB0005:GetUsersQuery|}|}|}|}|}|}
			{
				public record Query;

				private async ValueTask Handle(Query _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task UnparseableTimeSpanValuesShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			[Handler, Job(
				BackoffBase = "asdf",
				Timeout = "asdf"
			)]
			public sealed partial class {|IJOB0005:{|IJOB0005:GetUsersQuery|}|}
			{
				public record Query;

				private async ValueTask Handle(Query _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
