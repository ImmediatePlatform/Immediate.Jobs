using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Immediate.Jobs.Shared.Apis;
using Meziantou.Framework.Scheduling;

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
			var tzi = TimeZoneInfo.FindSystemTimeZoneById(timeZone);

			IRecurrenceRule rule = true switch
			{
				_ when CronExpression.TryParse(cron, out var cronExpression) => cronExpression,
				_ when RecurrenceRule.TryParse(cron, out var recurrenceRule) => recurrenceRule,
				_ => throw new ImmediateJobException($"Recurring schedule '{jobName}' has a cron expression that cannot be parsed. ('{cron}')"),
			};

			foreach (var next in rule.GetNextOccurrences(from, tzi))
			{
				if (next == from)
					continue;

				return next;
			}

			throw new ImmediateJobException($"Recurring schedule '{jobName}' has no future occurrence. ('{cron}')");
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
			var tzi = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);

			IRecurrenceRule rule = true switch
			{
				_ when CronExpression.TryParse(schedule.Cron, out var cronExpression) => cronExpression,
				_ when RecurrenceRule.TryParse(schedule.Cron, out var recurrenceRule) => recurrenceRule,
				_ => throw new ImmediateJobException($"Recurring schedule '{schedule.Name}' has a cron expression that cannot be parsed. ('{schedule.Cron}')"),
			};

			var recurrenceTimes = new List<DateTimeOffset> { schedule.NextRunAt };

			// add one tick to skip past current run
			var occurrences = rule.GetNextOccurrences(schedule.NextRunAt, tzi);

			foreach (var next in occurrences)
			{
				if (next == schedule.NextRunAt)
					continue;

				if (misfireHandlingMode != MisfireHandlingMode.EnqueueAll && next < now)
					continue;

				recurrenceTimes.Add(next);

				if (next > now)
					break;
			}

			if (recurrenceTimes[^1] <= now)
				throw new ImmediateJobException($"Recurring schedule '{schedule.Name}' has no future occurrence. ('{schedule.Cron}')");

			return recurrenceTimes;
		}
	}
}
