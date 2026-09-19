using System.Diagnostics;
using System.Globalization;
using Immediate.Jobs.DistributedAspire.Shared.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Immediate.Jobs.DistributedAspire.Shared.Data;

[DebuggerDisplay($"{{{nameof(GetDebuggerDisplay)}(),nq}}")]
public sealed class BatchEntity : IEntity
{
	public DateTimeOffset CreatedOn { get; set; }
	public DateTimeOffset? ModifiedOn { get; set; }
	public int Id { get; }
	public long Version { get; set; }
	public Guid Data { get; set; }

	internal sealed class BatchEntityConfiguration : IEntityTypeConfiguration<BatchEntity>
	{
		public void Configure(EntityTypeBuilder<BatchEntity> builder)
		{
			_ = builder.HasId();
			_ = builder.HasVersion();
			_ = builder.HasCreatedOn();

			_ = builder.Property(static x => x.Data)
				.IsRequired()
				.HasColumnName("Data");
		}
	}

	private string GetDebuggerDisplay()
	{
		return Id.ToString(CultureInfo.InvariantCulture);
	}
}
