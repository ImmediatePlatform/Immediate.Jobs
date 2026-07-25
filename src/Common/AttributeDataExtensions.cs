using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Immediate.Jobs;

internal static class AttributeDataExtensions
{
	extension(ImmutableArray<AttributeData> attributes)
	{
		public AttributeData? GetJobAttribute() =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsJobAttribute);

		public AttributeData? GetQueueAttribute() =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsQueueDefinitionAttribute);

		public AttributeData? GetHandlerAttribute() =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsHandlerAttribute);
	}

	extension(AttributeData attribute)
	{
		public string GetJobName(string className) =>
			attribute.ConstructorArguments switch
			{
				[{ } arg] when arg.GetStringValue() is { } name => name,
				_ => className.AsJobName(),
			};

		public string GetQueueName(string className) =>
			attribute.NamedArguments.GetStringValue("Name") ?? className.AsQueueName();
	}
}
