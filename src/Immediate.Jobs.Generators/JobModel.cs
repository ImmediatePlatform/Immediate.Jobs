using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Immediate.Jobs.Generators;

/// <summary>A symbol-free, value-equatable description of one generated job.</summary>
internal sealed record JobModel
{
	public required string HintName { get; init; }
	public required string? Namespace { get; init; }
	public required string Accessibility { get; init; }
	public required string ClassName { get; init; }
	public required string TypeName { get; init; }
	public required string PayloadTypeName { get; init; }
	public required bool HasPayload { get; init; }
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
	public required EquatableReadOnlyList<JobContextModel> Contexts { get; init; }
	public required JsonMetadataRenderModel Json { get; init; }
}

internal sealed record JobContextModel
{
	public required string ExtractorTypeName { get; init; }
	public required string ContextTypeName { get; init; }
	public required string JsonPropertyName { get; init; }
}

internal sealed record QueueModel
{
	public required string Name { get; init; }
	public required int Priority { get; init; }
	public required int Concurrency { get; init; }
}

internal static class GeneratorJobDiscovery
{
	private static readonly SymbolDisplayFormat TypeDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
		.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

	public static bool TryCreateModel(INamedTypeSymbol type, Compilation compilation, out JobModel? model)
	{
		model = null;
		var attribute = JobDiscovery.GetJobAttribute(type);
		if (attribute is null ||
			!JobDiscovery.IsHandler(type) ||
			!JobDiscovery.IsPartial(type) ||
			type.TypeKind != TypeKind.Class ||
			type.IsStatic ||
			type.Arity != 0)
			return false;

		var methods = type.GetMembers("HandleAsync")
			.OfType<IMethodSymbol>()
			.Where(static method => !method.IsImplicitlyDeclared)
			.ToImmutableArray();
		if (methods.Length != 1 ||
			!JobDiscovery.IsValidMethod(methods[0], out var payloadType, out var hasPayload) ||
			!JobDiscovery.ImplementsJobRequest(payloadType!))
			return false;

		var cron = JobDiscovery.GetNamedString(attribute, "Cron");
		if (cron is not null && (hasPayload || !CronValidator.TryValidate(cron, out _)) ||
			!JobDiscovery.IsValidTimeZone(JobDiscovery.GetNamedString(attribute, "TimeZone") ?? "UTC") ||
			JobDiscovery.FindConfigurationProblem(attribute) is not null)
			return false;

		if (hasPayload && PayloadValidation.FindProblem(payloadType!, compilation) is not null)
			return false;

		var contextUses = JobDiscovery.GetJobContextUses(type);
		if (contextUses.Any(use =>
			use.ContextType is null || PayloadValidation.FindProblem(use.ContextType, compilation) is not null))
			return false;

		var resolvedPayload = payloadType!;
		var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		var safe = new string([.. qualified.Select(static character => char.IsLetterOrDigit(character) ? character : '.')]);
		var contexts = contextUses.Select((use, index) => new JobContextModel
		{
			ExtractorTypeName = use.ExtractorType.ToDisplayString(TypeDisplayFormat),
			ContextTypeName = use.ContextType!
				.WithNullableAnnotation(NullableAnnotation.None)
				.ToDisplayString(TypeDisplayFormat),
			JsonPropertyName = $"Context{index}",
		}).ToEquatableReadOnlyList();

		if (!JobDiscovery.TryGetQueue(type, out var queueName, out var queuePriority, out var queueConcurrency))
			return false;

#if NETSTANDARD2_0
		var contextTypes = contextUses.Select(static use => use.ContextType!).ToImmutableArray();
#else
		ImmutableArray<ITypeSymbol> contextTypes = [.. contextUses.Select(static use => use.ContextType!)];
#endif

		model = new()
		{
			HintName = $"IJOB.{safe}.g.cs",
			Namespace = type.ContainingNamespace.IsGlobalNamespace
				? null
				: type.ContainingNamespace.ToDisplayString(),
			Accessibility = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
			ClassName = type.Name,
			TypeName = type.ToDisplayString(TypeDisplayFormat),
			PayloadTypeName = resolvedPayload.ToDisplayString(TypeDisplayFormat),
			HasPayload = hasPayload,
			Name = JobDiscovery.GetName(type),
			QueueName = queueName,
			QueuePriority = queuePriority,
			QueueConcurrency = queueConcurrency,
			Cron = cron,
			TimeZone = JobDiscovery.GetNamedString(attribute, "TimeZone") ?? "UTC",
			MaxAttempts = JobDiscovery.GetNamedInt(attribute, "MaxAttempts", 3),
			Timeout = JobDiscovery.GetNamedString(attribute, "Timeout"),
			MaxConcurrency = JobDiscovery.GetNamedInt(attribute, "MaxConcurrency", 0),
			OverlapPolicy = JobDiscovery.GetNamedInt(attribute, "OverlapPolicy", 0),
			Backoff = JobDiscovery.GetNamedInt(attribute, "Backoff", 2),
			BackoffBase = JobDiscovery.GetNamedString(attribute, "BackoffBase") ?? "00:00:05",
			Contexts = contexts,
			Json = JsonMetadataEmitter.CreateModel(resolvedPayload, contextTypes),
		};
		return true;
	}

	public static QueueModel? CreateQueueModel(INamedTypeSymbol queueType)
	{
		if (JobDiscovery.GetQueueDefinitionAttribute(queueType) is not { } definition)
			return null;

		var name = JobDiscovery.GetQueueName(queueType);
		var concurrency = JobDiscovery.GetNamedInt(definition, "Concurrency", 0);
		return string.IsNullOrWhiteSpace(name) || name == "default" || concurrency < 0
			? null
			: new()
			{
				Name = name,
				Priority = JobDiscovery.GetNamedInt(definition, "Priority", 0),
				Concurrency = concurrency,
			};
	}
}
