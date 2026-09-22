using Immediate.Jobs.DistributedAspire.Shared.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Immediate.Jobs.DistributedAspire.Shared.Data;

public static class EntityConfigurationExtensions
{
	internal static EntityTypeBuilder<T> HasId<T>(this EntityTypeBuilder<T> builder)
		where T : class, IIntegerKey
	{
		_ = builder.Property(static x => x.Id)
			.IsRequired()
			.HasColumnName("Id")
			.UseIdentityColumn();

		_ = builder.HasIndex(static x => x.Id)
			.IsUnique();

		return builder;
	}

	internal static EntityTypeBuilder<T> HasCreatedOn<T>(this EntityTypeBuilder<T> builder)
		where T : class, ICreated
	{
		_ = builder.Property(static x => x.CreatedOn)
			.IsRequired()
			.HasColumnName("CreatedOn");

		return builder;
	}

	internal static EntityTypeBuilder<T> HasModifiedOn<T>(this EntityTypeBuilder<T> builder)
		where T : class, IModified
	{
		_ = builder.Property(static x => x.ModifiedOn)
			.IsRequired(false);

		return builder;
	}

	internal static EntityTypeBuilder<T> HasVersion<T>(this EntityTypeBuilder<T> builder)
		where T : class, IRowVersion
	{
		_ = builder.Property(static x => x.Version)
			.IsRequired()
			.HasColumnName("Version")
			.IsConcurrencyToken();

		return builder;
	}
}
