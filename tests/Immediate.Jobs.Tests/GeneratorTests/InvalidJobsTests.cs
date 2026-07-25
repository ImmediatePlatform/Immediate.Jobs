namespace Immediate.Jobs.Tests.GeneratorTests;

public sealed class InvalidJobsTests
{
	[Fact]
	public async Task MissingHandlerAttributeShouldError()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[Job]
			public sealed partial class GetUsersQuery
			{
				public record Query;
			
				private async ValueTask Handle(
					Query _,
					CancellationToken token)
				{
				}
			}
			""",
			skippedSteps: ["Jobs"]
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(t => t.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}
}
