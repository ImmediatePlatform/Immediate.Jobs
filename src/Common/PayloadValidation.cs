using Microsoft.CodeAnalysis;

namespace Immediate.Jobs;

internal static class PayloadValidation
{
	public static string? FindProblem(ITypeSymbol type)
	{
		var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
		return Visit(type, visited);
	}

	public static bool ContainsNodaTime(ITypeSymbol type)
	{
		var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
		return VisitForNodaTime(type, visited);
	}

	private static string? Visit(ITypeSymbol type, HashSet<ITypeSymbol> visited)
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
			return Visit(array.ElementType, visited);
		if (type is not INamedTypeSymbol named)
			return null;

		foreach (var argument in named.TypeArguments)
		{
			var problem = Visit(argument, visited);
			if (problem is not null)
				return problem;
		}

		if (named.SpecialType != SpecialType.None ||
			named.TypeKind == TypeKind.Enum ||
			IsKnownSystemValue(named) ||
			named.ContainingNamespace.ToDisplayString().StartsWith("NodaTime", StringComparison.Ordinal))
			return null;
		if (named.ContainingNamespace.ToDisplayString().StartsWith("System", StringComparison.Ordinal))
			return "this System type does not have a generated metadata contract";
		if (named.TypeKind == TypeKind.Class && !named.InstanceConstructors.Any(constructor =>
			constructor.DeclaredAccessibility == Accessibility.Public &&
			constructor.Parameters.All(parameter => named.GetMembers().Any(member =>
				member.DeclaredAccessibility == Accessibility.Public &&
				string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))))
			return "the type has no public constructor that can be bound to its public members";

		foreach (var member in named.GetMembers().Where(static member => !IsJobDetailsMember(member)))
		{
			ITypeSymbol? memberType = member switch
			{
				IPropertySymbol property when property.DeclaredAccessibility == Accessibility.Public &&
					!property.IsStatic && property.GetMethod is not null => property.Type,
				IFieldSymbol field when field.DeclaredAccessibility == Accessibility.Public && !field.IsStatic => field.Type,
				_ => null,
			};
			if (memberType is null)
				continue;

			var problem = Visit(memberType, visited);
			if (problem is not null)
				return $"member '{member.Name}' uses an unsupported type ({problem})";
		}

		return null;
	}

	public static bool IsJobDetailsMember(ISymbol member)
	{
		if (member.ContainingType is null)
			return false;

		if (member.Name != "JobDetails")
			return false;

		var jobRequest = member.ContainingType.AllInterfaces
			.FirstOrDefault(static candidate => candidate.IsIJobRequest);
		var details = jobRequest?.GetMembers("JobDetails").OfType<IPropertySymbol>().SingleOrDefault();
		return details is not null && SymbolEqualityComparer.Default.Equals(
			member.ContainingType.FindImplementationForInterfaceMember(details),
			member
		);
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
		if (type is not INamedTypeSymbol named)
			return false;
		if (named.TypeArguments.Any(argument => VisitForNodaTime(argument, visited)))
			return true;

		return named.GetMembers()
			.Where(static member => !IsJobDetailsMember(member))
			.Any(member => member switch
			{
				IPropertySymbol property when property.DeclaredAccessibility == Accessibility.Public && !property.IsStatic =>
					VisitForNodaTime(property.Type, visited),
				IFieldSymbol field when field.DeclaredAccessibility == Accessibility.Public && !field.IsStatic =>
					VisitForNodaTime(field.Type, visited),
				_ => false,
			});
	}
}
