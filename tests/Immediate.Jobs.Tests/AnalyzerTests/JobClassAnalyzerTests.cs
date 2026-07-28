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

			[Handler, {|IJOB0005:{|IJOB0005:{|IJOB0005:{|IJOB0005:{|IJOB0005:{|IJOB0005:Job(
				MaxAttempts = -1,
				MaxConcurrency = -1,
				Backoff = (BackoffStrategy)5,
				BackoffBase = "-00:00:05",
				OverlapPolicy = (OverlapPolicy)5,
				Timeout = "-00:01:00"
			)|}|}|}|}|}|}]
			public sealed partial class GetUsersQuery
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

			[Handler, {|IJOB0005:{|IJOB0005:Job(
				BackoffBase = "asdf",
				Timeout = "asdf"
			)|}|}]
			public sealed partial class GetUsersQuery
			{
				public record Query;

				private async ValueTask Handle(Query _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task ValidCronShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			[Handler, Job(
				Cron = "1 2 3 4 5 6",
				TimeZone = "UTC"
			)]
			public sealed partial class GetUsersQuery
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task CronWithInvalidRequestTypeShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			[Handler, Job(
				Cron = "1 2 3 4 5 6",
				TimeZone = "UTC"
			)]
			public sealed partial class {|IJOB0006:GetUsersQuery|}
			{
				public record Query;

				private async ValueTask Handle(Query _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task InvalidCronConfigurationTriggers() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			[Handler, {|IJOB0007:{|IJOB0007:Job(
				Cron = "3 4 5 6",
				TimeZone = ""
			)|}|}]
			public sealed partial class GetUsersQuery
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task ClassNameLeavingNothingToDeriveShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, Job]
			public sealed partial class {|IJOB0008:Job|}
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task ClassNameDerivingOnlyPunctuationShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, Job]
			public sealed partial class {|IJOB0008:__Job|}
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task ExplicitNameWithoutContentShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, Job(Name = "  ")]
			public sealed partial class {|IJOB0008:GetUsersQuery|}
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task ExplicitNameShouldRescueAnUnderivableClassName() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, Job(Name = "the-job")]
			public sealed partial class Job
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task InvalidReturnTypeShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, Job(Name = "my-name")]
			public sealed partial class Job
			{
				private async ValueTask<int> {|IJOB0012:Handle|}(EmptyJobRequest _, CancellationToken token) => 1;
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
