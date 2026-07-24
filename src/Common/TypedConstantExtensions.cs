using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Immediate.Jobs;

internal static class TypedConstantExtensions
{
	extension(ImmutableArray<KeyValuePair<string, TypedConstant>> arguments)
	{
		public TypedConstant? GetArgumentValue(string name)
		{
			foreach (var argument in arguments)
			{
				if (string.Equals(name, argument.Key, StringComparison.Ordinal))
					return argument.Value;
			}

			return null;
		}

		public IFieldSymbol? GetEnumValue(string name) =>
			arguments.GetArgumentValue(name)?.GetEnumValue();

		public string? GetStringValue(string name) =>
			arguments.GetArgumentValue(name)?.GetStringValue();

		public int? GetIntValue(string name) =>
			arguments.GetArgumentValue(name)?.GetIntValue();

		public int GetIntValue(string name, int defaultValue) =>
			arguments.GetArgumentValue(name)?.GetIntValue() ?? defaultValue;

		public string? GetStringArray(string name) =>
			arguments.GetArgumentValue(name)?.GetStringArray();
	}

	extension(TypedConstant constant)
	{
		public IFieldSymbol? GetEnumValue()
		{
			if (constant is not
				{
					Kind: TypedConstantKind.Enum,
					Type: { } type,
					Value: int value
				})
			{
				return null;
			}

			return type.GetMembers()
				.OfType<IFieldSymbol>()
				.FirstOrDefault(ifs => ifs.ConstantValue is int cv && cv == value);
		}

		public string? GetStringValue() =>
			constant switch
			{
				{ Kind: TypedConstantKind.Primitive, Value: string str } => str,
				_ => null,
			};

		public int? GetIntValue() =>
			constant switch
			{
				{ Kind: TypedConstantKind.Primitive, Value: int i } => i,
				_ => null,
			};

		public string? GetStringArray()
		{
			if (constant.Kind != TypedConstantKind.Array)
				return null;

			return string.Join(
				", ",
				constant.Values
					.Select(tc => tc.ToCSharpString())
					.OrderBy(x => x, StringComparer.Ordinal)
			);
		}

		public INamedTypeSymbol? ArgumentType =>
			constant switch
			{
				{ Kind: TypedConstantKind.Type, Value: INamedTypeSymbol type } => type,
				_ => null,
			};
	}
}
