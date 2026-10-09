using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Testing.Storage;

internal static class MonitoringTestQueries
{
	internal static async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetSchedulesAsync(IJobStorage storage, CancellationToken cancellationToken)
	{
		var definitions = await storage.GetMonitoringDefinitionsAsync(cancellationToken);
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		var states = snapshot.Recurring.ToDictionary(static state => state.Name, StringComparer.Ordinal);
		return [.. definitions.Recurring.Select(definition => RecurringJobSchedule.FromDefinition(definition, states[definition.Name]))];
	}
}
