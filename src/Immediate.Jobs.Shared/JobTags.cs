using System.ComponentModel;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Shared normalization and matching rules for job and server tags.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class JobTags
{
	/// <summary>
	/// 	The tag assigned when no tags are configured.
	/// </summary>
	public const string Default = "default";

	/// <summary>
	/// 	Copies tags, removes ordinal duplicates, and supplies the default for an omitted or empty list.
	/// </summary>
	/// <param name="tags">
	/// 	The configured tags, or <see langword="null"/> for the default.
	/// </param>
	/// <returns>
	/// 	A nonempty, read-only list of tags.
	/// </returns>
	/// <exception cref="ArgumentException">
	/// 	A configured tag is null, empty, or whitespace.
	/// </exception>
	public static IReadOnlyList<string> Normalize(IEnumerable<string>? tags)
	{
		var normalized = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		if (tags is not null)
		{
			foreach (var tag in tags)
			{
				ArgumentException.ThrowIfNullOrWhiteSpace(tag, nameof(tags));
				if (seen.Add(tag))
					normalized.Add(tag);
			}
		}

		if (normalized.Count == 0)
			normalized.Add(Default);
		return normalized.AsReadOnly();
	}

	/// <summary>
	/// 	Tests whether two effective tag lists share an ordinal, case-sensitive tag.
	/// </summary>
	/// <param name="jobTags">
	/// 	The normalized job tags.
	/// </param>
	/// <param name="serverTags">
	/// 	The normalized server tags.
	/// </param>
	/// <returns>
	/// 	Whether the server can manage and process the definition.
	/// </returns>
	public static bool Intersect(IReadOnlyList<string> jobTags, IReadOnlyList<string> serverTags)
	{
		ArgumentNullException.ThrowIfNull(jobTags);
		ArgumentNullException.ThrowIfNull(serverTags);
		return jobTags.Any(tag => serverTags.Contains(tag, StringComparer.Ordinal));
	}
}
