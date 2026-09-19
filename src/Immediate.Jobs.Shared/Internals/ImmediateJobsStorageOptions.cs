using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared.Internals;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by MSDI")]
internal sealed class ImmediateJobsStorageOptions
{
	public bool Configured { get; set; }
}
