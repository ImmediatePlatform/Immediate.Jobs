using System.Collections.Immutable;
using Immediate.Jobs;
using Microsoft.CodeAnalysis;

namespace Immediate.Jobs.Generators;

internal static class JsonMetadataEmitter
{
	private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

	public static JsonMetadataRenderModel CreateModel(
		ITypeSymbol payloadType,
		ImmutableArray<ITypeSymbol> contextTypes
	)
	{
		var types = CollectTypes(contextTypes.Insert(0, payloadType));
		return new()
		{
			PayloadTypeName = Display(payloadType),
			Types = types.Select(CreateTypeModel).ToEquatableReadOnlyList(),
		};
	}

	private static JsonTypeRenderModel CreateTypeModel(ITypeSymbol type, int index)
	{
		var converterName = GetConverter(type);
		var isEnum = type.TypeKind == TypeKind.Enum;
		var usesConfiguredConverter = UsesConfiguredConverter(type);

		string? elementTypeName = null;
		JsonObjectRenderModel? objectInfo = null;
		if (type is IArrayTypeSymbol { Rank: 1 } array)
			elementTypeName = Display(array.ElementType);
		else if (converterName is null && !isEnum && !usesConfiguredConverter)
			objectInfo = CreateObjectModel((INamedTypeSymbol)type);

		return new JsonTypeRenderModel
		{
			Index = index,
			TypeName = Display(type),
			ConverterName = converterName,
			IsEnum = isEnum,
			UsesConfiguredConverter = usesConfiguredConverter,
			ElementTypeName = elementTypeName,
			ObjectInfo = objectInfo,
		};
	}

	private static JsonObjectRenderModel CreateObjectModel(INamedTypeSymbol type)
	{
		var members = GetMembers(type);
		var constructor = GetConstructor(type, members);
		return new()
		{
			HasParameterlessCreator = constructor is null || constructor.Parameters.Length == 0,
			ConstructorParameters = (constructor is null ? ImmutableArray<IParameterSymbol>.Empty : constructor.Parameters)
				.Select((parameter, index) => new JsonConstructorParameterRenderModel
				{
					Index = index,
					NameLiteral = Literal(parameter.Name),
					TypeName = Display(parameter.Type),
					HasDefaultValueLiteral = parameter.HasExplicitDefaultValue ? "true" : "false",
				})
				.ToEquatableReadOnlyList(),
			Members = members.Select(member =>
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
			}).ToEquatableReadOnlyList(),
		};
	}

	private static ImmutableArray<ITypeSymbol> CollectTypes(IEnumerable<ITypeSymbol> roots)
	{
		var builder = ImmutableArray.CreateBuilder<ITypeSymbol>();
		var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
		void Visit(ITypeSymbol type)
		{
			type = type.WithNullableAnnotation(NullableAnnotation.None);
			if (!visited.Add(type))
				return;
			builder.Add(type);
			if (type is IArrayTypeSymbol array)
				Visit(array.ElementType);
			if (type is INamedTypeSymbol named && GetConverter(named) is null && named.TypeKind != TypeKind.Enum && !UsesConfiguredConverter(named))
			{
				foreach (var member in GetMembers(named))
					Visit(GetMemberType(member));
			}
		}
		foreach (var root in roots)
			Visit(root);
		return builder.ToImmutable();
	}

	private static ImmutableArray<ISymbol> GetMembers(INamedTypeSymbol type) => type.GetMembers()
		.Where(static member => !JobDiscovery.IsJobDetailsMember(member))
		.Where(member => member switch
		{
			IPropertySymbol property => property.DeclaredAccessibility == Accessibility.Public && !property.IsStatic && property.GetMethod is not null && property.Parameters.Length == 0,
			IFieldSymbol field => field.DeclaredAccessibility == Accessibility.Public && !field.IsStatic && !field.IsImplicitlyDeclared,
			_ => false,
		})
		.ToImmutableArray();

	private static IMethodSymbol? GetConstructor(INamedTypeSymbol type, ImmutableArray<ISymbol> members) => type.InstanceConstructors
		.Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public)
		.Where(constructor => constructor.Parameters.All(parameter => members.Any(member => string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))))
		.OrderByDescending(constructor => constructor.Parameters.Length)
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
		if (type is IArrayTypeSymbol array && array.ElementType.SpecialType == SpecialType.System_Byte)
			return "ByteArrayConverter";
		return type.SpecialType switch
		{
			SpecialType.System_Boolean => "BooleanConverter",
			SpecialType.System_Byte => "ByteConverter",
			SpecialType.System_SByte => "SByteConverter",
			SpecialType.System_Int16 => "Int16Converter",
			SpecialType.System_UInt16 => "UInt16Converter",
			SpecialType.System_Int32 => "Int32Converter",
			SpecialType.System_UInt32 => "UInt32Converter",
			SpecialType.System_Int64 => "Int64Converter",
			SpecialType.System_UInt64 => "UInt64Converter",
			SpecialType.System_Char => "CharConverter",
			SpecialType.System_Single => "SingleConverter",
			SpecialType.System_Double => "DoubleConverter",
			SpecialType.System_Decimal => "DecimalConverter",
			SpecialType.System_String => "StringConverter",
			SpecialType.System_DateTime => "DateTimeConverter",
			SpecialType.System_Object => "ObjectConverter",
			_ => type.ToDisplayString() switch
			{
				"System.Guid" => "GuidConverter",
				"System.DateTimeOffset" => "DateTimeOffsetConverter",
				"System.TimeSpan" => "TimeSpanConverter",
				"System.DateOnly" => "DateOnlyConverter",
				"System.TimeOnly" => "TimeOnlyConverter",
				"System.Uri" => "UriConverter",
				"System.Version" => "VersionConverter",
				_ => null,
			},
		};
	}

	private static bool UsesConfiguredConverter(ITypeSymbol type) =>
		type.ContainingNamespace?.ToDisplayString().StartsWith("NodaTime", StringComparison.Ordinal) == true;

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
	public required string? ConverterName { get; init; }
	public required bool IsEnum { get; init; }
	public required bool UsesConfiguredConverter { get; init; }
	public required string? ElementTypeName { get; init; }
	public required JsonObjectRenderModel? ObjectInfo { get; init; }
}

internal sealed record JsonObjectRenderModel
{
	public required bool HasParameterlessCreator { get; init; }
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
