using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Immediate.Jobs.DistributedAspire.Shared.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Immediate.Jobs.DistributedAspire.Shared.Data;

public sealed class BatchEntity : IEntity
{
	public DateTimeOffset CreatedOn { get; set; } = default!;
	public PostgresDateTimeOffset? ModifiedOn { get; set; }
	public int Id { get; }
	public long Version { get; set; }
	public Guid Guid { get; set; }
	public byte[] Hash { get; set; } = default!;

	public byte[] CalculateHash()
	{
		return SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"BatchEntity {Id} {Guid} {CreatedOn} {ModifiedOn} {Version}")));
	}

	internal sealed class BatchEntityConfiguration : IEntityTypeConfiguration<BatchEntity>
	{
		public void Configure(EntityTypeBuilder<BatchEntity> builder)
		{
			_ = builder.HasId();
			_ = builder.HasVersion();
			_ = builder.HasHash();
			_ = builder.HasCreatedOn();

			_ = builder.Property(static x => x.Guid)
				.IsRequired()
				.HasColumnName("Guid");

			_ = builder
				.OwnsOne(c => c.ModifiedOn, p => p.HasPostgresDateTimeoffset());
		}
	}
}
