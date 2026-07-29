using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Immediate.Jobs.Generators;

public sealed partial class ImmediateJobsGenerator
{
	private static JobModel? TransformJob(
		GeneratorAttributeSyntaxContext context,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var symbol = (INamedTypeSymbol)context.TargetSymbol;
		var attributes = symbol.GetAttributes();

		if (symbol.GetValidHandleMethod() is not { ReturnType.IsValueTask: true } handleMethod)
			return null;

		if (attributes.HandlerAttribute is not { } handlerAttribute)
			return null;

		var @namespace = symbol.ContainingNamespace.ToDisplayString().NullIf("<global namespace>");

		var parameterType = handleMethod.Parameters[0].Type;
		var hasPayload = !parameterType.IsEmptyJobRequest;

		var attribute = context.Attributes[0];
		var jobName = attribute.GetJobName(className: symbol.Name);

		if (!jobName.HasNameContent())
			return null;

		var arguments = attribute.NamedArguments;

		var cron = arguments.GetStringValue("Cron");
		var timeZone = arguments.GetStringValue("TimeZone") ?? "UTC";

		if (cron is not null)
		{
			if (hasPayload)
				return null;

			if (!CronValidator.TryValidate(cron, out _))
				return null;

			if (timeZone.IsWhiteSpace())
				return null;
		}

		var maxAttempts = arguments.GetIntValue("MaxAttempts", 3);
		if (maxAttempts < 1)
			return null;

		var maxConcurrency = arguments.GetIntValue("MaxConcurrency", 0);
		if (maxConcurrency < 0)
			return null;

		var backoff = arguments.GetArgumentValue("Backoff") switch
		{
			{ } av => av.GetEnumValue()?.Name,
			_ => "ExponentialJitter",
		};
		if (backoff is not { })
			return null;

		var overlapPolicy = arguments.GetArgumentValue("OverlapPolicy") switch
		{
			{ } av => av.GetEnumValue()?.Name,
			_ => "Skip",
		};
		if (overlapPolicy is not { })
			return null;

		var timeout = arguments.GetStringValue("Timeout");
		if (timeout is { } && (!TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var timeoutValue) || timeoutValue <= TimeSpan.Zero))
			return null;

		var backoffBase = arguments.GetStringValue("BackoffBase") ?? "00:00:05";
		if (!TimeSpan.TryParse(backoffBase, CultureInfo.InvariantCulture, out var backoffValue) || backoffValue <= TimeSpan.Zero)
			return null;

		if (
			attributes.UsesQueueAttribute switch
			{
				{ AttributeClass.TypeArguments: [{ } queueSymbol] }
					when queueSymbol.GetAttributes().QueueDefinitionAttribute is { } queueDefinitionAttribute =>
					(
						QueueName: queueDefinitionAttribute.GetQueueName(queueSymbol.Name).NullIf("default").NullIfWhitespace(),
						QueuePriority: queueDefinitionAttribute.NamedArguments.GetIntValue("Priority", 0),
						QueueConcurrency: queueDefinitionAttribute.NamedArguments.GetIntValue("Concurrency", 0)
					),

				{ } => default((string? QueueName, int QueuePriority, int QueueConcurrency)?),

				_ => (QueueName: "default", QueuePriority: 0, QueueConcurrency: 0),
			} is not ({ } queueName, var queuePriority, >= 0 and var queueConcurrency)
		)
		{
			return null;
		}

		if (hasPayload && !PayloadValidation.CanSerializeToJson(parameterType, reportError: null))
			return null;

		var tags = handlerAttribute?.NamedArguments.GetStringArray("Tags");

		var extractors = attributes.GetContextExtractors().ToList();

		if (extractors.Any(e => !PayloadValidation.CanSerializeToJson(e.ContextType, reportError: null)))
			return null;

		var jsonMetadataEmitter = JsonMetadataEmitter.CreateModel(
			parameterType,
			extractors.Select(e => e.ContextType)
		);

		var contexts = extractors
			.Select((use, index) => new JobContextModel
			{
				ExtractorTypeName = use.ExtractorType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				ContextTypeName = use.ContextType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				JsonPropertyName = $"Context{index}",
			})
			.ToEquatableReadOnlyList();

		return new()
		{
			Namespace = @namespace,
			ClassName = symbol.Name,
			TypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
			PayloadTypeName = parameterType.ToDisplayString(DisplayNameFormatters.FullyQualifiedWithNullableFormat),
			HasPayload = hasPayload,
			HasJobDetails = parameterType.ImplementsJobRequest,
			Name = jobName,
			QueueName = queueName,
			QueuePriority = queuePriority,
			QueueConcurrency = queueConcurrency,
			Cron = cron,
			TimeZone = timeZone,
			MaxAttempts = maxAttempts,
			Timeout = timeout,
			MaxConcurrency = maxConcurrency,
			OverlapPolicy = overlapPolicy,
			Backoff = backoff,
			BackoffBase = backoffBase,
			Tags = tags,
			Contexts = contexts,
			Json = jsonMetadataEmitter,
		};
	}

	private static QueueModel? TransformQueue(
		GeneratorAttributeSyntaxContext context,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var queueType = (INamedTypeSymbol)context.TargetSymbol;
		var attribute = context.Attributes[0];

		if (attribute.GetQueueName(queueType.Name).NullIf("default").NullIfWhitespace() is not { } name)
			return null;

		var concurrency = attribute.NamedArguments.GetIntValue("Concurrency", 0);
		if (concurrency < 0)
			return null;

		var priority = attribute.NamedArguments.GetIntValue("Priority", 0);

		return new()
		{
			Name = name,
			Priority = priority,
			Concurrency = concurrency,
		};
	}
}

file static class Extensions
{
	public static string? NullIf(this string value, string check) =>
		value.Equals(check, StringComparison.Ordinal) ? null : value;

	public static string? NullIfWhitespace(this string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value;

	public static IEnumerable<(INamedTypeSymbol ExtractorType, ITypeSymbol ContextType)> GetContextExtractors(this ImmutableArray<AttributeData> attributes)
	{
		var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

		foreach (var a in attributes)
		{
			switch (a.AttributeClass)
			{
				case
				{
					IsUsesJobContextAttribute: true,
					TypeArguments: [INamedTypeSymbol { ContextType: { } contextType } extractorType],
				}:
				{
					if (seen.Add(extractorType))
						yield return (extractorType, contextType);

					break;
				}

				case { } ac when ac.GetAttributes().Any(a => a is { AttributeClass.IsUsesJobContextAttribute: true }):
				{
					foreach (var aa in ac.GetAttributes())
					{
						if (aa.AttributeClass is not
							{
								IsUsesJobContextAttribute: true,
								TypeArguments: [INamedTypeSymbol { ContextType: { } contextType } extractorType],
							})
						{
							continue;
						}

						if (seen.Add(extractorType))
							yield return (extractorType, contextType);
					}

					break;
				}

				default:
					break;
			}
		}
	}
}
