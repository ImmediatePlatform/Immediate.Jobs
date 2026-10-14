using Immediate.Jobs.Analyzers;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class JobDefaultsAnalyzerTests
{
	[Theory]
	[InlineData("Tags = new[] { \" \" }")]
	[InlineData("Tags = new[] { \"tag\", null }")]
	[InlineData("MaxAttempts = 0")]
	[InlineData("Timeout = \"invalid\"")]
	[InlineData("Timeout = \"PT10S\"")]
	[InlineData("Timeout = \"00:00:00\"")]
	[InlineData("TimeZone = \"\"")]
	public async Task InvalidDefaultsAreRejectedWithoutAnyJobs(string settings) =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobDefaultsAnalyzer>(
			$$"""
			using Immediate.Jobs.Shared;
			[assembly: {|IJOB0017:JobDefaults({{settings}})|}]
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Theory]
	[InlineData("")]
	[InlineData("Tags = null, Timeout = null, TimeZone = null")]
	[InlineData("Tags = new string[0]")]
	[InlineData("Timeout = \"00:00:10\"")]
	[InlineData("Tags = [\"email\", \"EMAIL\"], TimeZone = \"UTC\", MaxAttempts = 1, Timeout = \"00:00:10\"")]
	public async Task ValidDefaultsAreAccepted(string settings) =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<JobDefaultsAnalyzer>(
			$$"""
			using Immediate.Jobs.Shared;
			[assembly: JobDefaults({{settings}})]
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
