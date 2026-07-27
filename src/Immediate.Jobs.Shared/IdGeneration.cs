namespace Immediate.Jobs.Shared;

/// <summary>The kind of durable identifier being created.</summary>
public enum IdKind
{
	/// <summary>An individual job invocation.</summary>
	Job,

	/// <summary>An atomic job batch.</summary>
	Batch,
}

/// <summary>Creates durable identifiers for jobs and batches.</summary>
public interface IIdGenerator
{
	/// <summary>Creates a new identifier of the requested kind.</summary>
	string CreateId(IdKind kind);
}

internal sealed class GuidIdGenerator : IIdGenerator
{
	public static readonly GuidIdGenerator Instance = new();

	public string CreateId(IdKind kind) => Guid.NewGuid().ToString("N");
}
