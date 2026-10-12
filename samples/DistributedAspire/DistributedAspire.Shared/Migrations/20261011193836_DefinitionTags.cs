using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Immediate.Jobs.DistributedAspire.Shared.Migrations;

/// <inheritdoc />
[SuppressMessage("Design", "CA1062:Validate arguments of public methods")]
public sealed partial class DefinitionTags : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "Tags",
			table: "immediate_job_definition_metadata",
			type: "jsonb",
			nullable: false);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "Tags",
			table: "immediate_job_definition_metadata");
	}
}
