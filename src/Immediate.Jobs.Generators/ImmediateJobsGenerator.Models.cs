using Microsoft.CodeAnalysis.CSharp;

namespace Immediate.Jobs.Generators;

public sealed partial class ImmediateJobsGenerator
{
	private sealed record AssemblyDefaults
	{
		public required string AssemblyName { get; init; }
		public required LanguageVersion LanguageVersion { get; init; }
	}

	private sealed record JobModel
	{
		public required string? Namespace { get; init; }
		public required string Accessibility { get; init; }
		public required string ClassName { get; init; }
		public required string TypeName { get; init; }
		public required string PayloadTypeName { get; init; }
		public required bool HasPayload { get; init; }
		public required bool HasJobDetails { get; init; }
		public required string Name { get; init; }
		public required string QueueName { get; init; }
		public required int QueuePriority { get; init; }
		public required int QueueConcurrency { get; init; }
		public required string? Cron { get; init; }
		public required string TimeZone { get; init; }
		public required int MaxAttempts { get; init; }
		public required string? Timeout { get; init; }
		public required int MaxConcurrency { get; init; }
		public required int OverlapPolicy { get; init; }
		public required int Backoff { get; init; }
		public required string BackoffBase { get; init; }
		public required string? Tags { get; init; }
		public required EquatableReadOnlyList<JobContextModel> Contexts { get; init; }
		public required JsonMetadataRenderModel Json { get; init; }
	}

	private sealed record JobContextModel
	{
		public required string ExtractorTypeName { get; init; }
		public required string ContextTypeName { get; init; }
		public required string JsonPropertyName { get; init; }
	}

	private sealed record QueueModel
	{
		public required string Name { get; init; }
		public required int Priority { get; init; }
		public required int Concurrency { get; init; }
	}
}
