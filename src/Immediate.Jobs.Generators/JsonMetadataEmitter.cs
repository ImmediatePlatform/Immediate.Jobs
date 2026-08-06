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
		var typeFullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

		return type switch
		{
			{ ConverterTypeName: { } converterName } =>
				new JsonTypeRenderModel
				{
					Index = index,
					TypeName = typeFullName,
					IsValueType = type.IsValueType,
					ConverterName = converterName,
					NullableUnderlyingTypeName = null,
					IsEnum = false,
					UsesConfiguredConverter = false,
					CollectionInfo = null,
					ObjectInfo = null,
				},

			{ TypeKind: TypeKind.Enum } =>
				new JsonTypeRenderModel
				{
					Index = index,
					TypeName = typeFullName,
					IsValueType = type.IsValueType,
					ConverterName = null,
					NullableUnderlyingTypeName = null,
					IsEnum = true,
					UsesConfiguredConverter = false,
					CollectionInfo = null,
					ObjectInfo = null,
				},

			{ NullableUnderlyingType: { } nullableUnderlyingType } =>
				new JsonTypeRenderModel
				{
					Index = index,
					TypeName = typeFullName,
					IsValueType = type.IsValueType,
					ConverterName = null,
					NullableUnderlyingTypeName = nullableUnderlyingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
					IsEnum = false,
					UsesConfiguredConverter = false,
					CollectionInfo = null,
					ObjectInfo = null,
				},

			_ when string.Equals(type.RootNamespace, "NodaTime", StringComparison.Ordinal) =>
				new JsonTypeRenderModel
				{
					Index = index,
					TypeName = typeFullName,
					IsValueType = type.IsValueType,
					ConverterName = null,
					NullableUnderlyingTypeName = null,
					IsEnum = false,
					UsesConfiguredConverter = true,
					CollectionInfo = null,
					ObjectInfo = null,
				},

			{ CollectionRenderModel: { } collectionInfo } =>
				new JsonTypeRenderModel
				{
					Index = index,
					TypeName = typeFullName,
					IsValueType = type.IsValueType,
					ConverterName = null,
					NullableUnderlyingTypeName = null,
					IsEnum = false,
					UsesConfiguredConverter = false,
					CollectionInfo = collectionInfo,
					ObjectInfo = null,
				},

			INamedTypeSymbol namedTypeSymbol =>
				new JsonTypeRenderModel
				{
					Index = index,
					TypeName = typeFullName,
					IsValueType = type.IsValueType,
					ConverterName = null,
					NullableUnderlyingTypeName = null,
					IsEnum = false,
					UsesConfiguredConverter = false,
					CollectionInfo = null,
					ObjectInfo = CreateObjectModel(namedTypeSymbol),
				},

			_ =>
				new JsonTypeRenderModel
				{
					Index = index,
					TypeName = typeFullName,
					IsValueType = type.IsValueType,
					ConverterName = null,
					NullableUnderlyingTypeName = null,
					IsEnum = false,
					UsesConfiguredConverter = false,
					CollectionInfo = null,
					ObjectInfo = null,
				},
		};
	}

	extension(ITypeSymbol type)
	{

		private JsonCollectionRenderModel? CollectionRenderModel
		{
			get
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
		}

		private string? ConverterTypeName
		{
			get
			{
				return type switch
				{
					IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } =>
						"ByteArrayConverter",

					{ SpecialType: SpecialType.System_Boolean } => "BooleanConverter",
					{ SpecialType: SpecialType.System_Byte } => "ByteConverter",
					{ SpecialType: SpecialType.System_SByte } => "SByteConverter",
					{ SpecialType: SpecialType.System_Int16 } => "Int16Converter",
					{ SpecialType: SpecialType.System_UInt16 } => "UInt16Converter",
					{ SpecialType: SpecialType.System_Int32 } => "Int32Converter",
					{ SpecialType: SpecialType.System_UInt32 } => "UInt32Converter",
					{ SpecialType: SpecialType.System_Int64 } => "Int64Converter",
					{ SpecialType: SpecialType.System_UInt64 } => "UInt64Converter",
					{ SpecialType: SpecialType.System_Char } => "CharConverter",
					{ SpecialType: SpecialType.System_Single } => "SingleConverter",
					{ SpecialType: SpecialType.System_Double } => "DoubleConverter",
					{ SpecialType: SpecialType.System_Decimal } => "DecimalConverter",
					{ SpecialType: SpecialType.System_String } => "StringConverter",
					{ SpecialType: SpecialType.System_DateTime } => "DateTimeConverter",
					{ SpecialType: SpecialType.System_Object } => "ObjectConverter",

					INamedTypeSymbol
					{
						Name: { } name,
						Arity: 0,
						ContainingNamespace.IsSystem: true,
					} => name switch
					{
						"Guid" => "GuidConverter",
						"DateTimeOffset" => "DateTimeOffsetConverter",
						"TimeSpan" => "TimeSpanConverter",
						"DateOnly" => "DateOnlyConverter",
						"TimeOnly" => "TimeOnlyConverter",
						"Uri" => "UriConverter",
						"Version" => "VersionConverter",
						_ => null,
					},

					_ => null,
				};
			}
		}
	}

	private static JsonObjectRenderModel CreateObjectModel(INamedTypeSymbol type)
	{
		var members = GetMembers(type);
		var constructor = GetConstructor(type, members);

		if (constructor is null)
		{
			return new JsonObjectRenderModel
			{
				HasParameterlessCreator = false,
				HasParameterizedCreator = false,

				ConstructorParameters = [],
				CreationParameters = [],

				InitializerMembers = [],

				Members = members
					.Select(member => new JsonMemberRenderModel
					{
						Name = member.Name.EscapeIdentifier(),
						NameLiteral = member.Name.AsCSharpLiteral(),
						TypeName = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
						CanSet = member is { SetMethod: { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } },
						IsConstructorBound = false,
					})
					.ToEquatableReadOnlyList(),
			};
		}

		var initializerMembers = members
			.Where(
				member =>
					!ConstructorContains(constructor, member.Name)
					&& member is { SetMethod.DeclaredAccessibility: Accessibility.Public }
						and ({ SetMethod.IsInitOnly: true } or { IsRequired: true })
			)
			.ToList();

		var constructorParameterModels = constructor.Parameters
			.Select((parameter, index) => new JsonConstructorParameterRenderModel
			{
				Index = index,
				NameLiteral = parameter.Name.AsCSharpLiteral(),
				TypeName = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				HasDefaultValueLiteral = parameter.HasExplicitDefaultValue ? "true" : "false",
			})
			.ToEquatableReadOnlyList();

		var creationParameters = constructorParameterModels
			.Concat(initializerMembers.Select((member, index) => new JsonConstructorParameterRenderModel
			{
				Index = constructor.Parameters.Length + index,
				NameLiteral = member.Name.AsCSharpLiteral(),
				TypeName = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				HasDefaultValueLiteral = member.IsRequired ? "false" : "true",
			}))
			.ToEquatableReadOnlyList();

		var initializerMemberNames = new HashSet<string>(
			initializerMembers.Select(static member => member.Name),
			StringComparer.OrdinalIgnoreCase
		);

		var isParameterless = constructor.Parameters is [] && initializerMembers is [];

		return new()
		{
			HasParameterlessCreator = isParameterless,
			HasParameterizedCreator = !isParameterless,

			ConstructorParameters = constructorParameterModels,
			CreationParameters = creationParameters,

			InitializerMembers = initializerMembers
				.Select((member, index) => new JsonInitializerMemberRenderModel
				{
					Index = constructor.Parameters.Length + index,
					Name = member.Name.EscapeIdentifier(),
					TypeName = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				})
				.ToEquatableReadOnlyList(),

			Members = members
				.Select(member => new JsonMemberRenderModel
				{
					Name = member.Name.EscapeIdentifier(),
					NameLiteral = member.Name.AsCSharpLiteral(),
					TypeName = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
					CanSet = member is { SetMethod: { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } },
					IsConstructorBound = ConstructorContains(constructor, member.Name) || initializerMemberNames.Contains(member.Name),
				})
				.ToEquatableReadOnlyList(),
		};
	}

	private static HashSet<ITypeSymbol> CollectTypes(IEnumerable<ITypeSymbol> roots)
	{
		var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

		foreach (var root in roots)
			VisitTypeCollector(root, visited);

		return visited;
	}

	private static void VisitTypeCollector(ITypeSymbol type, HashSet<ITypeSymbol> visited)
	{
		if (!visited.Add(type))
			return;

		if (type.ConverterTypeName is { })
			return;

		if (type.RootNamespace is "NodaTime")
			return;

		if (type.TypeKind is TypeKind.Enum)
			return;

		if (type.NullableUnderlyingType is { } underlyingType)
		{
			VisitTypeCollector(underlyingType, visited);
			return;
		}

		if (type.CollectionRenderModel is not null)
		{
			if (type is IArrayTypeSymbol array)
			{
				VisitTypeCollector(array.ElementType, visited);
			}
			else if (type is INamedTypeSymbol namedCollection)
			{
				foreach (var argument in namedCollection.TypeArguments)
					VisitTypeCollector(argument, visited);
			}

			return;
		}

		if (type is INamedTypeSymbol named)
		{
			foreach (var member in GetMembers(named))
				VisitTypeCollector(member.Type, visited);
		}
	}

	private static List<IPropertySymbol> GetMembers(INamedTypeSymbol type)
	{
		return type.GetMembers()
			.OfType<IPropertySymbol>()
			.Where(ips => ips is
			{
				IsStatic: false,
				DeclaredAccessibility: Accessibility.Public,
				GetMethod: not null,
				IsJobDetailsMember: false,
			})
			.ToList();
	}

	private static IMethodSymbol? GetConstructor(INamedTypeSymbol type, List<IPropertySymbol> members) =>
		type.InstanceConstructors
			.Where(c => c.DeclaredAccessibility == Accessibility.Public)
			.Where(c => c.Parameters.All(p => members.Exists(member => string.Equals(member.Name, p.Name, StringComparison.OrdinalIgnoreCase))))
			.OrderByDescending(c => c.Parameters.Length)
			.FirstOrDefault();

	private static bool ConstructorContains(IMethodSymbol constructor, string name) =>
		constructor.Parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));
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
	public required bool CanSet { get; init; }
	public required bool IsConstructorBound { get; init; }
}
