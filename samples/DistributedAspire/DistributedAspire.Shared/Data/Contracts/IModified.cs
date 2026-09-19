namespace Immediate.Jobs.DistributedAspire.Shared.Data.Contracts;

public interface IModified
{
	DateTimeOffset? ModifiedOn { get; set; }
}
