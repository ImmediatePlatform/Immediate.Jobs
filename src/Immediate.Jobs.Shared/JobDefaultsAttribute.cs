using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Supplies defaults for generated jobs declared in this assembly. Explicit job settings override these values.
/// </summary>
/// <remarks>
/// 	Only explicitly supplied settings become assembly defaults. Unspecified settings use the built-in job defaults.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class JobDefaultsAttribute : Attribute
{
	/// <summary>
	/// 	The routing tags inherited by jobs that omit a tag list.
	/// </summary>
	/// <remarks>
	/// 	Null or empty lists use the default tag. Explicit job tag lists, including null or empty lists, override these tags.
	/// </remarks>
	[NotNull]
	public string[]? Tags { get; init => field = [.. JobTags.Normalize(value)]; }

	/// <summary>
	/// 	The default time-zone identifier. A null value uses UTC.
	/// </summary>
	public string? TimeZone { get; init; }

	/// <summary>
	/// 	The default total execution attempts, including the first attempt.
	/// </summary>
	public int MaxAttempts { get; init; }

	/// <summary>
	/// 	The default execution timeout as a <see cref="TimeSpan"/> string, or null for no timeout.
	/// </summary>
	public string? Timeout { get; init; }
}
