using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Immediate.Jobs;

internal static class ITypeSymbolExtensions
{
	extension([NotNullWhen(true)] ITypeSymbol? typeSymbol)
	{
		public bool IsJobAttribute =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "JobAttribute",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsJobDetails =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "JobDetails",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsContinuationOptions =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "ContinuationOptions",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsQueueDefinitionAttribute =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "QueueDefinitionAttribute",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsUsesQueueAttribute =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 1,
				Name: "UsesQueueAttribute",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsUsesJobContextAttribute =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 1,
				Name: "UsesJobContextAttribute",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsIJobRequest =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "IJobRequest",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsIJobContextExtractor1 =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 1,
				Name: "IJobContextExtractor",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsEmptyJobRequest =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "EmptyJobRequest",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsHandlerAttribute =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "HandlerAttribute",
				ContainingNamespace.IsImmediateHandlersShared: true,
			};

		public bool IsImmediateAssemblyIdentifierAttribute =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "ImmediateAssemblyIdentifierAttribute",
				ContainingNamespace.IsImmediateHandlersShared: true,
			};

		public bool IsCancellationToken =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "CancellationToken",
				ContainingNamespace.IsSystemThreading: true,
			};

		public bool IsValueTask =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "ValueTask",
				ContainingNamespace.IsSystemThreadingTasks: true,
			};

		public bool ImplementsJobRequest => typeSymbol is INamedTypeSymbol { ImplementsJobRequest: true };
	}

	extension(INamedTypeSymbol namedTypeSymbol)
	{
		public IMethodSymbol? GetValidHandleMethod()
		{
			if (namedTypeSymbol
					.GetMembers()
					.OfType<IMethodSymbol>()
					.Where(m => m.Name is "Handle" or "HandleAsync")
					.Take(2)
					.ToList() is not [var handleMethod])
			{
				return null;
			}

			// must have request type
			if (handleMethod.Parameters.Length is 0)
				return null;

			return handleMethod;
		}

		public bool ImplementsJobRequest => namedTypeSymbol.AllInterfaces.Any(static i => i.IsIJobRequest);
	}

	extension(INamespaceSymbol namespaceSymbol)
	{
		public bool IsImmediateJobsShared =>
			namespaceSymbol is
			{
				Name: "Shared",
				ContainingNamespace:
				{
					Name: "Jobs",
					ContainingNamespace:
					{
						Name: "Immediate",
						ContainingNamespace.IsGlobalNamespace: true,
					},
				},
			};

		public bool IsImmediateHandlersShared =>
			namespaceSymbol is
			{
				Name: "Shared",
				ContainingNamespace:
				{
					Name: "Handlers",
					ContainingNamespace:
					{
						Name: "Immediate",
						ContainingNamespace.IsGlobalNamespace: true,
					},
				},
			};

		public bool IsSystemThreading =>
			namespaceSymbol is
			{
				Name: "Threading",
				ContainingNamespace:
				{
					Name: "System",
					ContainingNamespace.IsGlobalNamespace: true,
				},
			};

		public bool IsSystemThreadingTasks =>
			namespaceSymbol is
			{
				Name: "Tasks",
				ContainingNamespace:
				{
					Name: "Threading",
					ContainingNamespace:
					{
						Name: "System",
						ContainingNamespace.IsGlobalNamespace: true,
					},
				},
			};
	}
}
