namespace Immediate.Jobs.Models;

public enum JobStatus
{
	None = 0,
	Created,
	Scheduled,
	InProgress,
	Completed,
	Failed,
	Canceled,
}
