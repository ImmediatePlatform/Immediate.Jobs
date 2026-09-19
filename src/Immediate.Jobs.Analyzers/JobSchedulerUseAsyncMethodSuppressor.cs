using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JobSchedulerUseAsyncMethodSuppressor : DiagnosticSuppressor
{
	private const string JobSchedulerMetadataName = "Immediate.Jobs.Shared.JobScheduler`1";

	public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions =>
		ImmutableArray.Create([
			new SuppressionDescriptor(
				id: "CallAsyncMethodsSuppressor",
				suppressedDiagnosticId: "CA1849",
				justification: "Synchronous JobScheduler methods build a batch and do not perform synchronous I/O."
			),
		]);

	public override void ReportSuppressions(SuppressionAnalysisContext context)
	{
		var token = context.CancellationToken;
		token.ThrowIfCancellationRequested();

		var jobScheduler = context.Compilation.GetTypeByMetadataName(JobSchedulerMetadataName);
		if (jobScheduler is null)
			return;

		foreach (var diagnostic in context.ReportedDiagnostics)
		{
			token.ThrowIfCancellationRequested();

			if (!diagnostic.Location.IsInSource)
				continue;

			var syntaxTree = diagnostic.Location.SourceTree;
			if (syntaxTree is null)
				continue;

			var root = syntaxTree.GetRoot(context.CancellationToken);
			var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

			var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
			if (invocation is null)
				continue;

			var semanticModel = context.GetSemanticModel(syntaxTree);
			var method = semanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;

			if (
				method is not null
				&& SymbolEqualityComparer.Default.Equals(method.ContainingType.OriginalDefinition, jobScheduler)
			)
			{
				context.ReportSuppression(Suppression.Create(SupportedSuppressions[0], diagnostic));
			}
		}
	}
}
