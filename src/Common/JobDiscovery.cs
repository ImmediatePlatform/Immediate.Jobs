using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Immediate.Jobs;

internal readonly record struct JobContextUse(
	INamedTypeSymbol ExtractorType,
	ITypeSymbol? ContextType,
	AttributeData AppliedAttribute
);

internal static class JobDiscovery
{
	public static ImmutableArray<INamedTypeSymbol> FindJobs(Compilation compilation, CancellationToken cancellationToken) =>
		FindAttributedClasses(compilation, GetJobAttribute, cancellationToken);

	public static ImmutableArray<INamedTypeSymbol> FindQueueDefinitions(
		Compilation compilation,
		CancellationToken cancellationToken
	) => FindAttributedClasses(compilation, GetQueueDefinitionAttribute, cancellationToken);

	private static ImmutableArray<INamedTypeSymbol> FindAttributedClasses(
		Compilation compilation,
		Func<INamedTypeSymbol, AttributeData?> getAttribute,
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
				if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol ||
					getAttribute(symbol) is null ||
					builder.Any(existing => SymbolEqualityComparer.Default.Equals(existing, symbol)))
					continue;

				builder.Add(symbol);
			}
		}

		return builder.ToImmutable();
	}

	public static AttributeData? GetJobAttribute(INamedTypeSymbol symbol) =>
		symbol.GetAttributes().FirstOrDefault(static attribute =>
			attribute.AttributeClass.IsJobAttribute);

	public static AttributeData? GetQueueDefinitionAttribute(INamedTypeSymbol symbol) =>
		symbol.GetAttributes().FirstOrDefault(static attribute =>
			attribute.AttributeClass.IsQueueDefinitionAttribute);

	public static AttributeData? GetUsesQueueAttribute(INamedTypeSymbol symbol) =>
		symbol.GetAttributes().FirstOrDefault(static attribute =>
			attribute.AttributeClass.IsUsesQueueAttribute);

	public static bool IsHandler(INamedTypeSymbol symbol) =>
		symbol.GetAttributes().Any(static attribute =>
			attribute.AttributeClass.IsHandlerAttribute);

	public static bool IsJobDetailsMember(ISymbol member)
	{
		if (member.ContainingType is null)
			return false;

		var jobRequest = member.ContainingType.AllInterfaces
			.FirstOrDefault(static candidate => candidate.IsIJobRequest);
		var details = jobRequest?.GetMembers("JobDetails").OfType<IPropertySymbol>().SingleOrDefault();
		return details is not null && SymbolEqualityComparer.Default.Equals(
			member.ContainingType.FindImplementationForInterfaceMember(details),
			member
		);
	}

	public static ImmutableArray<JobContextUse> GetJobContextUses(INamedTypeSymbol job)
	{
		var attributes = job.GetAttributes();
		var builder = ImmutableArray.CreateBuilder<JobContextUse>();
		var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

		foreach (var attribute in attributes.Where(IsUsesJobContextAttribute))
			Add(attribute, attribute);

		foreach (var marker in attributes.Where(static attribute => !IsUsesJobContextAttribute(attribute)))
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
				.Where(static candidate => candidate.OriginalDefinition.IsIJobContextExtractor1)
				.ToArray();
			builder.Add(new(extractor, interfaces.Length == 1 ? interfaces[0].TypeArguments[0] : null, appliedAttribute));
		}
	}

	private static bool IsUsesJobContextAttribute(AttributeData attribute) =>
		attribute.AttributeClass.IsUsesJobContextAttribute;

	public static bool IsValidMethod(
		IMethodSymbol method,
		out ITypeSymbol? payloadType,
		out bool hasPayload
	)
	{
		payloadType = null;
		hasPayload = false;
		if (method.IsStatic ||
			method.IsGenericMethod ||
			method.MethodKind != MethodKind.Ordinary ||
			method.DeclaredAccessibility != Accessibility.Private ||
			!method.ReturnType.IsValueTask ||
			method.Parameters.Length != 2 ||
			!method.Parameters[1].Type.IsCancellationToken ||
			method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
			return false;

		payloadType = method.Parameters[0].Type;
		hasPayload = !payloadType.IsEmptyJobRequest;
		return true;
	}

	public static string GetName(INamedTypeSymbol type)
	{
		var attribute = GetJobAttribute(type)!;
		var explicitName = attribute.ConstructorArguments.Length == 1
			? attribute.ConstructorArguments[0].Value as string
			: null;
		return string.IsNullOrWhiteSpace(explicitName) ? type.Name.AsJobName() : explicitName!;
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

		name = GetNamedString(definition, "Name") ?? queueType.Name.AsQueueName();
		priority = GetNamedInt(definition, "Priority", 0);
		concurrency = GetNamedInt(definition, "Concurrency", 0);
		return !string.IsNullOrWhiteSpace(name) && name != "default" && concurrency >= 0;
	}

	public static string GetQueueName(INamedTypeSymbol queueType)
	{
		var definition = GetQueueDefinitionAttribute(queueType)!;
		return GetNamedString(definition, "Name") ?? queueType.Name.AsQueueName();
	}

	public static string? GetNamedString(AttributeData attribute, string name) =>
		attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as string;

	public static int GetNamedInt(AttributeData attribute, string name, int fallback)
	{
		var pair = attribute.NamedArguments.FirstOrDefault(candidate => candidate.Key == name);
		return pair.Key is null || pair.Value.Value is not int value ? fallback : value;
	}

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
		if (timeout is not null &&
			(!TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var timeoutValue) || timeoutValue <= TimeSpan.Zero))
			return "Timeout must be a positive TimeSpan";

		var backoffBase = GetNamedString(attribute, "BackoffBase") ?? "00:00:05";
		return !TimeSpan.TryParse(backoffBase, CultureInfo.InvariantCulture, out var backoffValue) || backoffValue <= TimeSpan.Zero
			? "BackoffBase must be a positive TimeSpan"
			: null;
	}
}
