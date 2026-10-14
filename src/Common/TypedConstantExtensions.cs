using System.Collections.Immutable;
using System.Globalization;
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

		public string? GetValidEnumValue(string name) =>
			arguments.GetArgumentValue(name) switch
			{
				{ } value => value.GetEnumValue()?.Name,
				_ => null,
			};

		public string? GetValidEnumValue(string name, string defaultValue) =>
			arguments.GetArgumentValue(name) switch
			{
				{ } value => value.GetEnumValue()?.Name,
				_ => defaultValue,
			};

		public AcquisitionWindow? GetValidAcquisitionWindow(string windowName)
		{
			return (
				arguments.GetIntValue(windowName + "Max"),
				arguments.GetStringValue(windowName + "Period")
			) switch
			{
				(null, null) => new(Max: null, Period: null),

				({ } maximum, { } periodStr)
					when maximum > 0
					&& TimeSpan.TryParse(periodStr, CultureInfo.InvariantCulture, out var period)
					&& period > TimeSpan.Zero =>
					new(maximum, period),

				_ => null,
			};
		}

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
					Value: int value,
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

	extension(TypedConstant? constant)
	{
		public string? GetTags()
		{
			if (constant is not { IsNull: false, Kind: TypedConstantKind.Array, Values: var values } av)
				return "\"default\"";

			if (values is [])
				return "\"default\"";

			if (values.Any(tag => string.IsNullOrWhiteSpace(tag.Value as string)))
				return null;

			return av.GetStringArray();
		}
	}
}
