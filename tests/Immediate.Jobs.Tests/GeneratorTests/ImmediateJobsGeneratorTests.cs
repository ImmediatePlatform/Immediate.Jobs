using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Immediate.Jobs.Tests.GeneratorTests;

public sealed class ImmediateJobsGeneratorTests
{

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
			"IJ.Example.SendEmailJob.g.cs"
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
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..PlainRequestJob.g.cs", "IJ..PlainRequestJob.g.cs");
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
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..StructJob.g.cs", "IJ..StructJob.g.cs");
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
			"IJ..CleanupSessionsJob.g.cs"
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
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..TenantCleanupJob.g.cs", "IJ..TenantCleanupJob.g.cs");
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
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..WorkJob.g.cs", "IJ..WorkJob.g.cs");
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
				public UsageContext? Capture() => new(Guid.Empty, Region.Europe);
				public void Restore(UsageContext context) { }
			}

			public sealed class CorrelationExtractor : IJobContextExtractor<CorrelationContext>
			{
				public string Key => "correlation";
				public CorrelationContext? Capture() => new("abc");
				public void Restore(CorrelationContext context) { }
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
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..ContextualJob.g.cs", "IJ..ContextualJob.g.cs");
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
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..PlainJob.g.cs", "IJ..PlainJob.g.cs");
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
				public ClockContext? Capture() => new(Instant.FromUnixTimeTicks(1));
				public void Restore(ClockContext context) { }
			}
			[Handler, Job, UsesJobContext<ClockExtractor>]
			public sealed partial class ClockJob
			{ private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""";

		var result = GeneratorTestHelper.RunGenerator(source, includeNodaTime: true);
		GeneratorTestHelper.AssertGeneratedTrees(result, "IH..ClockJob.g.cs", "IJ..ClockJob.g.cs");
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
				public AmbientContext? Capture() => new("one");
				public void Restore(AmbientContext context) { }
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
				public AmbientContext? Capture() => new("one", 2);
				public void Restore(AmbientContext context) { }
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
				public FirstAmbientContext? Capture() => new("one");
				public void Restore(FirstAmbientContext context) { }
			}
			""";
		var extractorAfter = """
			#nullable enable
			using Immediate.Jobs.Shared; using System.Threading; using System.Threading.Tasks;
			public sealed class ChangingExtractor : IJobContextExtractor<SecondAmbientContext>
			{
				public string Key => "ambient";
				public SecondAmbientContext? Capture() => new("two");
				public void Restore(SecondAmbientContext context) { }
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
