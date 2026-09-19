using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	The kind of durable identifier being created.
/// </summary>
public enum IdKind
{
	/// <summary>
	/// 	An individual job invocation.
	/// </summary>
	Job,

	/// <summary>
	/// 	An atomic job batch.
	/// </summary>
	Batch,
}

/// <summary>
/// 	Creates durable identifiers for jobs and batches.
/// </summary>
public interface IIdGenerator
{
	/// <summary>
	/// 	Creates a new identifier of the requested kind.
	/// </summary>
	/// <param name="kind">
	/// 	The kind of durable identifier to create.
	/// </param>
	/// <returns>
	/// 	A new durable identifier.
	/// </returns>
	string CreateId(IdKind kind);
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by MSDI")]
internal sealed class GuidIdGenerator : IIdGenerator
{
	public string CreateId(IdKind kind) => Guid.NewGuid().ToString("N");
}
