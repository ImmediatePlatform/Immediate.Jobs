using Microsoft.CodeAnalysis;

namespace Immediate.Jobs.Analyzers;

/// <summary>Diagnostic descriptors produced for invalid job declarations.</summary>
internal static class DiagnosticDescriptors
{
	private const string Category = "Immediate.Jobs";

	public static readonly DiagnosticDescriptor InvalidCron = Create(
		"IJOB001",
		"Invalid recurring schedule",
		"Schedule value '{0}' is invalid: {1}"
	);

	public static readonly DiagnosticDescriptor DuplicateJobName = Create(
		"IJOB002",
		"Duplicate job name",
		"Job name '{0}' is also used by '{1}'"
	);

	public static readonly DiagnosticDescriptor UnsupportedPayload = Create(
		"IJOB003",
		"Unsupported job payload",
		"Payload type '{0}' cannot be source-generated for JSON serialization: {1}"
	);

	public static readonly DiagnosticDescriptor InvalidMethodSignature = Create(
		"IJOB004",
		"Invalid job method signature",
		"Job '{0}' must declare exactly one private instance HandleAsync method returning ValueTask, with a request followed by CancellationToken"
	);

	public static readonly DiagnosticDescriptor InvalidConfiguration = Create(
		"IJOB008",
		"Invalid job configuration",
		"Job '{0}' has invalid configuration: {1}"
	);

	public static readonly DiagnosticDescriptor JobMustBePartial = Create(
		"IJOB005",
		"Job class must be partial",
		"Job class '{0}' must be declared partial"
	);

	public static readonly DiagnosticDescriptor CronPayload = Create(
		"IJOB006",
		"Cron job cannot have a payload",
		"Cron job '{0}' cannot declare a payload parameter"
	);

	public static readonly DiagnosticDescriptor NodaTimePackageRequired = Create(
		"IJOB007",
		"NodaTime integration package is required",
		"Type '{0}' contains NodaTime values; reference Immediate.Jobs.NodaTime to enable generated serialization"
	);

	public static readonly DiagnosticDescriptor JobMustBeHandler = Create(
		"IJOB009",
		"Job class must be an Immediate.Handler",
		"Job class '{0}' must also be marked with Immediate.Handlers.Shared.HandlerAttribute"
	);

	public static readonly DiagnosticDescriptor InvalidQueueConfiguration = Create(
		"IJOB010",
		"Invalid queue configuration",
		"Queue '{0}' has invalid configuration: {1}"
	);

	public static readonly DiagnosticDescriptor InvalidQueueTarget = Create(
		"IJOB011",
		"Invalid queue target",
		"Queue type '{0}' must be marked with QueueDefinitionAttribute"
	);

	public static readonly DiagnosticDescriptor DuplicateQueueName = Create(
		"IJOB012",
		"Duplicate queue name",
		"Queue name '{0}' is also used by '{1}'"
	);

	public static readonly DiagnosticDescriptor InvalidContextExtractor = Create(
		"IJOB013",
		"Invalid job context extractor",
		"Context extractor type '{0}' must implement exactly one IJobContextExtractor<TContext> interface"
	);

	public static readonly DiagnosticDescriptor UnsupportedContext = Create(
		"IJOB014",
		"Unsupported job context",
		"Context type '{0}' cannot be source-generated for JSON serialization: {1}"
	);

	public static readonly DiagnosticDescriptor DetachedMidJobBatchAddition = Create(
		"IJOB020",
		"Detached work cannot be added to a batch",
		"AddToBatchAsync(JobDetails, ...) cannot use ContinuationOptions.Detached; use ScheduleAfter for detached work"
	);

	private static DiagnosticDescriptor Create(string id, string title, string message) =>
		new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
