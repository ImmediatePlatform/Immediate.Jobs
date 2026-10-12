using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Immediate.Jobs.DistributedAspire.Shared.Migrations;

/// <inheritdoc />
[SuppressMessage("Design", "CA1062:Validate arguments of public methods")]
public sealed partial class DefinitionRemovalCancellation : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "CancellationReason",
			table: "immediate_recurring_jobs",
			type: "text",
			nullable: true);

		migrationBuilder.AddColumn<string>(
			name: "CancellationReason",
			table: "immediate_jobs",
			type: "text",
			nullable: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "CancellationReason",
			table: "immediate_recurring_jobs");

		migrationBuilder.DropColumn(
			name: "CancellationReason",
			table: "immediate_jobs");
	}
}
