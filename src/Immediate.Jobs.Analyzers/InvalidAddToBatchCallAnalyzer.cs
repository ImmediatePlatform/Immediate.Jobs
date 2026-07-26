using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidAddToBatchCallAnalyzer : DiagnosticAnalyzer
{
	public static readonly DiagnosticDescriptor DetachedJobCannotBeAddedToBatch =
		new(
			id: DiagnosticIds.IJOB0020DetachedJobCannotBeAddedToBatch,
			title: "Detached work cannot be added to a batch",
			messageFormat: "AddToBatchAsync(JobDetails, ...) cannot use ContinuationOptions.Detached; use ScheduleAfter for detached work",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description: "AddToBatchAsync(JobDetails, ...) cannot use ContinuationOptions.Detached; use ScheduleAfter for detached work."
		);

	/// <inheritdoc />
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			DetachedJobCannotBeAddedToBatch,
		]);

	/// <inheritdoc />
	public override void Initialize(AnalysisContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();
		context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
	}

	private static void AnalyzeInvocation(OperationAnalysisContext context)
	{
		var invocation = (IInvocationOperation)context.Operation;

		if (invocation is not
			{
				TargetMethod.Name: "AddToBatchAsync",
				Arguments: { Length: >= 3 } arguments,
			}
		)
		{
			return;
		}

		if (!arguments.Any(a => a is { Parameter: { Name: "current", Type.IsJobDetails: true } }))
			return;

		if (arguments.FirstOrDefault(a => a is
			{
				Parameter:
				{
					Type.IsContinuationOptions: true,
					Name: "options",
				},
				IsImplicit: false,
				Value: IFieldReferenceOperation
				{
					Field:
					{
						Name: "Detached",
						ContainingType.IsContinuationOptions: true,
					},
				},
			}) is not { } argument)
		{
			return;
		}

		context.ReportDiagnostic(Diagnostic.Create(
			DetachedJobCannotBeAddedToBatch,
			argument.Syntax.GetLocation()
		));
	}
}
