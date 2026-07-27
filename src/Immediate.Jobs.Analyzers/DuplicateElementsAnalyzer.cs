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
			category: "ImmediateQueues",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Duplicate queue names lead to conflict in identifying queues; queue names must be unique.",
			customTags: [WellKnownDiagnosticTags.CompilationEnd, WellKnownDiagnosticTags.NotConfigurable]
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			DuplicateJobName,
			DuplicateQueueName,
		]);

	private sealed record Job
	{
		public required string ClassName { get; init; }
		public required string JobName { get; init; }
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

					if (attributes.JobAttribute is { } jobAttribute)
					{
						lock (@lock)
						{
							jobs.Add(
								new()
								{
									ClassName = context.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
									JobName = jobAttribute.GetJobName(className: context.Symbol.Name),
									Location = context.Symbol.Locations.FirstOrDefault(),
								}
							);
						}
					}

					if (attributes.QueueAttribute is { } queueAttribute)
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
				},
				SymbolKind.NamedType
			);

			context.RegisterCompilationEndAction(
				context =>
				{
					foreach (var jobGroup in jobs.GroupBy(x => x.JobName).Where(g => g.Skip(1).Any()))
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

					foreach (var queueGroup in queues.GroupBy(x => x.QueueName).Where(g => g.Skip(1).Any()))
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
			);
		});
	}
}
