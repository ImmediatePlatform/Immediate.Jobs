namespace Immediate.Jobs.DistributedAspire.Shared.Data.Contracts;

public interface IModified
{
	PostgresDateTimeOffset? ModifiedOn { get; set; }
}
