using Cronos;
using Immediate.Jobs.Analyzers;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class CronSyntaxTests
{
	// IJOB0007 is an error, so rejecting an expression the runtime would have accepted fails a
	// build over working code. Every case here is parsed with Cronos first, which is what actually
	// runs the schedule, and only then asserted to be accepted by the analyzer.
	[Theory]
	[InlineData("*/5 * * * *")]
	[InlineData("0 0,12 * * *")]
	[InlineData("0 0 1-15 * *")]
	[InlineData("0 0 * * MON")]
	[InlineData("0 0 * * mon-fri")]
	[InlineData("0 0 1 JAN *")]
	[InlineData("0 0 * * 7")]
	[InlineData("0 0 ? * MON")]
	[InlineData("0 0 1 * ?")]
	[InlineData("0 0 L * *")]
	[InlineData("0 0 LW * *")]
	[InlineData("0 0 L-3 * *")]
	[InlineData("0 0 15W * *")]
	[InlineData("0 0 * * 5L")]
	[InlineData("0 0 * * FRI#3")]
	[InlineData("0 */5 * * * *")]
	[InlineData("30 0 0 1 1 *")]
	[InlineData("@daily")]
	[InlineData("@every_minute")]
	public async Task ExpressionsTheRuntimeAcceptsShouldNotTrigger(string cron)
	{
		ArgumentNullException.ThrowIfNull(cron);
		_ = ParseWithRuntime(cron);

		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			JobWithCron(cron)
		).RunAsync(TestContext.Current.CancellationToken);
	}

	[Theory]
	[InlineData("not a cron")]
	[InlineData("0 0 * *")]
	[InlineData("60 0 * * *")]
	[InlineData("0 0 * * XYZ")]
	[InlineData("0 0 32 * *")]
	[InlineData("*/0 * * * *")]
	[InlineData("@nope")]
	public async Task ExpressionsTheRuntimeRejectsShouldTrigger(string cron)
	{
		ArgumentNullException.ThrowIfNull(cron);
		_ = Assert.ThrowsAny<Exception>(() => ParseWithRuntime(cron));

		await AnalyzerTestHelpers.CreateAnalyzerTest<JobClassAnalyzer>(
			JobWithCron(cron, expectDiagnostic: true)
		).RunAsync(TestContext.Current.CancellationToken);
	}

	private static CronExpression ParseWithRuntime(string cron)
	{
		var fields = cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
		return CronExpression.Parse(cron, fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
	}

	private static string JobWithCron(string cron, bool expectDiagnostic = false)
	{
		return
			$$"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, {{(
				expectDiagnostic
				? $"{{|IJOB0007:Job(Cron = \"{cron}\")|}}"
				: $"Job(Cron = \"{cron}\")"
			)}}]
			public sealed partial class ReminderJob
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			""";
	}
}
