using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Immediate.Jobs.Generators;

internal static class JsonMetadataEmitter
{
	private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

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
		var isEnum = type.TypeKind == TypeKind.Enum;
		var usesConfiguredConverter = type.RootNamespace == "NodaTime";
		var collectionInfo = converterName is null ? CreateCollectionModel(type) : null;

		JsonObjectRenderModel? objectInfo = null;
		if (converterName is null && !isEnum && !usesConfiguredConverter && collectionInfo is null)
			objectInfo = CreateObjectModel((INamedTypeSymbol)type);

		return new JsonTypeRenderModel
		{
			Index = index,
			TypeName = Display(type),
			IsValueType = type.IsValueType,
			ConverterName = converterName,
			IsEnum = isEnum,
			UsesConfiguredConverter = usesConfiguredConverter,
			CollectionInfo = collectionInfo,
			ObjectInfo = objectInfo,
		};
	}

	private static JsonCollectionRenderModel? CreateCollectionModel(ITypeSymbol type) => type switch
	{
		IArrayTypeSymbol { Rank: 1 } array => new()
		{
			IsArray = true,
			IsDictionary = false,
			ElementTypeName = Display(array.ElementType),
			KeyTypeName = null,
		},
		INamedTypeSymbol
		{
			OriginalDefinition:
			{
				Name: "List",
				Arity: 1,
				ContainingNamespace.IsSystemCollectionsGeneric: true,
			},
			TypeArguments: [{ } elementType],
		} => new()
		{
			IsArray = false,
			IsDictionary = false,
			ElementTypeName = Display(elementType),
			KeyTypeName = null,
		},
		INamedTypeSymbol
		{
			OriginalDefinition:
			{
				Name: "Dictionary",
				Arity: 2,
				ContainingNamespace.IsSystemCollectionsGeneric: true,
			},
			TypeArguments: [{ } keyType, { } valueType],
		} => new()
		{
			IsArray = false,
			IsDictionary = true,
			ElementTypeName = Display(valueType),
			KeyTypeName = Display(keyType),
		},
		_ => null,
	};

	private static JsonObjectRenderModel CreateObjectModel(INamedTypeSymbol type)
	{
		var members = GetMembers(type);
		var constructor = GetConstructor(type, members);
		var constructorParameters = constructor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty;

		return new()
		{
			HasParameterlessCreator = constructor is { Parameters: [] },
			HasParameterizedCreator = constructor is { Parameters.Length: > 0 },

			ConstructorParameters = constructorParameters
				.Select((parameter, index) => new JsonConstructorParameterRenderModel
				{
					Index = index,
					NameLiteral = Literal(parameter.Name),
					TypeName = Display(parameter.Type),
					HasDefaultValueLiteral = parameter.HasExplicitDefaultValue ? "true" : "false",
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
						TypeName = Display(memberType),
						IsPropertyLiteral = member is IPropertySymbol ? "true" : "false",
						CanSet = CanSet(member),
						IsConstructorBound = constructor is not null && ConstructorContains(constructor, member.Name),
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
				ContainingNamespace:
				{
					Name: "System",
					ContainingNamespace.IsGlobalNamespace: true,
				},
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

	private static string Display(ITypeSymbol type) =>
		type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(TypeFormat);

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
	public required bool IsEnum { get; init; }
	public required bool UsesConfiguredConverter { get; init; }
	public required JsonCollectionRenderModel? CollectionInfo { get; init; }
	public required JsonObjectRenderModel? ObjectInfo { get; init; }
}

internal sealed record JsonCollectionRenderModel
{
	public required bool IsArray { get; init; }
	public required bool IsDictionary { get; init; }
	public required string ElementTypeName { get; init; }
	public required string? KeyTypeName { get; init; }
}

internal sealed record JsonObjectRenderModel
{
	public required bool HasParameterlessCreator { get; init; }
	public required bool HasParameterizedCreator { get; init; }
	public required EquatableReadOnlyList<JsonConstructorParameterRenderModel> ConstructorParameters { get; init; }
	public required EquatableReadOnlyList<JsonMemberRenderModel> Members { get; init; }
}

internal sealed record JsonConstructorParameterRenderModel
{
	public required int Index { get; init; }
	public required string NameLiteral { get; init; }
	public required string TypeName { get; init; }
	public required string HasDefaultValueLiteral { get; init; }
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
