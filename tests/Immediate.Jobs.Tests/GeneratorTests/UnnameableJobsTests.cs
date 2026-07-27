namespace Immediate.Jobs.Tests.GeneratorTests;

public sealed class UnnameableJobsTests
{
	[Fact]
	public async Task ClassNameLeavingNothingToDeriveShouldNotGenerate()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, Job]
			public sealed partial class Job
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken) =>
					ValueTask.CompletedTask;
			}
			""",
			skippedSteps: ["Jobs"]
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.Dummy.Job.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(t => t.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task ExplicitNameShouldRescueAnUnderivableClassName()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, Job(Name = "the-job")]
			public sealed partial class Job
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken) =>
					ValueTask.CompletedTask;
			}
			"""
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}
}
