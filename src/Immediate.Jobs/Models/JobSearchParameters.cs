namespace Immediate.Jobs.Models;

public sealed record JobSearchParameters
{
	public string? JobKey { get; init; }
	public IReadOnlyList<JobStatus> Statuses { get; init; }
	public JobSearchParameterTimeRange? TimeRange { get; init; }
}

public record struct JobSearchParameterTimeRange
{
	public IReadOnlyList<JobStatus> Statuses { get; init; }
	public DateTimeOffset? Start { get; init; }
	public DateTimeOffset? End { get; init; }
}
