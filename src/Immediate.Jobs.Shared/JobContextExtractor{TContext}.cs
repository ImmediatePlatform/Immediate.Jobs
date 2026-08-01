using Immediate.Jobs.Shared.Internals;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Captures ambient state while enqueueing and restores it in a job execution scope.
/// </summary>
/// <typeparam name="TContext">
/// 	The durable, serializable context value.
/// </typeparam>
public abstract class JobContextExtractor<TContext> : JobContextExtractor
{
	/// <summary>
	/// Creates a new instance of <see cref="JobContextExtractor"/>.
	/// </summary>
	protected JobContextExtractor() : base() { }

	/// <summary>
	/// 	Captures context from the enqueueing scope, or returns no value when none is available.
	/// </summary>
	/// <returns>
	/// 	The captured context, or no value when none is available.
	/// </returns>
	public abstract TContext? Capture();

	/// <summary>
	/// 	Restores captured context into services in the job execution scope.
	/// </summary>
	/// <param name="context">
	/// 	The captured context to restore.
	/// </param>
	public abstract void Restore(TContext context);
}
