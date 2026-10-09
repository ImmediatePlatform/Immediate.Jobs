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

	[Theory]
	[InlineData(" Invoice")]
	[InlineData("Invoice ")]
	[InlineData("\tInvoice")]
	[InlineData("Invoice\n")]
	[InlineData("\u00a0Invoice")]
	[InlineData("Invoice\u00a0")]
	public void ExplicitNameWithLeadingOrTrailingWhitespaceShouldNotGenerate(string name)
	{
		var result = GeneratorTestHelper.RunGenerator(
			$$"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;

			namespace Dummy;

			[Handler, Job(Name = {{Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(name, quote: true)}})]
			public sealed partial class NamedJob
			{
				private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken token) => ValueTask.CompletedTask;
			}
			""",
			skippedSteps: ["Jobs"]
		);

		Assert.DoesNotContain(result.GeneratedTrees, tree => tree.FilePath.EndsWith("IJ.Dummy.NamedJob.g.cs", StringComparison.Ordinal));
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

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.Dummy.Job.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.Dummy.Job.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(t => t.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}
}
