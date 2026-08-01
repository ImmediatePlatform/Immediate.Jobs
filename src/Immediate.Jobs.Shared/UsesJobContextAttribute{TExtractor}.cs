using Immediate.Jobs.Shared.Internals;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Applies a context extractor to a generated job or reusable job marker attribute.
/// </summary>
/// <typeparam name="TExtractor">
/// 	The extractor type.
/// </typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesJobContextAttribute<TExtractor> : Attribute
	where TExtractor : JobContextExtractor;
