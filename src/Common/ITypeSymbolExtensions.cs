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

		public bool IsNoPayload =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "NoPayload",
				ContainingNamespace.IsImmediateJobsShared: true,
			};

		public bool IsHandlerAttribute =>
			typeSymbol is INamedTypeSymbol
			{
				Arity: 0,
				Name: "HandlerAttribute",
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
