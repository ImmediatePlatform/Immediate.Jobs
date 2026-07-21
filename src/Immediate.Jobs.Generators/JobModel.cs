using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Immediate.Jobs.Generators;

/// <summary>
///     A fully-resolved, symbol-free description of a single job. All values are primitives or
///     value-equatable collections so the model can flow through the incremental pipeline and be
///     cached without pinning a <see cref="Compilation"/> in memory.
/// </summary>
internal sealed record JobModel
{
	public required string HintName { get; init; }
	public required string? Namespace { get; init; }
	public required string Accessibility { get; init; }
	public required string ClassName { get; init; }
	public required string TypeName { get; init; }
	public required string PayloadTypeName { get; init; }

	/// <summary>The payload type name without nullable annotations, used to match assembly-level behaviors.</summary>
	public required string PayloadMatchName { get; init; }
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

	/// <summary>
	///     The resolved behavior type names. When <see cref="HasExplicitBehaviors"/> is <see langword="true"/> these
	///     come from the job's own <c>Behaviors</c> argument; otherwise the list is populated from the assembly-level
	///     <c>JobBehaviors</c> provider once it is combined into the pipeline.
	/// </summary>
	public required EquatableReadOnlyList<string> Behaviors { get; init; }

	/// <summary>Whether the job declared its own <c>Behaviors</c>, opting out of the assembly-level default set.</summary>
	public required bool HasExplicitBehaviors { get; init; }
	public required EquatableReadOnlyList<JobContextModel> Contexts { get; init; }
	public required JsonMetadataRenderModel Json { get; init; }
}

internal sealed record JobContextModel
{
	public required string ExtractorTypeName { get; init; }
	public required string ContextTypeName { get; init; }
	public required string JsonPropertyName { get; init; }
}

internal readonly record struct JobContextUse(
	INamedTypeSymbol ExtractorType,
	ITypeSymbol? ContextType,
	AttributeData AppliedAttribute
);

internal sealed record QueueModel
{
	public required string Name { get; init; }
	public required int Priority { get; init; }
	public required int Concurrency { get; init; }
}

/// <summary>
///     A symbol-free description of a single behavior discovered on an assembly-level <c>JobBehaviors</c> attribute
///     (or a job's own <c>Behaviors</c> argument). It carries just enough to render a concrete registration for a given
///     payload without holding any Roslyn symbol in the incremental pipeline.
/// </summary>
internal sealed record JobBehaviorModel
{
	/// <summary>For an open generic behavior, the fully-qualified name without type arguments; otherwise the full type name.</summary>
	public required string RenderName { get; init; }

	/// <summary>Whether the payload type argument must be appended to <see cref="RenderName"/> when rendering.</summary>
	public required bool IsGeneric { get; init; }

	/// <summary>The payload the behavior is bound to (nullable-stripped), or <see langword="null"/> when it applies to any payload.</summary>
	public required string? RequiredPayloadName { get; init; }
}

internal static class JobDiscovery
{
	private const string JobAttributeName = "Immediate.Jobs.JobAttribute";
	private const string HandlerAttributeName = "Immediate.Handlers.Shared.HandlerAttribute";
	private const string CancellationTokenName = "System.Threading.CancellationToken";
	private const string ValueTaskName = "System.Threading.Tasks.ValueTask";

	private static readonly SymbolDisplayFormat TypeDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
		.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
	private static readonly SymbolDisplayFormat NoTypeArgsFormat = SymbolDisplayFormat.FullyQualifiedFormat
		.WithGenericsOptions(SymbolDisplayGenericsOptions.None);
	private const string JobBehaviorsAttributeName = "Immediate.Jobs.JobBehaviorsAttribute";
	private const string QueueDefinitionAttributeName = "Immediate.Jobs.QueueDefinitionAttribute";
	private const string JobContextExtractorName = "Immediate.Jobs.IJobContextExtractor<TContext>";

	public static ImmutableArray<INamedTypeSymbol> FindJobs(Compilation compilation, CancellationToken cancellationToken)
	{
		var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
		foreach (var tree in compilation.SyntaxTrees)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var model = compilation.GetSemanticModel(tree);
			foreach (var declaration in tree.GetRoot(cancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>())
			{
				if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol)
					continue;

				if (GetJobAttribute(symbol) is null || builder.Any(existing => SymbolEqualityComparer.Default.Equals(existing, symbol)))
					continue;

				builder.Add(symbol);
			}
		}

		return builder.ToImmutable();
	}

	public static ImmutableArray<INamedTypeSymbol> FindQueueDefinitions(
		Compilation compilation,
		CancellationToken cancellationToken
	)
	{
		var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
		foreach (var tree in compilation.SyntaxTrees)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var semanticModel = compilation.GetSemanticModel(tree);
			foreach (var declaration in tree.GetRoot(cancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>())
			{
				if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol symbol &&
					GetQueueDefinitionAttribute(symbol) is not null &&
					!builder.Any(existing => SymbolEqualityComparer.Default.Equals(existing, symbol)))
				{
					builder.Add(symbol);
				}
			}
		}

		return builder.ToImmutable();
	}

	public static AttributeData? GetJobAttribute(INamedTypeSymbol symbol) =>
		symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == JobAttributeName);

	public static AttributeData? GetQueueDefinitionAttribute(INamedTypeSymbol symbol) =>
		symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == QueueDefinitionAttributeName);

	public static AttributeData? GetUsesQueueAttribute(INamedTypeSymbol symbol) =>
		symbol.GetAttributes().FirstOrDefault(a =>
			a.AttributeClass is { Name: "UsesQueueAttribute", Arity: 1 } attributeClass &&
			attributeClass.ContainingNamespace.ToDisplayString() == "Immediate.Jobs");

	public static bool IsHandler(INamedTypeSymbol symbol) =>
		symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == HandlerAttributeName);

	public static bool IsPartial(INamedTypeSymbol symbol) => symbol.DeclaringSyntaxReferences
		.Select(reference => reference.GetSyntax())
		.OfType<TypeDeclarationSyntax>()
		.Any(declaration => declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

	public static ImmutableArray<JobContextUse> GetJobContextUses(INamedTypeSymbol job)
	{
		var attributes = job.GetAttributes();
		var builder = ImmutableArray.CreateBuilder<JobContextUse>();
		var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

		foreach (var attribute in attributes.Where(IsUsesJobContextAttribute))
			Add(attribute, attribute);

		foreach (var marker in attributes.Where(attribute => !IsUsesJobContextAttribute(attribute)))
		{
			if (marker.AttributeClass is null)
				continue;
			foreach (var attribute in marker.AttributeClass.GetAttributes().Where(IsUsesJobContextAttribute))
				Add(attribute, marker);
		}

		return builder.ToImmutable();

		void Add(AttributeData contextAttribute, AttributeData appliedAttribute)
		{
			if (contextAttribute.AttributeClass?.TypeArguments is not [INamedTypeSymbol extractor] || !seen.Add(extractor))
				return;
			var interfaces = extractor.AllInterfaces
				.Where(candidate => candidate.OriginalDefinition.ToDisplayString() == JobContextExtractorName)
				.ToArray();
			builder.Add(new(extractor, interfaces.Length == 1 ? interfaces[0].TypeArguments[0] : null, appliedAttribute));
		}
	}

	private static bool IsUsesJobContextAttribute(AttributeData attribute) =>
		attribute.AttributeClass is { Name: "UsesJobContextAttribute", Arity: 1 } attributeClass &&
		attributeClass.ContainingNamespace.ToDisplayString() == "Immediate.Jobs";

	public static bool TryCreateModel(INamedTypeSymbol type, Compilation compilation, out JobModel? model)
	{
		model = null;
		var attribute = GetJobAttribute(type);
		if (attribute is null || !IsHandler(type) || !IsPartial(type) || type.TypeKind != TypeKind.Class || type.IsStatic || type.Arity != 0)
			return false;

		var methods = type.GetMembers("HandleAsync").OfType<IMethodSymbol>()
			.Where(method => !method.IsImplicitlyDeclared)
			.ToImmutableArray();
		if (methods.Length != 1 || !IsValidMethod(methods[0], out var payloadType, out var hasPayload, out _))
			return false;

		var cron = GetNamedString(attribute, "Cron");
		if (cron is not null && (hasPayload || !CronValidator.TryValidate(cron, out _)) ||
			!IsValidTimeZone(GetNamedString(attribute, "TimeZone") ?? "UTC") ||
			FindConfigurationProblem(attribute) is not null)
			return false;

		if (hasPayload && PayloadValidation.FindProblem(payloadType!, compilation) is not null)
			return false;

		var contextUses = GetJobContextUses(type);
		if (contextUses.Any(use => use.ContextType is null || PayloadValidation.FindProblem(use.ContextType, compilation) is not null))
			return false;

		var resolvedPayload = payloadType ?? compilation.GetTypeByMetadataName("Immediate.Jobs.NoPayload")!;
		var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		var safe = new string(qualified.Select(character => char.IsLetterOrDigit(character) ? character : '.').ToArray());

		var payloadTypeName = resolvedPayload.ToDisplayString(TypeDisplayFormat);
		var payloadMatchName = resolvedPayload.WithNullableAnnotation(NullableAnnotation.None)
			.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		var contexts = contextUses.Select((use, index) => new JobContextModel
		{
			ExtractorTypeName = use.ExtractorType.ToDisplayString(TypeDisplayFormat),
			ContextTypeName = use.ContextType!.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(TypeDisplayFormat),
			JsonPropertyName = $"Context{index}",
		}).ToEquatableReadOnlyList();

		// Behaviors declared on the job itself are resolved here (node-local). Jobs that don't declare their own
		// inherit the assembly-level default set, which is combined into the pipeline as a separate provider.
		var behaviorsArgument = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == "Behaviors");
		var hasExplicitBehaviors = behaviorsArgument.Key is not null && !behaviorsArgument.Value.IsNull;
		var behaviors = hasExplicitBehaviors
			? behaviorsArgument.Value.Values
				.Select(ParseBehavior)
				.Where(behavior => behavior is not null)
				.Select(behavior => ResolveBehavior(behavior!, payloadTypeName, payloadMatchName))
				.Where(name => name is not null)
				.Select(name => name!)
				.ToEquatableReadOnlyList()
			: default;

		if (!TryGetQueue(type, out var queueName, out var queuePriority, out var queueConcurrency))
			return false;

		model = new JobModel
		{
			HintName = $"IJOB.{safe}.g.cs",
			Namespace = type.ContainingNamespace.IsGlobalNamespace
				? null
				: type.ContainingNamespace.ToDisplayString(),
			Accessibility = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
			ClassName = type.Name,
			TypeName = type.ToDisplayString(TypeDisplayFormat),
			PayloadTypeName = payloadTypeName,
			PayloadMatchName = payloadMatchName,
			HasPayload = hasPayload,
			Name = GetName(type),
			QueueName = queueName,
			QueuePriority = queuePriority,
			QueueConcurrency = queueConcurrency,
			Cron = cron,
			TimeZone = GetNamedString(attribute, "TimeZone") ?? "UTC",
			MaxAttempts = GetNamedInt(attribute, "MaxAttempts", 3),
			Timeout = GetNamedString(attribute, "Timeout"),
			MaxConcurrency = GetNamedInt(attribute, "MaxConcurrency", 0),
			OverlapPolicy = GetNamedInt(attribute, "OverlapPolicy", 0),
			Backoff = GetNamedInt(attribute, "Backoff", 2),
			BackoffBase = GetNamedString(attribute, "BackoffBase") ?? "00:00:05",
			Behaviors = behaviors,
			HasExplicitBehaviors = hasExplicitBehaviors,
			Contexts = contexts,
			Json = JsonMetadataEmitter.CreateModel(resolvedPayload, contextUses.Select(use => use.ContextType!).ToImmutableArray()),
		};
		return true;
	}

	public static bool IsValidMethod(
		IMethodSymbol method,
		out ITypeSymbol? payloadType,
		out bool hasPayload,
		out bool returnsValueTask
	)
	{
		payloadType = null;
		hasPayload = false;
		returnsValueTask = false;
		if (method.IsStatic || method.IsGenericMethod || method.MethodKind != MethodKind.Ordinary ||
			method.DeclaredAccessibility != Accessibility.Private)
			return false;

		var returnName = method.ReturnType.ToDisplayString();
		if (returnName != ValueTaskName)
			return false;
		returnsValueTask = true;

		if (method.Parameters.Length != 2 ||
			method.Parameters[1].Type.ToDisplayString() != CancellationTokenName ||
			method.Parameters.Any(parameter => parameter.RefKind != RefKind.None))
			return false;

		payloadType = method.Parameters[0].Type;
		hasPayload = payloadType.ToDisplayString() != "Immediate.Jobs.NoPayload";
		return true;
	}

	public static string GetName(INamedTypeSymbol type)
	{
		var attribute = GetJobAttribute(type)!;
		var explicitName = attribute.ConstructorArguments.Length == 1
			? attribute.ConstructorArguments[0].Value as string
			: null;
		return string.IsNullOrWhiteSpace(explicitName) ? ToKebabCase(RemoveJobSuffix(type.Name)) : explicitName!;
	}

	public static bool TryGetQueue(
		INamedTypeSymbol job,
		out string name,
		out int priority,
		out int concurrency
	)
	{
		var usesQueue = GetUsesQueueAttribute(job);
		if (usesQueue?.AttributeClass?.TypeArguments is not [INamedTypeSymbol queueType] ||
			GetQueueDefinitionAttribute(queueType) is not { } definition)
		{
			name = "default";
			priority = 0;
			concurrency = 0;
			return usesQueue is null;
		}

		name = GetNamedString(definition, "Name") ?? ToKebabCase(queueType.Name);
		priority = GetNamedInt(definition, "Priority", 0);
		concurrency = GetNamedInt(definition, "Concurrency", 0);
		return !string.IsNullOrWhiteSpace(name) && name != "default" && concurrency >= 0;
	}

	public static string GetQueueName(INamedTypeSymbol queueType)
	{
		var definition = GetQueueDefinitionAttribute(queueType)!;
		return GetNamedString(definition, "Name") ?? ToKebabCase(queueType.Name);
	}

	public static QueueModel? CreateQueueModel(INamedTypeSymbol queueType)
	{
		if (GetQueueDefinitionAttribute(queueType) is not { } definition)
			return null;

		var name = GetQueueName(queueType);
		var concurrency = GetNamedInt(definition, "Concurrency", 0);
		return string.IsNullOrWhiteSpace(name) || name == "default" || concurrency < 0
			? null
			: new()
			{
				Name = name,
				Priority = GetNamedInt(definition, "Priority", 0),
				Concurrency = concurrency,
			};
	}

	public static string? GetNamedString(AttributeData attribute, string name) =>
		attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as string;

	// Validity is intentionally limited to a non-empty check. Resolving the identifier against the
	// host's time-zone database (TimeZoneInfo.FindSystemTimeZoneById) would make generator/analyzer
	// output depend on the build machine's OS and installed tz data — the same source could compile
	// on one host and fail on another. The identifier is resolved at run time, where both IANA and
	// Windows ids are supported, so we only reject values that can never be valid here.
	public static bool IsValidTimeZone(string timeZone) => !string.IsNullOrWhiteSpace(timeZone);

	public static string? FindConfigurationProblem(AttributeData attribute)
	{
		if (GetNamedInt(attribute, "MaxAttempts", 3) < 1)
			return "MaxAttempts must be at least one";
		if (GetNamedInt(attribute, "MaxConcurrency", 0) < 0)
			return "MaxConcurrency cannot be negative";
		if (GetNamedInt(attribute, "OverlapPolicy", 0) is < 0 or > 2)
			return "OverlapPolicy is not a defined value";
		if (GetNamedInt(attribute, "Backoff", 2) is < 0 or > 2)
			return "Backoff is not a defined value";

		var timeout = GetNamedString(attribute, "Timeout");
		if (timeout is not null && (!TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var timeoutValue) || timeoutValue <= TimeSpan.Zero))
			return "Timeout must be a positive TimeSpan";
		var backoffBase = GetNamedString(attribute, "BackoffBase") ?? "00:00:05";
		if (!TimeSpan.TryParse(backoffBase, CultureInfo.InvariantCulture, out var backoffValue) || backoffValue <= TimeSpan.Zero)
			return "BackoffBase must be a positive TimeSpan";
		return null;
	}

	public static int GetNamedInt(AttributeData attribute, string name, int fallback)
	{
		var pair = attribute.NamedArguments.FirstOrDefault(candidate => candidate.Key == name);
		return pair.Key is null || pair.Value.Value is not int value ? fallback : value;
	}

	/// <summary>
	///     Transforms every type listed on the assembly-level <c>JobBehaviors</c> attributes attached to a compilation
	///     unit into symbol-free <see cref="JobBehaviorModel"/> values, preserving declaration order.
	/// </summary>
	public static ImmutableArray<JobBehaviorModel> ParseAssemblyBehaviors(
		GeneratorAttributeSyntaxContext context,
		CancellationToken cancellationToken
	)
	{
		var builder = ImmutableArray.CreateBuilder<JobBehaviorModel>();
		foreach (var attribute in context.Attributes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (attribute.AttributeClass?.ToDisplayString() != JobBehaviorsAttributeName)
				continue;

			foreach (var argument in attribute.ConstructorArguments)
			{
				var values = argument.Kind == TypedConstantKind.Array ? argument.Values : ImmutableArray.Create(argument);
				foreach (var value in values)
				{
					if (ParseBehavior(value) is { } behavior)
						builder.Add(behavior);
				}
			}
		}
		return builder.ToImmutable();
	}

	/// <summary>
	///     Inspects a <c>typeof(...)</c> constant and, when it derives from <c>JobBehavior&lt;TPayload&gt;</c>, captures
	///     how to render it for a concrete payload. Returns <see langword="null"/> for anything that is not a behavior.
	/// </summary>
	public static JobBehaviorModel? ParseBehavior(TypedConstant constant)
	{
		if (constant.Value is not INamedTypeSymbol behavior)
			return null;

		var isGeneric = behavior.IsUnboundGenericType && behavior.Arity == 1;

		string? requiredPayloadName = null;
		var isJobBehavior = false;
		for (var current = behavior.OriginalDefinition; current is not null; current = current.BaseType)
		{
			if (current.OriginalDefinition.ToDisplayString() != "Immediate.Jobs.JobBehavior<TPayload>" ||
				current.TypeArguments.Length != 1)
				continue;

			isJobBehavior = true;
			var boundPayload = current.TypeArguments[0];
			// A behavior bound to its own type parameter (JobBehavior<T>) applies to any payload; one bound to a
			// concrete type only applies when the job's payload matches that type.
			if (boundPayload is not ITypeParameterSymbol parameter ||
				!SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, behavior.OriginalDefinition))
			{
				requiredPayloadName = boundPayload.WithNullableAnnotation(NullableAnnotation.None)
					.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			}
			break;
		}

		if (!isJobBehavior)
			return null;

		return new JobBehaviorModel
		{
			RenderName = isGeneric
				? behavior.ToDisplayString(NoTypeArgsFormat)
				: behavior.ToDisplayString(TypeDisplayFormat),
			IsGeneric = isGeneric,
			RequiredPayloadName = requiredPayloadName,
		};
	}

	/// <summary>
	///     Renders a behavior for a specific payload, or returns <see langword="null"/> when the behavior is bound to a
	///     different payload than the job's.
	/// </summary>
	public static string? ResolveBehavior(JobBehaviorModel behavior, string payloadTypeName, string payloadMatchName)
	{
		if (behavior.RequiredPayloadName is not null &&
			!string.Equals(behavior.RequiredPayloadName, payloadMatchName, StringComparison.Ordinal))
			return null;

		return behavior.IsGeneric ? $"{behavior.RenderName}<{payloadTypeName}>" : behavior.RenderName;
	}

	private static string RemoveJobSuffix(string name) =>
		name.EndsWith("Job", StringComparison.Ordinal) && name.Length > 3 ? name.Substring(0, name.Length - 3) : name;

	private static string ToKebabCase(string value)
	{
		var result = new List<char>(value.Length + 8);
		for (var index = 0; index < value.Length; index++)
		{
			var current = value[index];
			if (index > 0 && char.IsUpper(current) &&
				(char.IsLower(value[index - 1]) || (index + 1 < value.Length && char.IsLower(value[index + 1]))))
				result.Add('-');
			result.Add(char.ToLower(current, CultureInfo.InvariantCulture));
		}
		return new string(result.ToArray());
	}
}

internal static class PayloadValidation
{
	public static string? FindProblem(ITypeSymbol type, Compilation compilation)
	{
		var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
		return Visit(type, compilation, visited);
	}

	public static bool ContainsNodaTime(ITypeSymbol type)
	{
		var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
		return VisitForNodaTime(type, visited);
	}

	private static string? Visit(ITypeSymbol type, Compilation compilation, HashSet<ITypeSymbol> visited)
	{
		if (!visited.Add(type))
			return null;
		if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter || type.IsRefLikeType)
			return "the type is pointer-like, ref-like, or open generic";
		if (type.TypeKind == TypeKind.Interface || type is INamedTypeSymbol { IsAbstract: true })
			return "interfaces and abstract types do not have a statically known JSON shape";
		if (type.TypeKind == TypeKind.Delegate || type.SpecialType == SpecialType.System_Delegate || type.ToDisplayString() == "System.Type")
			return "delegates and System.Type are not supported payload values";
		if (type is IArrayTypeSymbol array)
			return Visit(array.ElementType, compilation, visited);
		if (type is not INamedTypeSymbol named)
			return null;

		foreach (var argument in named.TypeArguments)
		{
			var problem = Visit(argument, compilation, visited);
			if (problem is not null)
				return problem;
		}

		if (named.SpecialType != SpecialType.None || named.TypeKind == TypeKind.Enum || IsKnownSystemValue(named) ||
			named.ContainingNamespace.ToDisplayString().StartsWith("NodaTime", StringComparison.Ordinal))
			return null;
		if (named.ContainingNamespace.ToDisplayString().StartsWith("System", StringComparison.Ordinal))
			return "this System type does not have a generated metadata contract";
		if (named.TypeKind == TypeKind.Class && !named.InstanceConstructors.Any(constructor =>
			constructor.DeclaredAccessibility == Accessibility.Public &&
			constructor.Parameters.All(parameter => named.GetMembers().Any(member =>
				member.DeclaredAccessibility == Accessibility.Public && string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))))
			return "the type has no public constructor that can be bound to its public members";

		foreach (var member in named.GetMembers())
		{
			ITypeSymbol? memberType = member switch
			{
				IPropertySymbol property when property.DeclaredAccessibility == Accessibility.Public && !property.IsStatic && property.GetMethod is not null => property.Type,
				IFieldSymbol field when field.DeclaredAccessibility == Accessibility.Public && !field.IsStatic => field.Type,
				_ => null,
			};
			if (memberType is null)
				continue;
			var problem = Visit(memberType, compilation, visited);
			if (problem is not null)
				return $"member '{member.Name}' uses an unsupported type ({problem})";
		}
		return null;
	}

	private static bool IsKnownSystemValue(INamedTypeSymbol type) => type.ToDisplayString() is
		"System.Guid" or "System.DateTimeOffset" or "System.TimeSpan" or "System.DateOnly" or
		"System.TimeOnly" or "System.Uri" or "System.Version";

	private static bool VisitForNodaTime(ITypeSymbol type, HashSet<ITypeSymbol> visited)
	{
		if (!visited.Add(type))
			return false;
		if (type.ContainingNamespace?.ToDisplayString().StartsWith("NodaTime", StringComparison.Ordinal) == true)
			return true;
		if (type is IArrayTypeSymbol array && VisitForNodaTime(array.ElementType, visited))
			return true;
		if (type is INamedTypeSymbol named)
		{
			if (named.TypeArguments.Any(argument => VisitForNodaTime(argument, visited)))
				return true;
			return named.GetMembers().Any(member => member switch
			{
				IPropertySymbol property when property.DeclaredAccessibility == Accessibility.Public && !property.IsStatic => VisitForNodaTime(property.Type, visited),
				IFieldSymbol field when field.DeclaredAccessibility == Accessibility.Public && !field.IsStatic => VisitForNodaTime(field.Type, visited),
				_ => false,
			});
		}
		return false;
	}
}

internal static class CronValidator
{
	public static bool TryValidate(string cron, out string error)
	{
		if (string.IsNullOrWhiteSpace(cron))
		{
			error = "the expression is empty";
			return false;
		}
		var fields = cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (fields.Length is not (5 or 6))
		{
			error = "expected five or six fields";
			return false;
		}
		var ranges = fields.Length == 6
			? new[] { (0, 59), (0, 59), (0, 23), (1, 31), (1, 12), (0, 7) }
			: new[] { (0, 59), (0, 23), (1, 31), (1, 12), (0, 7) };
		for (var index = 0; index < fields.Length; index++)
		{
			if (!ValidateField(fields[index], ranges[index].Item1, ranges[index].Item2))
			{
				error = $"field {index + 1} is malformed or out of range";
				return false;
			}
		}
		error = string.Empty;
		return true;
	}

	private static bool ValidateField(string field, int minimum, int maximum)
	{
		foreach (var item in field.Split(','))
		{
			var pieces = item.Split('/');
			if (pieces.Length > 2 || pieces.Length == 2 && (!int.TryParse(pieces[1], out var step) || step <= 0))
				return false;
			var range = pieces[0];
			if (range == "*")
				continue;
			var bounds = range.Split('-');
			if (bounds.Length > 2 || !TryValue(bounds[0], minimum, maximum, out var first))
				return false;
			if (bounds.Length == 2 && (!TryValue(bounds[1], minimum, maximum, out var last) || last < first))
				return false;
		}
		return true;
	}

	private static bool TryValue(string value, int minimum, int maximum, out int parsed) =>
		int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= minimum && parsed <= maximum;
}
