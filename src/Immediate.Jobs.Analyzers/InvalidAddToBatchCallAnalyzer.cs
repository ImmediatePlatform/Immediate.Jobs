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
			id: DiagnosticIds.IJOB0015DetachedJobCannotBeAddedToBatch,
			title: "Detached work cannot be added to a batch",
			messageFormat: "ScheduleAfter(payload, JobDetails, ...) cannot use ContinuationOptions.Detached; use ScheduleAfter for detached work",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description: "ScheduleAfter(payload, JobDetails, ...) cannot use ContinuationOptions.Detached; use ScheduleAfter for detached work."
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
				TargetMethod.Name: "EnqueueAsync" or "ScheduleAsync",
				Arguments:
				[
				_,
				{ Parameter.Type.IsJobDetails: true },
				..,
				{
					Parameter.Type.IsContinuationOptions: true,
					IsImplicit: false,
				} argument,
				{ Parameter.Type.IsCancellationToken: true },
				],
			}
		)
		{
			return;
		}

		if (argument is not
			{
				Value: IFieldReferenceOperation
				{
					Field:
					{
						Name: "Detached",
						ContainingType.IsContinuationOptions: true,
					},
				},
			})
		{
			return;
		}

		context.ReportDiagnostic(Diagnostic.Create(
			DetachedJobCannotBeAddedToBatch,
			argument.Syntax.GetLocation()
		));
	}
}
