using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Immediate.Jobs.DistributedAspire.Shared.Migrations;

/// <inheritdoc />
[SuppressMessage("Design", "CA1062:Validate arguments of public methods")]
public sealed partial class DefinitionAcquisitionControls : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "immediate_job_acquisitions",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "uuid", nullable: false),
				JobName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
				AcquiredAt = table.Column<long>(type: "bigint", nullable: false),
			},
			constraints: table => table.PrimaryKey("PK_immediate_job_acquisitions", x => x.Id));

		migrationBuilder.CreateTable(
			name: "immediate_job_definitions",
			columns: table => new
			{
				JobName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
				IsPaused = table.Column<bool>(type: "boolean", nullable: false),
				AcquisitionStatus = table.Column<short>(type: "smallint", nullable: false),
				NextEligibleAt = table.Column<long>(type: "bigint", nullable: true),
				FixedWindowStart = table.Column<long>(type: "bigint", nullable: true),
				FixedWindowCount = table.Column<int>(type: "integer", nullable: false),
				ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false),
			},
			constraints: table => table.PrimaryKey("PK_immediate_job_definitions", x => x.JobName));

		migrationBuilder.CreateIndex(
			name: "IX_immediate_jobs_JobName_State_LeaseExpiresAt",
			table: "immediate_jobs",
			columns: ["JobName", "State", "LeaseExpiresAt"]);

		migrationBuilder.CreateIndex(
			name: "IX_immediate_job_acquisitions_JobName_AcquiredAt",
			table: "immediate_job_acquisitions",
			columns: ["JobName", "AcquiredAt"]);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "immediate_job_acquisitions");

		migrationBuilder.DropTable(
			name: "immediate_job_definitions");

		migrationBuilder.DropIndex(
			name: "IX_immediate_jobs_JobName_State_LeaseExpiresAt",
			table: "immediate_jobs");
	}
}
