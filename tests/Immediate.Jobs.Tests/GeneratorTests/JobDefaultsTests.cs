using System.Globalization;

namespace Immediate.Jobs.Tests.GeneratorTests;

public sealed class JobDefaultsTests
{
	[Theory]
	[InlineData("", "\"assembly\"", "America/New_York", 5, "00:02:00")]
	[InlineData("Tags = new string[0]", "\"assembly\"", "America/New_York", 5, "00:02:00")]
	[InlineData("Tags = null", "\"assembly\"", "America/New_York", 5, "00:02:00")]
	[InlineData("Tags = new[] { \"job\" }, TimeZone = \"UTC\", MaxAttempts = 2, Timeout = \"00:00:30\"", "\"job\"", "UTC", 2, "00:00:30")]
	[InlineData("TimeZone = null, Timeout = null", "\"assembly\"", "UTC", 5, null)]
	public void JobSettingsOverrideAssemblyDefaults(string settings, string tags, string timeZone, int attempts, string? timeout)
	{
		var result = GeneratorTestHelper.RunGenerator(
			$$"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[assembly: JobDefaults(Tags = new[] { "assembly" }, TimeZone = "America/New_York", MaxAttempts = 5, Timeout = "00:02:00")]

			[Handler, Job({{settings}})]
			public sealed partial class WorkJob
			{
				private static ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		);

		var generated = result.GeneratedTrees.Single(tree => tree.FilePath.EndsWith("IJ..WorkJob.g.cs", StringComparison.Ordinal)).ToString();
		Assert.Contains($"Tags = [{tags}]", generated, StringComparison.Ordinal);
		Assert.Contains($"TimeZone = \"{timeZone}\"", generated, StringComparison.Ordinal);
		Assert.Contains($"MaxAttempts = {attempts.ToString(CultureInfo.InvariantCulture)}", generated, StringComparison.Ordinal);

		if (timeout is null)
			Assert.DoesNotContain("Timeout =", generated, StringComparison.Ordinal);
		else
			Assert.Contains($"TimeSpan.Parse(\"{timeout}\"", generated, StringComparison.Ordinal);
	}
}
