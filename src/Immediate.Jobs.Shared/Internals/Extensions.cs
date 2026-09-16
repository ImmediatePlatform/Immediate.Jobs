using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Internals;

internal static class ArgumentExceptionExtensions
{
	extension(ArgumentException)
	{
		[MethodImpl(MethodImplOptions.NoInlining)]
		[DoesNotReturn]
		public static void Throw(string paramName, string message) =>
			throw new ArgumentException(message: message, paramName: paramName);
	}
}

internal static class ArgumentOutOfRangeExceptionExtensions
{
	extension(ArgumentOutOfRangeException)
	{
		[MethodImpl(MethodImplOptions.NoInlining)]
		[DoesNotReturn]
		public static void Throw(string paramName, string message) =>
			throw new ArgumentOutOfRangeException(message: message, paramName: paramName);
	}
}

internal static class ActivityExtensions
{
	extension(Activity? activity)
	{
		public void Deconstruct(out string? parent, out string? state) =>
			(parent, state) = (activity?.Id, activity?.TraceStateString);
	}
}

/// <summary>
///		Collection of extensions for <see cref="DateTimeOffset"/>
/// </summary>
/// <remarks>
///		Internal use only
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DateTimeOffsetExtensions
{
	extension(DateTimeOffset from)
	{
		/// <summary>
		///		Returns the next occurrence of a cron expression after <paramref name="from"/>.
		/// </summary>
		/// <param name="cron">
		///		The cron expression to evaluate
		/// </param>
		/// <param name="timeZone">
		///		The name of the <see cref="TimeZoneInfo"/> in which the cron expression is evaluated
		/// </param>
		/// <param name="jobName">
		///		The name of the recurring job for error reporting
		/// </param>
		/// <returns>
		///		The <see cref="DateTimeOffset"/> of the next trigger of the cron expression.
		/// </returns>
		/// <exception cref="ImmediateJobException">
		///		Thrown if the cron expression expires with no future occurrences.
		/// </exception>
		public DateTimeOffset GetNextOccurrence(string cron, string timeZone, string jobName)
		{
			var expression = JobCron.Parse(cron);
			var tzi = JobCron.GetTimeZone(timeZone);
			return expression.GetNextOccurrence(from, tzi, inclusive: false) switch
			{
				{ } next => next,
				_ => throw new ImmediateJobException($"Recurring schedule '{jobName}' has no future occurrence."),
			};
		}
	}
}

/// <summary>
///		Collection of extensions for <see cref="RecurringJobSchedule"/>
/// </summary>
/// <remarks>
///		Internal use only
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RecurringJobScheduleExtensions
{
	extension(RecurringJobSchedule schedule)
	{
		/// <summary>
		///	    Returns a list of next occurrences of a particular cron job, starting from the expected next trigger
		///	    until <see cref="TimeProvider.GetUtcNow"/>.
		/// </summary>
		/// <param name="now">
		///		An internally consistent current timestamp being used by the caller.
		/// </param>
		/// <param name="misfireHandlingMode">
		///		Used to determine how many occurrences are returned. When <see cref="MisfireHandlingMode.EnqueueAll"/>,
		///		all occurrences are returned in order to enqueue each; otherwise, only relevant timestamps are returned.
		/// </param>
		/// <returns>
		///		The list of next occurrences of a particular cron job, based on the given configuration.
		/// </returns>
		public IReadOnlyList<DateTimeOffset> GetNextOccurrencesUntil(
			DateTimeOffset now,
			MisfireHandlingMode misfireHandlingMode
		)
		{
			var recurrenceTimes = new List<DateTimeOffset> { schedule.NextRunAt };

			if (misfireHandlingMode == MisfireHandlingMode.EnqueueAll)
			{
				while (recurrenceTimes[^1] <= now)
				{
					recurrenceTimes.Add(
						recurrenceTimes[^1].GetNextOccurrence(
							schedule.Cron,
							schedule.TimeZone,
							schedule.Name
						)
					);
				}
			}
			else
			{
				var next = schedule.NextRunAt;

				while (next < now)
				{
					next = next.GetNextOccurrence(
						schedule.Cron,
						schedule.TimeZone,
						schedule.Name
					);
				}

				recurrenceTimes.Add(next);

				if (next == now)
				{
					recurrenceTimes.Add(
						next.GetNextOccurrence(
							schedule.Cron,
							schedule.TimeZone,
							schedule.Name
						)
					);
				}
			}

			return recurrenceTimes;
		}
	}
}
