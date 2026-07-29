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

	[Fact]
	public async Task InvalidReturnTypeDoesNotGenerate()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[Handler, Job]
			public sealed partial class GetUsersQuery
			{
				public record Query;
			
				private async ValueTask<int> Handle(
					Query _,
					CancellationToken token)
				{
					return 1;
				}
			}
			""",
			skippedSteps: ["Jobs"]
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.Dummy.GetUsersQuery.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(t => t.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task CronJobWithPayloadDoesNotGenerate()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[Handler, Job(Cron = "0 */5 * * * *")]
			public sealed partial class RecurringPayloadJob
			{
				public record Query(string Value);
			
				private ValueTask Handle(
					Query _,
					CancellationToken token)
				{
					return ValueTask.CompletedTask;
				}
			}
			""",
			skippedSteps: ["Jobs"]
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.Dummy.RecurringPayloadJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(t => t.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task InvalidCronExpressionDoesNotGenerate()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[Handler, Job(Cron = "not-a-cron")]
			public sealed partial class RecurringJob
			{
				private ValueTask Handle(
					EmptyJobRequest _,
					CancellationToken token)
				{
					return ValueTask.CompletedTask;
				}
			}
			""",
			skippedSteps: ["Jobs"]
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.Dummy.RecurringJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(t => t.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task InvalidTimeoutDoesNotGenerate()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[Handler, Job(Timeout = "00:00:00")]
			public sealed partial class TimeoutJob
			{
				private ValueTask Handle(
					EmptyJobRequest _,
					CancellationToken token)
				{
					return ValueTask.CompletedTask;
				}
			}
			""",
			skippedSteps: ["Jobs"]
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.Dummy.TimeoutJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(t => t.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task InvalidContextPayloadDoesNotGenerate()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			public interface IInvalidContext;
			
			public sealed class InvalidContextExtractor : JobContextExtractor<IInvalidContext>
			{
				public override string Key => "invalid";
				public override IInvalidContext? Capture() => null;
				public override void Restore(IInvalidContext context) { }
			}
			
			[Handler, Job, UsesJobContext<InvalidContextExtractor>]
			public sealed partial class ContextJob
			{
				private ValueTask Handle(
					EmptyJobRequest _,
					CancellationToken token)
				{
					return ValueTask.CompletedTask;
				}
			}
			""",
			skippedSteps: ["Jobs"]
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.Dummy.ContextJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(t => t.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}
}
