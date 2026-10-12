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
		public string GetJobName(string className) =>
			attribute.NamedArguments.GetStringValue("Name") ?? className.AsJobName();

		public string GetQueueName(string className) =>
			attribute.NamedArguments.GetStringValue("Name") ?? className.AsQueueName();

		public Location? Location => attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();

		public IEnumerable<string> GetJobDefaultsErrors()
		{
			var arguments = attribute.NamedArguments;

			if (arguments.GetArgumentValue("Tags").GetTags() is null)
				yield return "Tags must not contain null, empty, or whitespace entries.";

			if (arguments.GetIntValue("MaxAttempts", 3) < 1)
				yield return "MaxAttempts must be at least one.";

			if (arguments.GetStringValue("Timeout") is { } timeout
				&& (!TimeSpanSerializer.TryParse(timeout, out var duration) || duration <= TimeSpan.Zero))
			{
				yield return "Timeout must be a positive TimeSpan or ISO8601 duration.";
			}

			if (arguments.GetStringValue("TimeZone") is { } timeZone
				&& timeZone.IsWhiteSpace())
			{
				yield return "Time Zone is invalid.";
			}
		}

	}
}
