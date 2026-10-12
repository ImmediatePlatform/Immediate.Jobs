using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Immediate.Jobs;

internal static class AttributeDataExtensions
{
	extension(ImmutableArray<AttributeData> attributes)
	{
		public AttributeData? JobAttribute =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsJobAttribute);

		public AttributeData? QueueDefinitionAttribute =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsQueueDefinitionAttribute);

		public AttributeData? UsesQueueAttribute =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsUsesQueueAttribute);

		public AttributeData? HandlerAttribute =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsHandlerAttribute);
	}

	extension(AttributeData attribute)
	{
		public ImmutableArray<KeyValuePair<string, TypedConstant>> GetJobArguments(INamedTypeSymbol job)
		{
			var defaults = job.ContainingAssembly.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass.IsJobDefaultsAttribute);
			if (defaults is null)
				return attribute.NamedArguments;

			var arguments = defaults.NamedArguments.ToDictionary(static argument => argument.Key, static argument => argument.Value, StringComparer.Ordinal);
			foreach (var argument in attribute.NamedArguments)
			{
				if (string.Equals(argument.Key, "Tags", StringComparison.Ordinal)
					&& (argument.Value.IsNull || argument.Value is { Kind: TypedConstantKind.Array, Values.Length: 0 }))
					continue;

				arguments[argument.Key] = argument.Value;
			}

			return arguments.ToImmutableArray();
		}

		public string GetJobName(string className) =>
			attribute.NamedArguments.GetStringValue("Name") ?? className.AsJobName();

		public string GetQueueName(string className) =>
			attribute.NamedArguments.GetStringValue("Name") ?? className.AsQueueName();

		public Location? Location => attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
	}
}
