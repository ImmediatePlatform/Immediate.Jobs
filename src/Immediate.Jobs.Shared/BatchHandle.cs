namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An opaque reference to a committed atomic batch.
/// </summary>
public sealed record BatchHandle
{
	/// <summary>
	/// 	Creates a handle for an existing batch identifier.
	/// </summary>
	/// <param name="id">
	/// 	The opaque batch identifier.
	/// </param>
	public BatchHandle(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		Id = id;
	}

	/// <summary>
	/// 	The opaque batch identifier.
	/// </summary>
	public string Id { get; }

	/// <summary>Creates a handle from an existing batch identifier.</summary>
	public static implicit operator BatchHandle(string id) => new(id);

	/// <summary>Returns the batch identifier represented by a handle.</summary>
	public static implicit operator string(BatchHandle handle) => handle.Id;
}
