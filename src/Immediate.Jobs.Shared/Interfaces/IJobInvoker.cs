namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	The generated invocation boundary used by the worker.
/// </summary>
public interface IJobInvoker
{
	/// <summary>
	/// 	Invokes the job directly from a scoped service provider.
	/// </summary>
	/// <param name="scopedServices">
	/// 	The services for the current job execution scope.
	/// </param>
	/// <param name="execution">
	/// 	The metadata for the current execution.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous invocation.
	/// </returns>
	ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution);
}
