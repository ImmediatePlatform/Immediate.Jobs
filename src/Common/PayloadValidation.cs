using Microsoft.CodeAnalysis;

namespace Immediate.Jobs;

internal static class PayloadValidation
{
	public static bool CanSerializeToJson(this ITypeSymbol type, Action<string, Location?>? reportError) =>
		Visit(type, location: null, reportError, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

	private static bool Visit(ITypeSymbol type, Location? location, Action<string, Location?>? reportError, HashSet<ITypeSymbol> visited)
	{
		if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter || type.IsRefLikeType)
		{
			reportError?.Invoke("the type is pointer-like, ref-like, or open generic", location);
			return false;
		}

		if (type.TypeKind == TypeKind.Interface || type is INamedTypeSymbol { IsAbstract: true })
		{
			reportError?.Invoke("interfaces and abstract types do not have a statically known JSON shape", location);
			return false;
		}

		if (type.TypeKind == TypeKind.Delegate || type.SpecialType == SpecialType.System_Delegate || type.ToDisplayString() == "System.Type")
		{
			reportError?.Invoke("delegates and System.Type are not supported payload values", location);
			return false;
		}

		if (type is IArrayTypeSymbol array)
			return Visit(array.ElementType, location, reportError, visited);

		if (type is not INamedTypeSymbol named)
			return true;

		if (named.TypeArguments.Length > 0
			&& named.AllInterfaces.Any(static i => i.IsIEnumerable1))
		{
			var canSerialize = true;
			foreach (var argument in named.TypeArguments)
			{
				var result = Visit(argument, location, reportError, visited);
				if (reportError is null && !result)
					return false;
				canSerialize &= result;
			}

			return canSerialize;
		}

		foreach (var argument in named.TypeArguments)
		{
			var result = Visit(argument, argument.Locations.FirstOrDefault(), reportError, visited);
			if (reportError is null && !result)
				return false;
		}

		if (!visited.Add(type))
			return true;

		if (named is { SpecialType: not SpecialType.None }
				or { TypeKind: TypeKind.Enum }
				or { IsKnownSystemValue: true })
		{
			return true;
		}

		var rootNamespace = named.RootNamespace;

		if (rootNamespace == "NodaTime")
			return true;

		if (rootNamespace == "System")
		{
			reportError?.Invoke("this System type does not have a generated metadata contract", location);
			return false;
		}

		foreach (var member in named.GetMembers()
			.Where(s => s is IPropertySymbol or IFieldSymbol)
			.Where(ips => ips is
			{
				IsStatic: false,
				DeclaredAccessibility: Accessibility.Public,
			})
			.Where(ips => ips is
				IPropertySymbol
				{
					GetMethod: not null,
					IsJobDetailsMember: false,
				}
				or IFieldSymbol { IsImplicitlyDeclared: true }
			))
		{
			var memberType = member switch { IPropertySymbol ips => ips.Type, IFieldSymbol ifs => ifs.Type, _ => null };

			var result = Visit(memberType!, member.Locations.FirstOrDefault(), reportError, visited);
			if (reportError is null && !result)
				return false;
		}

		return true;
	}

	extension(IPropertySymbol symbol)
	{
		public bool IsJobDetailsMember =>
			symbol is
			{
				Name: "JobDetails",
				Type.IsJobDetails: true,
			};
	}

	extension(INamedTypeSymbol type)
	{
		private bool IsKnownSystemValue =>
			type is
			{
				Arity: 0,
				ContainingNamespace:
				{
					Name: "System",
					ContainingNamespace.IsGlobalNamespace: true,
				},
				Name: "Guid" or "DateTimeOffset" or "TimeSpan" or "DateOnly" or "TimeOnly" or "Uri" or "Version",
			};
	}
}
