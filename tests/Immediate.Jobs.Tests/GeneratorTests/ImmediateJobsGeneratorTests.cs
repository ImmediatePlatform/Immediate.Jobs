namespace Immediate.Jobs.Tests.GeneratorTests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

public sealed class ImmediateJobsGeneratorTests
{
	[Fact]
	public void StronglyTypedQueueFlowsIntoGeneratedSchedulerAndDefinition()
	{
		const string source = """
			using Immediate.Jobs;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[QueueDefinition(Priority = 10, Concurrency = 1)]
			public sealed class CriticalQueue;

			[Handler, Job, UsesQueue<CriticalQueue>]
			public sealed partial class WorkJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		var generated = string.Join("\n", result.GeneratedTrees.Select(tree => tree.ToString()));

		Assert.Contains("\"critical-queue\"", generated);
		Assert.Contains("Priority = 10", generated);
		Assert.Contains("Concurrency = 1", generated);
		Assert.Contains("services.AddSingleton(new global::Immediate.Jobs.JobQueueDefinition", generated);
	}

	[Fact]
	public async Task PayloadJobGeneratesTypedSchedulerDirectInvokerAndRegistrations()
	{
		const string source = """
			using Immediate.Jobs;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;

			namespace Example;

			[Handler, Job("send-email", MaxAttempts = 5, Timeout = "00:02:00")]
			public sealed partial class SendEmailJob
			{
				public sealed record Payload(Guid UserId, string Template);
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		var generated = string.Join("\n", result.GeneratedTrees.Select(tree => tree.ToString()));

		Assert.Contains("interface IScheduler : global::Immediate.Jobs.IJobScheduler<global::Example.SendEmailJob.Payload>", generated);
		Assert.Contains("sealed class Scheduler(", generated);
		Assert.Contains("sealed class Invoker", generated);
		Assert.Contains("handler.HandleAsync(payload, execution.CancellationToken)", generated);
		Assert.Contains("AddImmediateJobs(", generated);
		Assert.Contains("AddSingleton<global::Immediate.Jobs.JobDefinition>", generated);
		_ = await Verify(result);
	}

	[Fact]
	public async Task CronJobGeneratesPayloadlessRecurringScheduler()
	{
		const string source = """
			using Immediate.Jobs;
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
		var generated = string.Join("\n", result.GeneratedTrees.Select(tree => tree.ToString()));

		Assert.Contains("interface IScheduler : global::Immediate.Jobs.IRecurringJobScheduler", generated);
		Assert.Contains("TriggerNow", generated);
		Assert.Contains("AddOrUpdateRecurring", generated);
		Assert.Contains("RemoveRecurring", generated);
		Assert.Contains("Cron = \"0 */5 * * * *\"", generated);
		_ = await Verify(result);
	}

	[Fact]
	public async Task JobBehaviorsAreResolvedInDeclaredOrder()
	{
		const string source = """
			using Immediate.Jobs;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[assembly: JobBehaviors(typeof(LoggingBehavior<>), typeof(MetricsBehavior<>))]

			public sealed class LoggingBehavior<T> : JobBehavior<T>
			{
				public override ValueTask HandleAsync(JobContext<T> context, JobNext<T> next) => next(context);
			}

			public sealed class MetricsBehavior<T> : JobBehavior<T>
			{
				public override ValueTask HandleAsync(JobContext<T> context, JobNext<T> next) => next(context);
			}

			[Handler, Job]
			public sealed partial class WorkJob
			{
				public sealed record Payload(string Value);
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		var generated = string.Join("\n", result.GeneratedTrees.Select(tree => tree.ToString()));

		Assert.Contains("behavior0.HandleAsync(context, InvokeBehavior1)", generated);
		Assert.Contains("behavior1.HandleAsync(context, InvokeBehavior2)", generated);
		Assert.Contains("GetRequiredService<global::LoggingBehavior<global::WorkJob.Payload>>", generated);
		_ = await Verify(result);
	}

	[Fact]
	public async Task ContextExtractorsGenerateOrderedCaptureRestoreMetadataAndScopedRegistrations()
	{
		const string source = """
			#nullable enable
			using Immediate.Jobs;
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
				public sealed record Payload(string Message);
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var result = GeneratorTestHelper.RunGenerator(source);
		var generated = string.Join("\n", result.GeneratedTrees.Select(tree => tree.ToString()));

		Assert.Contains("CorrelationExtractor contextExtractor0", generated);
		Assert.Contains("UsageContextExtractor contextExtractor1", generated);
		Assert.Contains("CaptureContextAsync", generated);
		Assert.Contains("contextExtractor0.RestoreAsync", generated);
		Assert.Contains("contextExtractor1.RestoreAsync", generated);
		Assert.Contains("JsonTypeInfo<global::CorrelationContext> Context0", generated);
		Assert.Contains("JsonTypeInfo<global::UsageContext> Context1", generated);
		Assert.Equal(1, Count(generated, "TryAddScoped(services, typeof(global::UsageContextExtractor))"));
		Assert.Contains("TryAddScoped(services, typeof(global::ContextualJob.Scheduler))", generated);
		_ = await Verify(result);
	}

	[Fact]
	public void JobWithoutContextDoesNotEmitCaptureOrRestoreCode()
	{
		const string source = """
			using Immediate.Jobs;
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
		var job = result.GeneratedTrees.Single(tree => tree.FilePath.Contains("IJOB.global", StringComparison.Ordinal)).ToString();

		Assert.DoesNotContain("CaptureContextAsync", job);
		Assert.DoesNotContain("JobContextEnvelope", job);
	}

	[Fact]
	public async Task NodaTimeContextUsesConfiguredGeneratedMetadata()
	{
		const string source = """
			#nullable enable
			using Immediate.Jobs;
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
		var generated = string.Join("\n", result.GeneratedTrees.Select(tree => tree.ToString()));

		Assert.Contains("JsonTypeInfo<global::ClockContext> Context0", generated);
		Assert.Contains("options.GetConverter(typeof(global::NodaTime.Instant))", generated);
		_ = await Verify(result);
	}

	[Fact]
	public void EditingContextShapeInvalidatesOnlyOwningJobModel()
	{
		const string contextBefore = """
			#nullable enable
			using Immediate.Jobs;
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
		const string contextAfter = """
			#nullable enable
			using Immediate.Jobs;
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
		const string owner = """
			using Immediate.Jobs; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job, UsesJobContext<AmbientExtractor>] public sealed partial class ContextOwnerJob
			{ private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""";
		const string unrelated = """
			using Immediate.Jobs; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
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
			path: "Context.cs",
			cancellationToken: TestContext.Current.CancellationToken
		);
		compilation = compilation.ReplaceSyntaxTree(contextTree, replacement);

		driver = GeneratorTestHelper.RunAndAssert(driver, compilation);
		var jobsResult = driver.GetRunResult().Results.Single(result => result.TrackedSteps.ContainsKey("Jobs"));
		var outputs = jobsResult.TrackedSteps["Jobs"].SelectMany(step => step.Outputs).ToArray();
		var ownerOutput = Assert.Single(outputs, output => output.Value?.ToString()?.Contains("ClassName = ContextOwnerJob", StringComparison.Ordinal) == true);
		var unrelatedOutput = Assert.Single(outputs, output => output.Value?.ToString()?.Contains("ClassName = UnrelatedJob", StringComparison.Ordinal) == true);

		Assert.Equal(IncrementalStepRunReason.Modified, ownerOutput.Reason);
		Assert.Contains(unrelatedOutput.Reason, new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged });
	}

	[Fact]
	public void EditingExtractorContractInvalidatesOnlyReferencingJobModel()
	{
		const string contexts = """
			public sealed record FirstAmbientContext(string Value);
			public sealed record SecondAmbientContext(string Value);
			""";
		const string extractorBefore = """
			#nullable enable
			using Immediate.Jobs; using System.Threading; using System.Threading.Tasks;
			public sealed class ChangingExtractor : IJobContextExtractor<FirstAmbientContext>
			{
				public string Key => "ambient";
				public ValueTask<FirstAmbientContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<FirstAmbientContext?>(new("one"));
				public ValueTask RestoreAsync(FirstAmbientContext context, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""";
		const string extractorAfter = """
			#nullable enable
			using Immediate.Jobs; using System.Threading; using System.Threading.Tasks;
			public sealed class ChangingExtractor : IJobContextExtractor<SecondAmbientContext>
			{
				public string Key => "ambient";
				public ValueTask<SecondAmbientContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<SecondAmbientContext?>(new("two"));
				public ValueTask RestoreAsync(SecondAmbientContext context, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""";
		const string owner = """
			using Immediate.Jobs; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job, UsesJobContext<ChangingExtractor>] public sealed partial class ExtractorOwnerJob
			{ private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""";
		const string unrelated = """
			using Immediate.Jobs; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
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
			path: "Extractor.cs",
			cancellationToken: TestContext.Current.CancellationToken
		);
		compilation = compilation.ReplaceSyntaxTree(extractorTree, replacement);

		driver = GeneratorTestHelper.RunAndAssert(driver, compilation);
		var jobsResult = driver.GetRunResult().Results.Single(result => result.TrackedSteps.ContainsKey("Jobs"));
		var outputs = jobsResult.TrackedSteps["Jobs"].SelectMany(step => step.Outputs).ToArray();
		var ownerOutput = Assert.Single(outputs, output => output.Value?.ToString()?.Contains("ClassName = ExtractorOwnerJob", StringComparison.Ordinal) == true);
		var unrelatedOutput = Assert.Single(outputs, output => output.Value?.ToString()?.Contains("ClassName = OtherJob", StringComparison.Ordinal) == true);

		Assert.Equal(IncrementalStepRunReason.Modified, ownerOutput.Reason);
		Assert.Contains(unrelatedOutput.Reason, new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged });
	}

	private static int Count(string value, string search)
	{
		var count = 0;
		var offset = 0;
		while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
		{
			count++;
			offset += search.Length;
		}
		return count;
	}
}
