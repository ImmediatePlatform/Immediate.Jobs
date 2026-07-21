using Microsoft.CodeAnalysis;

namespace Immediate.Jobs;

internal static class Utility
{
	public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> values)
		where T : class => values.Where(static value => value is not null)!;

	public static IncrementalValuesProvider<T> WhereNotNull<T>(this IncrementalValuesProvider<T?> values)
		where T : class => values.Where(static value => value is not null)!;
}
