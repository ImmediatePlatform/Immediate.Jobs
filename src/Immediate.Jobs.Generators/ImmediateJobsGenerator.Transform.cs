using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Immediate.Jobs.Generators;

public sealed partial class ImmediateJobsGenerator
{
	private static readonly SymbolDisplayFormat TypeDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
		.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

	private static JobModel? TransformJob(
		GeneratorAttributeSyntaxContext context,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return context.TargetSymbol is INamedTypeSymbol type
			&& TryCreateJobModel(type, context.SemanticModel.Compilation, out var job)
				? job
				: null;
	}

	private static QueueModel? TransformQueue(
		GeneratorAttributeSyntaxContext context,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return context.TargetSymbol is INamedTypeSymbol type ? CreateQueueModel(type) : null;
	}

	private static bool TryCreateJobModel(INamedTypeSymbol type, Compilation compilation, out JobModel? model)
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
			!JobDiscovery.IsValidMethod(methods[0], out var payloadType, out var hasPayload))
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
			HasJobDetails = JobDiscovery.ImplementsJobRequest(resolvedPayload),
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
			Tags = GetHandlerTags(type),
			Contexts = contexts,
			Json = JsonMetadataEmitter.CreateModel(resolvedPayload, contextTypes),
		};
		return true;
	}

	private static string? GetHandlerTags(INamedTypeSymbol type)
	{
		var attribute = type.GetAttributes().FirstOrDefault(static candidate =>
			candidate.AttributeClass.IsHandlerAttribute);
		var tags = attribute?.NamedArguments.FirstOrDefault(static pair => pair.Key == "Tags").Value;
		if (tags is not { Kind: TypedConstantKind.Array })
			return null;

		return string.Join(
			", ",
			tags.Value.Values
				.Select(static value => value.ToCSharpString())
				.OrderBy(static value => value, StringComparer.Ordinal)
		);
	}

	private static QueueModel? CreateQueueModel(INamedTypeSymbol queueType)
	{
		if (JobDiscovery.GetQueueDefinitionAttribute(queueType) is not { } definition)
			return null;

		var name = JobDiscovery.GetQueueName(queueType);
		var concurrency = JobDiscovery.GetNamedInt(definition, "Concurrency", 0);
		if (string.IsNullOrWhiteSpace(name) || name == "default" || concurrency < 0)
			return null;

		return new()
		{
			Name = name,
			Priority = JobDiscovery.GetNamedInt(definition, "Priority", 0),
			Concurrency = concurrency,
		};
	}
}
