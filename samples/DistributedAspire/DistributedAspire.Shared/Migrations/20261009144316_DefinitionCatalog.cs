using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Immediate.Jobs.DistributedAspire.Shared.Migrations;

/// <inheritdoc />
[SuppressMessage("Design", "CA1062:Validate arguments of public methods")]
public sealed partial class DefinitionCatalog : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterDatabase()
			.Annotation("Npgsql:CollationDefinition:immediate_jobs_case_insensitive", "und-u-ks-level2,und-u-ks-level2,icu,False");

		migrationBuilder.AlterColumn<string>(
			name: "JobName", table: "immediate_jobs", type: "character varying(256)",
			maxLength: 256, nullable: false, collation: "immediate_jobs_case_insensitive",
			oldClrType: typeof(string), oldType: "character varying(256)", oldMaxLength: 256);

		migrationBuilder.AlterColumn<string>(
			name: "JobName", table: "immediate_recurring_jobs", type: "character varying(256)",
			maxLength: 256, nullable: false, collation: "immediate_jobs_case_insensitive",
			oldClrType: typeof(string), oldType: "character varying(256)", oldMaxLength: 256);

		migrationBuilder.CreateTable(
			name: "immediate_job_definition_catalog",
			columns: table => new
			{
				Id = table.Column<int>(type: "integer", nullable: false),
				ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false),
			},
			constraints: table => table.PrimaryKey("PK_immediate_job_definition_catalog", x => x.Id));

		migrationBuilder.CreateTable(
			name: "immediate_job_definition_metadata",
			columns: table => new
			{
				Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "immediate_jobs_case_insensitive"),
				QueueName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
				QueuePriority = table.Column<int>(type: "integer", nullable: false),
				QueueConcurrency = table.Column<int>(type: "integer", nullable: false),
				Cron = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
				TimeZone = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
				MaxAttempts = table.Column<int>(type: "integer", nullable: false),
				TimeoutTicks = table.Column<long>(type: "bigint", nullable: true),
				MaxConcurrency = table.Column<int>(type: "integer", nullable: false),
				OverlapPolicy = table.Column<short>(type: "smallint", nullable: false),
				MisfireHandlingMode = table.Column<short>(type: "smallint", nullable: false),
				Backoff = table.Column<short>(type: "smallint", nullable: false),
				BackoffBaseTicks = table.Column<long>(type: "bigint", nullable: false),
			},
			constraints: table => table.PrimaryKey("PK_immediate_job_definition_metadata", x => x.Name));
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterColumn<string>(
			name: "JobName", table: "immediate_jobs", type: "character varying(256)",
			maxLength: 256, nullable: false,
			oldClrType: typeof(string), oldType: "character varying(256)", oldMaxLength: 256,
			oldCollation: "immediate_jobs_case_insensitive");

		migrationBuilder.AlterColumn<string>(
			name: "JobName", table: "immediate_recurring_jobs", type: "character varying(256)",
			maxLength: 256, nullable: false,
			oldClrType: typeof(string), oldType: "character varying(256)", oldMaxLength: 256,
			oldCollation: "immediate_jobs_case_insensitive");

		migrationBuilder.DropTable(name: "immediate_job_definition_catalog");
		migrationBuilder.DropTable(name: "immediate_job_definition_metadata");
		migrationBuilder.AlterDatabase()
			.OldAnnotation("Npgsql:CollationDefinition:immediate_jobs_case_insensitive", "und-u-ks-level2,und-u-ks-level2,icu,False");
	}
}
