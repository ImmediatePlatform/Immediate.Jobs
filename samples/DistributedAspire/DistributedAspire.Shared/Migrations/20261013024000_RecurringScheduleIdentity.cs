using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Immediate.Jobs.DistributedAspire.Shared.Migrations;

/// <inheritdoc />
[SuppressMessage("Design", "CA1062:Validate arguments of public methods")]
public sealed partial class RecurringScheduleIdentity : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterColumn<string>(
			name: "Name",
			table: "immediate_recurring_jobs",
			type: "character varying(256)",
			maxLength: 256,
			nullable: false,
			collation: "immediate_jobs_case_insensitive",
			oldClrType: typeof(string),
			oldType: "character varying(256)",
			oldMaxLength: 256);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterColumn<string>(
			name: "Name",
			table: "immediate_recurring_jobs",
			type: "character varying(256)",
			maxLength: 256,
			nullable: false,
			oldClrType: typeof(string),
			oldType: "character varying(256)",
			oldMaxLength: 256,
			oldCollation: "immediate_jobs_case_insensitive");
	}
}
