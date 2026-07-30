using Microsoft.CodeAnalysis;

namespace Immediate.Jobs.Tests.GeneratorTests;

public sealed class ImmediateJobsGeneratorTests
{
	[Fact]
	public async Task PayloadJobGeneratesTypedSchedulerDirectInvokerAndRegistrations()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;

			namespace Example;

			[Handler, Job(Name = "send-email", MaxAttempts = 5, Timeout = "00:02:00")]
			public sealed partial class SendEmailJob
			{
				public sealed record Payload(Guid UserId, string Template) : IJobRequest { public JobDetails? JobDetails { get; set; } }
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.Example.SendEmailJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.Example.SendEmailJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task PlainRequestGeneratesJobWithoutJobDetailsAssignment()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
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
			"""
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH..PlainRequestJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ..PlainRequestJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task ExplicitJobDetailsOnValueTypeUsesConstrainedByReferenceAssignment()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
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
			"""
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH..StructJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ..StructJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task CronJobGeneratesPayloadlessRecurringScheduler()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job(Cron = "0 */5 * * * *")]
			public sealed partial class CleanupSessionsJob
			{
				private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH..CleanupSessionsJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ..CleanupSessionsJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task PayloadlessJobWithoutCronGeneratesDynamicRecurringScheduler()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class TenantCleanupJob
			{
				private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH..TenantCleanupJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ..TenantCleanupJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task InvokerDelegatesExecutionToImmediateHandlersPipeline()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
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

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task CollectionPayloadsAndTypesWithoutPublicConstructorsGenerateValidMetadata()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Collections.Generic;
			using System.Threading;
			using System.Threading.Tasks;

			public sealed record Item(int Value);

			public sealed class PrivateConstructorValue
			{
				private PrivateConstructorValue() { }
				public string Value { get; } = "hidden";
			}

			[Handler, Job]
			public sealed partial class CollectionJob
			{
				public sealed record Payload(
					Item[] Array,
					List<Item> List,
					IList<Item> IList,
					IReadOnlyList<Item> IReadOnlyList,
					IEnumerable<Item> IEnumerable,
					Dictionary<string, Item> Dictionary,
					IDictionary<string, Item> IDictionary,
					IReadOnlyDictionary<string, Item> IReadOnlyDictionary,
					PrivateConstructorValue PrivateConstructor
				);

				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
					ValueTask.CompletedTask;
			}
			"""
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task ContextExtractorsGenerateOrderedCaptureRestoreMetadataAndScopedRegistrations()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
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

			public sealed class UsageContextExtractor : JobContextExtractor<UsageContext>
			{
				public override string Key => "usage";
				public override UsageContext? Capture() => new(Guid.Empty, Region.Europe);
				public override void Restore(UsageContext context) { }
			}

			public sealed class CorrelationExtractor : JobContextExtractor<CorrelationContext>
			{
				public override string Key => "correlation";
				public override CorrelationContext? Capture() => new("abc");
				public override void Restore(CorrelationContext context) { }
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
			"""
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH..ContextualJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ..ContextualJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task JobWithoutContextDoesNotEmitCaptureOrRestoreCode()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class PlainJob
			{
				private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH..PlainJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ..PlainJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}

	[Fact]
	public async Task NodaTimeContextUsesConfiguredGeneratedMetadata()
	{
		var result = GeneratorTestHelper.RunGenerator(
			"""
			#nullable enable
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using NodaTime;
			using System.Threading;
			using System.Threading.Tasks;
			public sealed record ClockContext(Instant Now);
			public sealed class ClockExtractor : JobContextExtractor<ClockContext>
			{
				public override string Key => "clock";
				public override ClockContext? Capture() => new(Instant.FromUnixTimeTicks(1));
				public override void Restore(ClockContext context) { }
			}
			[Handler, Job, UsesJobContext<ClockExtractor>]
			public sealed partial class ClockJob
			{ private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			includeNodaTime: true
		);

		Assert.Equal(
			[
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH..ClockJob.g.cs",
				"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ..ClockJob.g.cs",
				"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
			],
			result.GeneratedTrees.Select(tree => tree.FilePath.Replace('\\', '/'))
		);

		_ = await Utility.VerifyIgnoreImmediateHandlers(result);
	}
}
