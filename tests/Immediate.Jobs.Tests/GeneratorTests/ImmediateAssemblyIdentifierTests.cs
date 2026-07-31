namespace Immediate.Jobs.Tests.GeneratorTests;

public sealed class ImmediateAssemblyIdentifierTests
{
	[Theory]
	[MemberData(nameof(AddJobsTests.Frameworks), MemberType = typeof(AddJobsTests))]
	public async Task ImmediateAssemblyIdentifierOverridesAssemblyName(string framework)
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			[assembly: ImmediateAssemblyIdentifier("Custom")]

			namespace Dummy;

			[Handler, Job]
			public static partial class GetUsersQuery
			{
				public record Query;

				private static async ValueTask HandleAsync(
					Query _,
					CancellationToken token) { }
			}
			"""
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.Dummy.GetUsersQuery.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.Dummy.GetUsersQuery.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(t => t.FilePath.Replace('\\', '/'))
		);

		_ = await Verify(result)
			.UseParameters(framework);
	}
}
