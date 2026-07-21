using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Immediate.Jobs.Tests.GeneratorTests;

public sealed class ImmediateJobsGeneratorTests
{
	[Theory]
	[MemberData(nameof(Frameworks))]
	public async Task ServiceCollectionExtensionsUsesQueuesAndTaggedRegistrations(string framework)
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[QueueDefinition(Priority = 10, Concurrency = 1)]
			public sealed class CriticalQueue;

			public sealed class WorkContextExtractor : IJobContextExtractor<string>
			{
				public string Key => "work";
				public ValueTask<string?> CaptureAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
				public ValueTask RestoreAsync(string context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}

			[Handler(Tags = ["critical"]), Job, UsesQueue<CriticalQueue>, UsesJobContext<WorkContextExtractor>]
			public sealed partial class WorkJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH..WorkJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJOB.global..WorkJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJOB.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await GeneratorTestHelper.VerifyRegistrations(result).UseParameters(framework);
	}

	public static TheoryData<string> Frameworks => [GeneratorTestHelper.TargetFramework];

	[Fact]
	public async Task PayloadJobGeneratesTypedSchedulerDirectInvokerAndRegistrations()
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;

			namespace Example;

			[Handler, Job("send-email", MaxAttempts = 5, Timeout = "00:02:00")]
			public sealed partial class SendEmailJob
			{
				public sealed record Payload(Guid UserId, string Template) : IJobRequest { public JobDetails? JobDetails { get; set; } }
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		GeneratorTestHelper.AssertGeneratedTrees(
			result,
			"IH.Example.SendEmailJob.g.cs",
			"IJOB.global..Example.SendEmailJob.g.cs"
		);
		_ = await GeneratorTestHelper.VerifyJob(result);
	}

	[Fact]
	public async Task PlainRequestGeneratesJobWithoutJobDetailsAssignment()
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class PlainRequestJob
			{
				public sealed record Payload(string Value);
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..PlainRequestJob.g.cs", "IJOB.global..PlainRequestJob.g.cs");
		_ = await GeneratorTestHelper.VerifyJob(result);
	}

	[Fact]
	public async Task ExplicitJobDetailsOnValueTypeUsesConstrainedByReferenceAssignment()
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			public record struct StructPayload(string Value) : IJobRequest
			{
				JobDetails? IJobRequest.JobDetails { get; set; }
			}

			[Handler, Job]
			public sealed partial class StructJob
			{
				private ValueTask HandleAsync(StructPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..StructJob.g.cs", "IJOB.global..StructJob.g.cs");
		_ = await GeneratorTestHelper.VerifyJob(result);
	}

	[Fact]
	public async Task CronJobGeneratesPayloadlessRecurringScheduler()
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job(Cron = "0 */5 * * * *")]
			public sealed partial class CleanupSessionsJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		GeneratorTestHelper.AssertGeneratedTrees(
			result,
			"IH..CleanupSessionsJob.g.cs",
			"IJOB.global..CleanupSessionsJob.g.cs"
		);
		_ = await GeneratorTestHelper.VerifyJob(result);
	}

	[Fact]
	public async Task PayloadlessJobWithoutCronGeneratesDynamicRecurringScheduler()
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class TenantCleanupJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..TenantCleanupJob.g.cs", "IJOB.global..TenantCleanupJob.g.cs");
		_ = await GeneratorTestHelper.VerifyJob(result);
	}

	[Fact]
	public async Task InvokerDelegatesExecutionToImmediateHandlersPipeline()
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class WorkJob
			{
				public sealed record Payload(string Value) : IJobRequest { public JobDetails? JobDetails { get; set; } }
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..WorkJob.g.cs", "IJOB.global..WorkJob.g.cs");
		_ = await GeneratorTestHelper.VerifyJob(result);
	}

	[Fact]
	public async Task ContextExtractorsGenerateOrderedCaptureRestoreMetadataAndScopedRegistrations()
	{
		var source = """
			#nullable enable
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;

			public enum Region { Europe, America }
			public sealed record UsageContext(Guid UserId, Region Region);
			public sealed class CorrelationContext
			{
				public CorrelationContext(string value) => Value = value;
				public string Value { get; }
			}

			public sealed class UsageContextExtractor : IJobContextExtractor<UsageContext>
			{
				public string Key => "usage";
				public ValueTask<UsageContext?> CaptureAsync(CancellationToken cancellationToken) => ValueTask.FromResult<UsageContext?>(new(Guid.Empty, Region.Europe));
				public ValueTask RestoreAsync(UsageContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}

			public sealed class CorrelationExtractor : IJobContextExtractor<CorrelationContext>
			{
				public string Key => "correlation";
				public ValueTask<CorrelationContext?> CaptureAsync(CancellationToken cancellationToken) => ValueTask.FromResult<CorrelationContext?>(new("abc"));
				public ValueTask RestoreAsync(CorrelationContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}

			[UsesJobContext<UsageContextExtractor>]
			[UsesJobContext<CorrelationExtractor>]
			public sealed class WebJobAttribute : Attribute;

			[Handler, Job, UsesJobContext<CorrelationExtractor>, WebJob]
			public sealed partial class ContextualJob
			{
				public sealed record Payload(string Message) : IJobRequest { public JobDetails? JobDetails { get; set; } }
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..ContextualJob.g.cs", "IJOB.global..ContextualJob.g.cs");
		_ = await GeneratorTestHelper.VerifyJob(result);
	}

	[Fact]
	public async Task JobWithoutContextDoesNotEmitCaptureOrRestoreCode()
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class PlainJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..PlainJob.g.cs", "IJOB.global..PlainJob.g.cs");
		_ = await GeneratorTestHelper.VerifyJob(result);
	}

	[Fact]
	public async Task NodaTimeContextUsesConfiguredGeneratedMetadata()
	{
		var source = """
			#nullable enable
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using NodaTime;
			using System.Threading;
			using System.Threading.Tasks;
			public sealed record ClockContext(Instant Now);
			public sealed class ClockExtractor : IJobContextExtractor<ClockContext>
			{
				public string Key => "clock";
				public ValueTask<ClockContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<ClockContext?>(new(Instant.FromUnixTimeTicks(1)));
				public ValueTask RestoreAsync(ClockContext context, CancellationToken ct) => ValueTask.CompletedTask;
			}
			[Handler, Job, UsesJobContext<ClockExtractor>]
			public sealed partial class ClockJob
			{ private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""";

		var result = GeneratorTestHelper.RunGeneratorWithNodaTime(source);
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..ClockJob.g.cs", "IJOB.global..ClockJob.g.cs");
		_ = await GeneratorTestHelper.VerifyJob(result);
	}

	[Fact]
	public void EditingContextShapeInvalidatesOnlyOwningJobModel()
	{
		var contextBefore = """
			#nullable enable
			using Immediate.Jobs.Shared;
			using System.Threading;
			using System.Threading.Tasks;
			public sealed record AmbientContext(string Value);
			public sealed class AmbientExtractor : IJobContextExtractor<AmbientContext>
			{
				public string Key => "ambient";
				public ValueTask<AmbientContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<AmbientContext?>(new("one"));
				public ValueTask RestoreAsync(AmbientContext context, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""";
		var contextAfter = """
			#nullable enable
			using Immediate.Jobs.Shared;
			using System.Threading;
			using System.Threading.Tasks;
			public sealed record AmbientContext(string Value, int Version);
			public sealed class AmbientExtractor : IJobContextExtractor<AmbientContext>
			{
				public string Key => "ambient";
				public ValueTask<AmbientContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<AmbientContext?>(new("one", 2));
				public ValueTask RestoreAsync(AmbientContext context, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""";
		var owner = """
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job, UsesJobContext<AmbientExtractor>] public sealed partial class ContextOwnerJob
			{ private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""";
		var unrelated = """
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job] public sealed partial class UnrelatedJob
			{ private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""";

		var compilation = GeneratorTestHelper.CreateCompilation(
			("Context.cs", contextBefore),
			("Owner.cs", owner),
			("Unrelated.cs", unrelated)
		);
		var driver = GeneratorTestHelper.RunAndAssert(GeneratorTestHelper.CreateDriver(), compilation);
		var contextTree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "Context.cs");
		var replacement = CSharpSyntaxTree.ParseText(
			contextAfter,
			GeneratorTestHelper.ParseOptions,
			path: "Context.cs",
			cancellationToken: TestContext.Current.CancellationToken
		);
		compilation = compilation.ReplaceSyntaxTree(contextTree, replacement);

		driver = GeneratorTestHelper.RunAndAssert(driver, compilation);
		var jobsResult = driver.GetRunResult().Results.Single(result => result.TrackedSteps.ContainsKey("Jobs"));
		var outputs = jobsResult.TrackedSteps["Jobs"].SelectMany(step => step.Outputs).ToArray();
		var (_, ownerReason) = Assert.Single(outputs, output => output.Value?.ToString()?.Contains("ClassName = ContextOwnerJob", StringComparison.Ordinal) == true);
		var (_, unrelatedReason) = Assert.Single(outputs, output => output.Value?.ToString()?.Contains("ClassName = UnrelatedJob", StringComparison.Ordinal) == true);

		Assert.Equal(IncrementalStepRunReason.Modified, ownerReason);
		Assert.Contains(unrelatedReason, new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged });
	}

	[Fact]
	public void EditingExtractorContractInvalidatesOnlyReferencingJobModel()
	{
		var contexts = """
			public sealed record FirstAmbientContext(string Value);
			public sealed record SecondAmbientContext(string Value);
			""";
		var extractorBefore = """
			#nullable enable
			using Immediate.Jobs.Shared; using System.Threading; using System.Threading.Tasks;
			public sealed class ChangingExtractor : IJobContextExtractor<FirstAmbientContext>
			{
				public string Key => "ambient";
				public ValueTask<FirstAmbientContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<FirstAmbientContext?>(new("one"));
				public ValueTask RestoreAsync(FirstAmbientContext context, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""";
		var extractorAfter = """
			#nullable enable
			using Immediate.Jobs.Shared; using System.Threading; using System.Threading.Tasks;
			public sealed class ChangingExtractor : IJobContextExtractor<SecondAmbientContext>
			{
				public string Key => "ambient";
				public ValueTask<SecondAmbientContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<SecondAmbientContext?>(new("two"));
				public ValueTask RestoreAsync(SecondAmbientContext context, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""";
		var owner = """
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job, UsesJobContext<ChangingExtractor>] public sealed partial class ExtractorOwnerJob
			{ private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""";
		var unrelated = """
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job] public sealed partial class OtherJob
			{ private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""";

		var compilation = GeneratorTestHelper.CreateCompilation(
			("Contexts.cs", contexts),
			("Extractor.cs", extractorBefore),
			("Owner.cs", owner),
			("Other.cs", unrelated)
		);
		var driver = GeneratorTestHelper.RunAndAssert(GeneratorTestHelper.CreateDriver(), compilation);
		var extractorTree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "Extractor.cs");
		var replacement = CSharpSyntaxTree.ParseText(
			extractorAfter,
			GeneratorTestHelper.ParseOptions,
			path: "Extractor.cs",
			cancellationToken: TestContext.Current.CancellationToken
		);
		compilation = compilation.ReplaceSyntaxTree(extractorTree, replacement);

		driver = GeneratorTestHelper.RunAndAssert(driver, compilation);
		var jobsResult = driver.GetRunResult().Results.Single(result => result.TrackedSteps.ContainsKey("Jobs"));
		var outputs = jobsResult.TrackedSteps["Jobs"].SelectMany(step => step.Outputs).ToArray();
		var (_, ownerReason) = Assert.Single(outputs, output => output.Value?.ToString()?.Contains("ClassName = ExtractorOwnerJob", StringComparison.Ordinal) == true);
		var (_, unrelatedReason) = Assert.Single(outputs, output => output.Value?.ToString()?.Contains("ClassName = OtherJob", StringComparison.Ordinal) == true);

		Assert.Equal(IncrementalStepRunReason.Modified, ownerReason);
		Assert.Contains(unrelatedReason, new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged });
	}

}
