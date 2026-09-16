using Immediate.Jobs.Analyzers;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class CronSyntaxTests
{
	[Theory]
	[InlineData("invalid")]
	[InlineData("")]
	[InlineData("*")]
	[InlineData("0 0 * ſEP *")]
	public async Task InvalidSyntaxReportsFailure(string cron)
	{
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			$$"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, {|IJOB0007:Job(Cron = "{{cron}}")|}]
			public sealed partial class ReminderJob
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
	}

	[Theory]
	[InlineData("* * * * *")]
	[InlineData("@yearly")]
	[InlineData("FREQ=DAILY")]
	[InlineData("FREQ=WEEKLY;BYDAY=MO")]
	[InlineData("FREQ=MONTHLY;BYMONTHDAY=-1")]
	public async Task ValidSyntaxDoesNotReport(string cron)
	{
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			$$"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, Job(Cron = "{{cron}}")]
			public sealed partial class ReminderJob
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
	}

	[Theory]
	[InlineData("FREQ=DAILY;COUNT=5")]
	[InlineData("FREQ=DAILY;UNTIL=20000131T140000Z")]
	[InlineData("FREQ=WEEKLY;BYDAY=MO;COUNT=1")]
	public async Task ValidSyntaxWithEndReports(string cron)
	{
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			$$"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, {|IJOB0007:Job(Cron = "{{cron}}")|}]
			public sealed partial class ReminderJob
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
	}
}
