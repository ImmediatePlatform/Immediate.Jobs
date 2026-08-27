using Microsoft.CodeAnalysis;

namespace Immediate.Jobs;

internal static class PayloadValidation
{
	public static bool CanSerializeToJson(this ITypeSymbol type, Action<string, Location?>? reportError) =>
		Visit(type, location: null, reportError, [with(SymbolEqualityComparer.Default)]);

	private static bool Visit(ITypeSymbol type, Location? location, Action<string, Location?>? reportError, HashSet<ITypeSymbol> visited)
	{
		switch (type)
		{
			case IArrayTypeSymbol { Rank: not 1 }:
			{
				reportError?.Invoke("multi-dimensional arrays are not supported by generated JSON metadata", location);
				return false;
			}

			case IArrayTypeSymbol { ElementType: { } elementType }:
				return Visit(elementType, location, reportError, visited);

			case { NullableUnderlyingType: { } underlyingType }:
				return Visit(underlyingType, location, reportError, visited);

			case INamedTypeSymbol
			{
				Name: "List" or "IList" or "IReadOnlyList" or "IEnumerable",
				Arity: 1,
				ContainingNamespace.IsSystemCollectionsGeneric: true,
				TypeArguments: [{ } elementType],
			}:
				return Visit(elementType, location, reportError, visited);

			case INamedTypeSymbol
			{
				Name: "Dictionary" or "IDictionary" or "IReadOnlyDictionary",
				Arity: 2,
				ContainingNamespace.IsSystemCollectionsGeneric: true,
				TypeArguments: [{ } keyType, { } elementType],
			}:
			{
				if (!keyType.IsSupportedJsonDictionaryKey)
				{
					reportError?.Invoke("the dictionary key type is not supported by System.Text.Json", location);
					if (reportError is null)
						return false;
				}

				return Visit(elementType, location, reportError, visited);
			}

			case { TypeKind: TypeKind.Interface } or INamedTypeSymbol { IsAbstract: true }:
			{
				reportError?.Invoke("interfaces and abstract types do not have a statically known JSON shape", location);
				return false;
			}

			case { TypeKind: TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter }
				or { IsRefLikeType: true }:
			{
				reportError?.Invoke("the type is pointer-like, ref-like, or open generic", location);
				return false;
			}

			case { TypeKind: TypeKind.Delegate } or { SpecialType: SpecialType.System_Delegate }
				or INamedTypeSymbol { Arity: 0, Name: "Type", ContainingNamespace.IsSystem: true, }:
			{
				reportError?.Invoke("delegates and System.Type are not supported payload values", location);
				return false;
			}

			case not INamedTypeSymbol:
				return true;

			case { SpecialType: not SpecialType.None }
				or { TypeKind: TypeKind.Enum }
				or INamedTypeSymbol { IsKnownSystemValue: true }:
			{
				return true;
			}

			default:
				break;
		}

		// secured by `case not INamedTypeSymbol:` above
		var named = (INamedTypeSymbol)type;

		foreach (var argument in named.TypeArguments)
		{
			var result = Visit(argument, argument.Locations.FirstOrDefault(), reportError, visited);
			if (reportError is null && !result)
				return false;
		}

		if (!visited.Add(type))
			return true;

		var rootNamespace = named.RootNamespace;

		if (string.Equals(rootNamespace, "NodaTime", StringComparison.Ordinal))
			return true;

		if (string.Equals(rootNamespace, "System", StringComparison.Ordinal))
		{
			reportError?.Invoke("this System type does not have a generated metadata contract", location);
			return false;
		}

		foreach (var member in named.GetMembers()
			.Where(ips => ips is
				IPropertySymbol
				{
					GetMethod: not null,
					IsJobDetailsMember: false,
				}
				or IFieldSymbol { IsImplicitlyDeclared: true }
			)
			.Where(ips => ips is
			{
				IsStatic: false,
				DeclaredAccessibility: Accessibility.Public,
			}))
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
