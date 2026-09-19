using Immediate.Validations.Shared;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.LinqToDB;

/// <summary>Registers LinqToDB job storage.</summary>
public static class LinqToDBServiceCollectionExtensions
{
	/// <summary>
	///		Selects LinqToDB as the Immediate.Jobs storage provider.
	/// </summary>
	/// <remarks>
	///		The application owns the configured <see cref="DataOptions"/> and its ADO.NET driver. Call <see
	///		cref="LinqToDBSchemaExtensions.CreateImmediateJobsSchemaAsync"/> explicitly when bootstrapping a new database.
	/// </remarks>
	/// <param name="builder">
	///		The Immediate.Jobs storage options builder to configure.
	/// </param>
	/// <param name="schema">
	///		The database schema containing the Immediate.Jobs tables, or <see langword="null"/> for the provider default.
	/// </param>
	/// <returns>
	///		The configured Immediate.Jobs options.
	/// </returns>
	public static IImmediateJobsStorageBuilder UseLinqToDB<T>(
		this IImmediateJobsStorageBuilder builder,
		string? schema = null
	) where T : DataConnection
	{
		ArgumentNullException.ThrowIfNull(builder);

		builder.Services.AddSingleton<Owned<T>>();

		var optionsBuilder = builder.Services
			.AddOptionsWithValidateOnStart<LinqToDBJobStorageOptions>()
			.Validate(
				o =>
				{
					ValidationException.ThrowIfInvalid(o, $@"Validation error for ""{nameof(LinqToDBJobStorageOptions)}""");
					return true;
				}
			);

		if (!string.IsNullOrWhiteSpace(schema))
			optionsBuilder.Configure(o => o.Schema = schema);

		builder.UseStorage<LinqToDBJobStorage<T>>();

		return builder;
	}
}
