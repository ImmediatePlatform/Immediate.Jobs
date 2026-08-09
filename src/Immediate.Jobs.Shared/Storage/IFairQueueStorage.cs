namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Storage capability whose due-job acquisition honors a configured fair-queue policy.
/// </summary>
public interface IFairQueueStorage : IJobStorage;
