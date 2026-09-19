using Immediate.Validations.Shared;

namespace Immediate.Jobs.LinqToDB;

[Validate]
internal sealed partial class LinqToDBJobStorageOptions : IValidationTarget<LinqToDBJobStorageOptions>
{
	[NotEmpty]
	public string? Schema { get; set; }
}
