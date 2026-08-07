using System.Globalization;

#pragma warning disable IDE0130
namespace Immediate.Jobs.Testing;

internal static class ConformanceAssert
{
	internal static void True(bool condition, string caseName, string invariant, string? context = null)
	{
		if (!condition)
			throw new JobTestAssertionException(FormatFailure(caseName, invariant, context));
	}

	internal static void False(bool condition, string caseName, string invariant, string? context = null) =>
		True(!condition, caseName, invariant, context);

	internal static void Equal<T>(
		T expected,
		T actual,
		string caseName,
		string invariant,
		string? context = null
	)
	{
		if (EqualityComparer<T>.Default.Equals(expected, actual))
			return;

		throw new JobTestAssertionException(
			FormatFailure(caseName, invariant, context, expected, actual)
		);
	}

	internal static void SequenceEqual<T>(
		IEnumerable<T> expected,
		IEnumerable<T> actual,
		string caseName,
		string invariant,
		string? context = null,
		IEqualityComparer<T>? comparer = null
	)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentNullException.ThrowIfNull(actual);

		var expectedValues = expected.ToArray();
		var actualValues = actual.ToArray();
		if (expectedValues.SequenceEqual(actualValues, comparer))
			return;

		throw new JobTestAssertionException(
			FormatFailure(
				caseName,
				invariant,
				context,
				$"[{string.Join(", ", expectedValues.Select(FormatValue))}]",
				$"[{string.Join(", ", actualValues.Select(FormatValue))}]"
			)
		);
	}

	internal static void Null(object? actual, string caseName, string invariant, string? context = null)
	{
		if (actual is null)
			return;

		throw new JobTestAssertionException(FormatFailure(caseName, invariant, context, expected: null, actual));
	}

	internal static T NotNull<T>(T? actual, string caseName, string invariant, string? context = null)
		where T : class
	{
		if (actual is not null)
			return actual;

		throw new JobTestAssertionException(FormatFailure(caseName, invariant, context, "non-null", actual: null));
	}

	internal static T IsAssignableFrom<T>(object? actual, string caseName, string invariant, string? context = null)
	{
		if (actual is T typed)
			return typed;

		throw new JobTestAssertionException(
			FormatFailure(caseName, invariant, context, typeof(T).FullName, actual?.GetType().FullName)
		);
	}

	internal static async ValueTask<TException> ThrowsAsync<TException>(
		Func<ValueTask> action,
		string caseName,
		string invariant,
		string? context = null
	)
		where TException : Exception
	{
		ArgumentNullException.ThrowIfNull(action);

		try
		{
			await action().ConfigureAwait(false);
		}
		catch (TException exception)
		{
			return exception;
		}
		catch (Exception exception)
		{
			throw new JobTestAssertionException(
				FormatFailure(caseName, invariant, context, typeof(TException).FullName, exception.GetType().FullName),
				exception
			);
		}

		throw new JobTestAssertionException(
			FormatFailure(caseName, invariant, context, typeof(TException).FullName, "no exception")
		);
	}

#pragma warning disable CA1031 // This assertion deliberately accepts any non-cancellation provider failure.
	internal static async ValueTask<Exception> ThrowsAnyAsync(
		Func<ValueTask> action,
		string caseName,
		string invariant,
		string? context = null
	)
	{
		ArgumentNullException.ThrowIfNull(action);

		try
		{
			await action().ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			return exception;
		}

		throw new JobTestAssertionException(
			FormatFailure(caseName, invariant, context, "an exception", "no exception")
		);
	}
#pragma warning restore CA1031

	internal static async ValueTask<T> EventuallyAsync<T>(
		Func<ValueTask<T>> observe,
		Func<T, bool> condition,
		int maximumAttempts,
		string caseName,
		string invariant,
		string? context = null
	)
	{
		ArgumentNullException.ThrowIfNull(observe);
		ArgumentNullException.ThrowIfNull(condition);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);

		T actual = default!;
		for (var attempt = 0; attempt < maximumAttempts; attempt++)
		{
			actual = await observe().ConfigureAwait(false);
			if (condition(actual))
				return actual;
		}

		throw new JobTestAssertionException(
			FormatFailure(
				caseName,
				invariant,
				context,
				$"condition within {maximumAttempts.ToString(CultureInfo.InvariantCulture)} observations",
				actual
			)
		);
	}

	internal static string FormatFailure(
		string caseName,
		string invariant,
		string? context = null,
		object? expected = null,
		object? actual = null
	)
	{
		var message = $"[{caseName}] Conformance failure: {invariant}.";
		if (!string.IsNullOrWhiteSpace(context))
			message += $" Context: {context}.";
		if (expected is not null || actual is not null)
			message += $" Expected: {FormatValue(expected)}. Actual: {FormatValue(actual)}.";
		return message;
	}

	private static string FormatValue<T>(T value) =>
		value switch
		{
			null => "<null>",
			IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
			_ => value.ToString() ?? "<null>",
		};
}
