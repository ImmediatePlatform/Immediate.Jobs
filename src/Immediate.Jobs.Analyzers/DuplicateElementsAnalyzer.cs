using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuplicateElementsAnalyzer : DiagnosticAnalyzer
{
	public static readonly DiagnosticDescriptor DuplicateJobName =
		new(
			id: DiagnosticIds.IJOB0002DuplicateJobName,
			title: "Job Names must be unique",
			messageFormat: "Job Name `{0}` has been specified multiple times: {1}",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Duplicate job names lead to conflict in identifying jobs; job names must be unique.",
			customTags: [WellKnownDiagnosticTags.CompilationEnd, WellKnownDiagnosticTags.NotConfigurable]
		);

	public static readonly DiagnosticDescriptor DuplicateQueueName =
		new(
			id: DiagnosticIds.IJOB0003DuplicateQueueName,
			title: "Queue Names must be unique",
			messageFormat: "Queue Name `{0}` has been specified multiple times: {1}",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Duplicate queue names lead to conflict in identifying queues; queue names must be unique.",
			customTags: [WellKnownDiagnosticTags.CompilationEnd, WellKnownDiagnosticTags.NotConfigurable]
		);

	public static readonly DiagnosticDescriptor UnusedQueueDefinition =
		new(
			id: DiagnosticIds.IJOB0016UnusedQueueDefinition,
			title: "Queue definition is not used",
			messageFormat: "Queue `{0}` is defined but no jobs currently use it",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description: "Queue definitions with no jobs attached may indicate unused configuration.",
			customTags: [WellKnownDiagnosticTags.CompilationEnd]
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			DuplicateJobName,
			DuplicateQueueName,
			UnusedQueueDefinition,
		]);

	private sealed record Job
	{
		public required string ClassName { get; init; }
		public required string JobName { get; init; }
		public required string QueueName { get; init; }
		public required Location? Location { get; init; }
	}

	private sealed record Queue
	{
		public required string ClassName { get; init; }
		public required string QueueName { get; init; }
		public required Location? Location { get; init; }
	}

	public override void Initialize(AnalysisContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();

		context.RegisterCompilationStartAction(context =>
		{
			var jobs = new List<Job>();
			var queues = new List<Queue>();

			var @lock = new Lock();

			context.RegisterSymbolAction(
				context =>
				{
					var attributes = context.Symbol.GetAttributes();

					GatherJobDefinition(context, attributes, jobs, @lock);
					GatherQueueDefinition(context, attributes, queues, @lock);
				},
				SymbolKind.NamedType
			);

			context.RegisterCompilationEndAction(
				context =>
				{
					AnalyzeDuplicateJobNames(context, jobs);
					AnalyzeDuplicateQueueNames(context, queues);
					AnalyzeUnusedQueues(context, jobs, queues);
				}
			);
		});
	}

	private static void GatherJobDefinition(
		SymbolAnalysisContext context,
		ImmutableArray<AttributeData> attributes,
		List<Job> jobs,
		Lock @lock
	)
	{
		if (attributes.JobAttribute is { } jobAttribute)
		{
			lock (@lock)
			{
				jobs.Add(
					new()
					{
						ClassName = context.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
						JobName = jobAttribute.GetJobName(className: context.Symbol.Name),
						QueueName = context.Symbol.GetAttributes() switch
						{
							{ UsesQueueAttribute.AttributeClass.TypeArguments: [{ } queueType] }
								when queueType.GetAttributes() is { QueueDefinitionAttribute: { } queueAttribute } =>
								queueAttribute.GetQueueName(queueType.Name),
							_ => "default",
						},
						Location = context.Symbol.Locations.FirstOrDefault(),
					}
				);
			}
		}
	}

	private static void GatherQueueDefinition(
		SymbolAnalysisContext context,
		ImmutableArray<AttributeData> attributes,
		List<Queue> queues,
		Lock @lock
	)
	{
		if (attributes.QueueDefinitionAttribute is { } queueAttribute)
		{
			lock (@lock)
			{
				queues.Add(
					new()
					{
						ClassName = context.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
						QueueName = queueAttribute.GetQueueName(className: context.Symbol.Name),
						Location = context.Symbol.Locations.FirstOrDefault(),
					}
				);
			}
		}
	}

	private static void AnalyzeDuplicateJobNames(CompilationAnalysisContext context, List<Job> jobs)
	{
		foreach (var jobGroup in jobs.GroupBy(x => x.JobName, StringComparer.Ordinal).Where(g => g.Skip(1).Any()))
		{
			var classes = string.Join(", ", jobGroup.Select(l => l.ClassName));

			foreach (var location in jobGroup)
			{
				context.ReportDiagnostic(
					Diagnostic.Create(
						DuplicateJobName,
						location.Location,
						jobGroup.Key,
						classes
					)
				);
			}
		}
	}

	private static void AnalyzeDuplicateQueueNames(CompilationAnalysisContext context, List<Queue> queues)
	{
		foreach (var queueGroup in queues.GroupBy(x => x.QueueName, StringComparer.Ordinal).Where(g => g.Skip(1).Any()))
		{
			var classes = string.Join(", ", queueGroup.Select(l => l.ClassName));

			foreach (var location in queueGroup)
			{
				context.ReportDiagnostic(
					Diagnostic.Create(
						DuplicateQueueName,
						location.Location,
						queueGroup.Key,
						classes
					)
				);
			}
		}
	}

	private static void AnalyzeUnusedQueues(CompilationAnalysisContext context, List<Job> jobs, List<Queue> queues)
	{
		var usedQueues = jobs.Select(j => j.QueueName).ToHashSet(StringComparer.Ordinal);

		foreach (var queue in queues)
		{
			if (usedQueues.Contains(queue.QueueName))
				continue;

			context.ReportDiagnostic(
				Diagnostic.Create(
					UnusedQueueDefinition,
					queue.Location,
					queue.ClassName
				)
			);
		}
	}
}
