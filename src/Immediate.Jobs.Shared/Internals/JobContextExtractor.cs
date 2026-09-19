using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Captures ambient state while enqueueing and restores it in a job execution scope.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class JobContextExtractor
{
	[SuppressMessage(
		"Design",
		"MA0017:Abstract types should not have public or internal constructors",
		Justification = "intentional to create a particular framework for consumption with attributes"
	)]
	internal JobContextExtractor() { }

	/// <summary>
	/// 	The stable key used for this context slice in persisted job envelopes.
	/// </summary>
	public abstract string Key { get; }
}
