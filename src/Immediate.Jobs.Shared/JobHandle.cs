namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An opaque reference to a durable job invocation.
/// </summary>
public readonly struct JobHandle : IEquatable<JobHandle>
{
	/// <summary>
	/// 	Creates a handle for an existing invocation identifier.
	/// </summary>
	/// <param name="id">
	/// 	The opaque invocation identifier.
	/// </param>
	public JobHandle(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		Id = id;
	}

	/// <summary>
	/// 	The opaque invocation identifier.
	/// </summary>
	public string Id { get; }

	internal Batch? Batch { get; init; }

	/// <inheritdoc />
	public bool Equals(JobHandle other) => string.Equals(Id, other.Id, StringComparison.Ordinal);

	/// <inheritdoc />
	public override bool Equals(object? obj) => obj is JobHandle other && Equals(other);

	/// <inheritdoc />
	public override int GetHashCode() => Id is null ? 0 : StringComparer.Ordinal.GetHashCode(Id);

	/// <summary>
	/// 	Compares two handles by their opaque invocation identifier.
	/// </summary>
	/// <param name="left">
	/// 	The first handle to compare.
	/// </param>
	/// <param name="right">
	/// 	The second handle to compare.
	/// </param>
	/// <returns>
	///		<see langword="true"/> when the handles have the same invocation identifier; otherwise, <see langword="false"/>.
	/// </returns>
	public static bool operator ==(JobHandle left, JobHandle right) => left.Equals(right);

	/// <summary>
	/// 	Compares two handles by their opaque invocation identifier.
	/// </summary>
	/// <param name="left">
	/// 	The first handle to compare.
	/// </param>
	/// <param name="right">
	/// 	The second handle to compare.
	/// </param>
	/// <returns>
	///		<see langword="true"/> when the handles have different invocation identifiers; otherwise, <see langword="false"/>.
	/// </returns>
	public static bool operator !=(JobHandle left, JobHandle right) => !left.Equals(right);

	/// <inheritdoc />
	public override string ToString() => Id ?? string.Empty;
}
