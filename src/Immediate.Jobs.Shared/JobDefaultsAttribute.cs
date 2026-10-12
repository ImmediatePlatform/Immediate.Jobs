using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Supplies defaults for generated jobs declared in this assembly. Explicit job settings override these values.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class JobDefaultsAttribute : Attribute
{
	/// <summary>
	/// 	The tags inherited by jobs that omit a tag list or supply a null or empty list.
	/// </summary>
	[NotNull]
	public string[]? Tags { get; init => field = [.. JobTags.Normalize(value)]; } = [JobTags.Default];

	/// <summary>
	/// 	The default time-zone identifier. A null value uses UTC.
	/// </summary>
	public string? TimeZone { get; init; }

	/// <summary>
	/// 	The default total execution attempts, including the first attempt.
	/// </summary>
	public int MaxAttempts { get; init; } = 3;

	/// <summary>
	/// 	The default execution timeout as a TimeSpan string, or null for no timeout.
	/// </summary>
	public string? Timeout { get; init; }
}
