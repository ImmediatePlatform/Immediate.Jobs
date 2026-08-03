using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Immediate.Jobs.Generators;

internal static class JsonMetadataEmitter
{
	public static JsonMetadataRenderModel CreateModel(
		ITypeSymbol payloadType,
		IEnumerable<ITypeSymbol> contextTypes
	)
	{
		var types = CollectTypes([payloadType, .. contextTypes]);

		return new()
		{
			PayloadTypeName = payloadType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
			Types = types.Select(CreateTypeModel).ToEquatableReadOnlyList(),
		};
	}

	private static JsonTypeRenderModel CreateTypeModel(ITypeSymbol type, int index)
	{
		var converterName = GetConverter(type);
		var nullableUnderlyingType = GetNullableUnderlyingType(type);
		var isEnum = type.TypeKind == TypeKind.Enum;
		var usesConfiguredConverter = string.Equals(type.RootNamespace, "NodaTime", StringComparison.Ordinal);
		var collectionInfo = converterName is null ? CreateCollectionModel(type) : null;

		JsonObjectRenderModel? objectInfo = null;
		if (converterName is null && nullableUnderlyingType is null && !isEnum && !usesConfiguredConverter && collectionInfo is null
			&& type is INamedTypeSymbol namedType)
		{
			objectInfo = CreateObjectModel(namedType);
		}

		return new JsonTypeRenderModel
		{
			Index = index,
			TypeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
			IsValueType = type.IsValueType,
			ConverterName = converterName,
			NullableUnderlyingTypeName = nullableUnderlyingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
			IsEnum = isEnum,
			UsesConfiguredConverter = usesConfiguredConverter,
			CollectionInfo = collectionInfo,
			ObjectInfo = objectInfo,
		};
	}

	private static JsonCollectionRenderModel? CreateCollectionModel(ITypeSymbol type)
	{
		return type switch
		{
			IArrayTypeSymbol { Rank: 1, ElementType: { } elementType } => new()
			{
				CollectionType = "Array",
				ElementTypeName = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				KeyTypeName = null,
			},

			INamedTypeSymbol
			{
				Name: ("List" or "IList" or "IEnumerable") and var name,
				Arity: 1,
				ContainingNamespace.IsSystemCollectionsGeneric: true,
				TypeArguments: [{ } elementType],
			} => new()
			{
				CollectionType = name,
				ElementTypeName = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				KeyTypeName = null,
			},

			INamedTypeSymbol
			{
				Name: "IReadOnlyList",
				Arity: 1,
				ContainingNamespace.IsSystemCollectionsGeneric: true,
				TypeArguments: [{ } elementType],
			} => new()
			{
				CollectionType = "IEnumerable",
				ElementTypeName = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				KeyTypeName = null,
			},

			INamedTypeSymbol
			{
				Name: ("Dictionary" or "IDictionary" or "IReadOnlyDictionary") and var name,
				Arity: 2,
				ContainingNamespace.IsSystemCollectionsGeneric: true,
				TypeArguments: [{ } keyType, { } elementType],
			} => new()
			{
				CollectionType = name,
				ElementTypeName = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				KeyTypeName = keyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
			},

			_ => null,
		};
	}

	private static JsonObjectRenderModel CreateObjectModel(INamedTypeSymbol type)
	{
		var members = GetMembers(type);
		var constructor = GetConstructor(type, members);
		var constructorParameters = constructor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty;
		var initializerMembers = constructor is null
			? []
			: members
				.Where(member => !ConstructorContains(constructor, member.Name) && CanInitialize(member))
				.ToList();
		var constructorParameterModels = constructorParameters
			.Select((parameter, index) => new JsonConstructorParameterRenderModel
			{
				Index = index,
				NameLiteral = Literal(parameter.Name),
				TypeName = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				HasDefaultValueLiteral = parameter.HasExplicitDefaultValue ? "true" : "false",
			})
			.ToEquatableReadOnlyList();
		var creationParameters = constructorParameterModels
			.Concat(initializerMembers.Select((member, index) => new JsonConstructorParameterRenderModel
			{
				Index = constructorParameters.Length + index,
				NameLiteral = Literal(member.Name),
				TypeName = GetMemberType(member).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				HasDefaultValueLiteral = member is IPropertySymbol { IsRequired: true } ? "false" : "true",
			}))
			.ToEquatableReadOnlyList();
		var initializerMemberNames = new HashSet<string>(
			initializerMembers.Select(static member => member.Name),
			StringComparer.OrdinalIgnoreCase
		);

		return new()
		{
			HasParameterlessCreator = constructor is { Parameters: [] } && initializerMembers.Count == 0,
			HasParameterizedCreator = constructor is not null
				&& (constructor.Parameters.Length > 0 || initializerMembers.Count > 0),

			ConstructorParameters = constructorParameterModels,
			CreationParameters = creationParameters,

			InitializerMembers = initializerMembers
				.Select((member, index) => new JsonInitializerMemberRenderModel
				{
					Index = constructorParameters.Length + index,
					Name = Escape(member.Name),
					TypeName = GetMemberType(member).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				})
				.ToEquatableReadOnlyList(),

			Members = members
				.Select(member =>
				{
					var memberType = GetMemberType(member);
					return new JsonMemberRenderModel
					{
						Name = Escape(member.Name),
						NameLiteral = Literal(member.Name),
						TypeName = memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
						IsPropertyLiteral = member is IPropertySymbol ? "true" : "false",
						CanSet = CanSet(member),
						IsConstructorBound = constructor is not null
							&& (ConstructorContains(constructor, member.Name) || initializerMemberNames.Contains(member.Name)),
					};
				})
				.ToEquatableReadOnlyList(),
		};
	}

	private static HashSet<ITypeSymbol> CollectTypes(IEnumerable<ITypeSymbol> roots)
	{
		var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
		void Visit(ITypeSymbol type)
		{
			if (!visited.Add(type))
				return;

			if (GetNullableUnderlyingType(type) is { } underlyingType)
			{
				Visit(underlyingType);
				return;
			}

			if (CreateCollectionModel(type) is not null)
			{
				if (type is IArrayTypeSymbol array)
				{
					Visit(array.ElementType);
				}
				else if (type is INamedTypeSymbol namedCollection)
				{
					foreach (var argument in namedCollection.TypeArguments)
						Visit(argument);
				}

				return;
			}

			if (type is INamedTypeSymbol { TypeKind: not TypeKind.Enum, RootNamespace: not "NodaTime" } named && GetConverter(named) is null)
			{
				foreach (var member in GetMembers(named))
					Visit(GetMemberType(member));
			}
		}

		foreach (var root in roots)
			Visit(root);

		return visited;
	}

	private static ITypeSymbol? GetNullableUnderlyingType(ITypeSymbol type) =>
		type is INamedTypeSymbol
		{
			OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
			TypeArguments: [{ } underlyingType],
		}
			? underlyingType
			: null;

	private static List<ISymbol> GetMembers(INamedTypeSymbol type)
	{
		return type.GetMembers()
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
			)
			.ToList();
	}

	private static IMethodSymbol? GetConstructor(INamedTypeSymbol type, List<ISymbol> members) =>
		type.InstanceConstructors
			.Where(c => c.DeclaredAccessibility == Accessibility.Public)
			.Where(c => c.Parameters.All(p => members.Any(member => string.Equals(member.Name, p.Name, StringComparison.OrdinalIgnoreCase))))
			.OrderByDescending(c => c.Parameters.Length)
			.FirstOrDefault();

	private static bool ConstructorContains(IMethodSymbol constructor, string name) =>
		constructor.Parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));

	private static ITypeSymbol GetMemberType(ISymbol member) =>
		member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;

	private static bool CanSet(ISymbol member) => member switch
	{
		IPropertySymbol property => property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false },
		IFieldSymbol field => !field.IsReadOnly && !field.IsConst,
		_ => false,
	};

	private static bool CanInitialize(ISymbol member) =>
		member is IPropertySymbol
		{
			SetMethod: { DeclaredAccessibility: Accessibility.Public } setter,
		} property
		&& (setter.IsInitOnly || property.IsRequired);

	private static string? GetConverter(ITypeSymbol type)
	{
		if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
			return "ByteArrayConverter";

		if (type.SpecialType == SpecialType.System_Boolean) return "BooleanConverter";
		if (type.SpecialType == SpecialType.System_Byte) return "ByteConverter";
		if (type.SpecialType == SpecialType.System_SByte) return "SByteConverter";
		if (type.SpecialType == SpecialType.System_Int16) return "Int16Converter";
		if (type.SpecialType == SpecialType.System_UInt16) return "UInt16Converter";
		if (type.SpecialType == SpecialType.System_Int32) return "Int32Converter";
		if (type.SpecialType == SpecialType.System_UInt32) return "UInt32Converter";
		if (type.SpecialType == SpecialType.System_Int64) return "Int64Converter";
		if (type.SpecialType == SpecialType.System_UInt64) return "UInt64Converter";
		if (type.SpecialType == SpecialType.System_Char) return "CharConverter";
		if (type.SpecialType == SpecialType.System_Single) return "SingleConverter";
		if (type.SpecialType == SpecialType.System_Double) return "DoubleConverter";
		if (type.SpecialType == SpecialType.System_Decimal) return "DecimalConverter";
		if (type.SpecialType == SpecialType.System_String) return "StringConverter";
		if (type.SpecialType == SpecialType.System_DateTime) return "DateTimeConverter";
		if (type.SpecialType == SpecialType.System_Object) return "ObjectConverter";

		if (type is not INamedTypeSymbol
			{
				Arity: 0,
				ContainingNamespace.IsSystem: true,
			})
		{
			return null;
		}

		return type.Name switch
		{
			"Guid" => "GuidConverter",
			"DateTimeOffset" => "DateTimeOffsetConverter",
			"TimeSpan" => "TimeSpanConverter",
			"DateOnly" => "DateOnlyConverter",
			"TimeOnly" => "TimeOnlyConverter",
			"Uri" => "UriConverter",
			"Version" => "VersionConverter",
			_ => null,
		};
	}

	private static string Literal(string value) =>
		Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

	private static string Escape(string value) =>
		Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(value) == Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
			? value
			: "@" + value;
}

internal sealed record JsonMetadataRenderModel
{
	public required string PayloadTypeName { get; init; }
	public required EquatableReadOnlyList<JsonTypeRenderModel> Types { get; init; }
}

internal sealed record JsonTypeRenderModel
{
	public required int Index { get; init; }
	public required string TypeName { get; init; }
	public required bool IsValueType { get; init; }
	public required string? ConverterName { get; init; }
	public required string? NullableUnderlyingTypeName { get; init; }
	public required bool IsEnum { get; init; }
	public required bool UsesConfiguredConverter { get; init; }
	public required JsonCollectionRenderModel? CollectionInfo { get; init; }
	public required JsonObjectRenderModel? ObjectInfo { get; init; }
}

internal sealed record JsonCollectionRenderModel
{
	public required string CollectionType { get; init; }
	public required string ElementTypeName { get; init; }
	public required string? KeyTypeName { get; init; }
}

internal sealed record JsonObjectRenderModel
{
	public required bool HasParameterlessCreator { get; init; }
	public required bool HasParameterizedCreator { get; init; }
	public required EquatableReadOnlyList<JsonConstructorParameterRenderModel> ConstructorParameters { get; init; }
	public required EquatableReadOnlyList<JsonConstructorParameterRenderModel> CreationParameters { get; init; }
	public required EquatableReadOnlyList<JsonInitializerMemberRenderModel> InitializerMembers { get; init; }
	public required EquatableReadOnlyList<JsonMemberRenderModel> Members { get; init; }
}

internal sealed record JsonConstructorParameterRenderModel
{
	public required int Index { get; init; }
	public required string NameLiteral { get; init; }
	public required string TypeName { get; init; }
	public required string HasDefaultValueLiteral { get; init; }
}

internal sealed record JsonInitializerMemberRenderModel
{
	public required int Index { get; init; }
	public required string Name { get; init; }
	public required string TypeName { get; init; }
}

internal sealed record JsonMemberRenderModel
{
	public required string Name { get; init; }
	public required string NameLiteral { get; init; }
	public required string TypeName { get; init; }
	public required string IsPropertyLiteral { get; init; }
	public required bool CanSet { get; init; }
	public required bool IsConstructorBound { get; init; }
}
