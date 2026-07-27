namespace Immediate.Jobs.Tests.GeneratorTests;

public sealed class AddJobsTests
{
	[Theory]
	[MemberData(nameof(Frameworks))]
	public async Task ValidAddJobsMethod(string framework)
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			using Microsoft.Extensions.DependencyInjection;
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

		_ = await Verify(result)
			.UseParameters(framework);
	}

	[Theory]
	[MemberData(nameof(Frameworks))]
	public async Task ServiceCollectionExtensionsUsesQueuesAndTaggedRegistrations(string framework)
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[QueueDefinition(Priority = 10, Concurrency = 1)]
			public sealed class CriticalQueue;

			public sealed class WorkContextExtractor : IJobContextExtractor<string>
			{
				public string Key => "work";
				public string? Capture() => null;
				public void Restore(string context) { }
			}

			[Handler(Tags = ["critical"]), Job, UsesQueue<CriticalQueue>, UsesJobContext<WorkContextExtractor>]
			public sealed partial class WorkJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH..WorkJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ..WorkJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await Verify(result).UseParameters(framework);
	}

	public static TheoryData<string> Frameworks =>
		[Utility.ReferenceAssemblies.TargetFramework];
}
