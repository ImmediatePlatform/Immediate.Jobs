using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Immediate.Jobs;

internal static class AttributeDataExtensions
{
	extension(ImmutableArray<AttributeData> attributes)
	{
		public AttributeData? JobAttribute =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsJobAttribute);

		public AttributeData? QueueAttribute =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsQueueDefinitionAttribute);

		public AttributeData? HandlerAttribute =>
			attributes.FirstOrDefault(x => x.AttributeClass.IsHandlerAttribute);
	}

	extension(AttributeData attribute)
	{
		public string GetJobName(string className) =>
			attribute.NamedArguments.GetStringValue("Name") ?? className.AsJobName();

		public string GetQueueName(string className) =>
			attribute.NamedArguments.GetStringValue("Name") ?? className.AsQueueName();

		public Location? Location => attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
	}
}
