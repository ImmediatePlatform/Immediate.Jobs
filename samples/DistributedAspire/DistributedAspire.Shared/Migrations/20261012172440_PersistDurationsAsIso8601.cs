using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Immediate.Jobs.DistributedAspire.Shared.Migrations
{
    /// <inheritdoc />
    public partial class PersistDurationsAsIso8601 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackoffBaseTicks",
                table: "immediate_job_definition_metadata");

            migrationBuilder.DropColumn(
                name: "FixedWindowPeriodTicks",
                table: "immediate_job_definition_metadata");

            migrationBuilder.DropColumn(
                name: "SlidingWindowPeriodTicks",
                table: "immediate_job_definition_metadata");

            migrationBuilder.DropColumn(
                name: "TimeoutTicks",
                table: "immediate_job_definition_metadata");

            migrationBuilder.AddColumn<string>(
                name: "BackoffBase",
                table: "immediate_job_definition_metadata",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FixedWindowPeriod",
                table: "immediate_job_definition_metadata",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlidingWindowPeriod",
                table: "immediate_job_definition_metadata",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Timeout",
                table: "immediate_job_definition_metadata",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Delay",
                table: "immediate_job_continuations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldMaxLength: 32);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackoffBase",
                table: "immediate_job_definition_metadata");

            migrationBuilder.DropColumn(
                name: "FixedWindowPeriod",
                table: "immediate_job_definition_metadata");

            migrationBuilder.DropColumn(
                name: "SlidingWindowPeriod",
                table: "immediate_job_definition_metadata");

            migrationBuilder.DropColumn(
                name: "Timeout",
                table: "immediate_job_definition_metadata");

            migrationBuilder.AddColumn<long>(
                name: "BackoffBaseTicks",
                table: "immediate_job_definition_metadata",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "FixedWindowPeriodTicks",
                table: "immediate_job_definition_metadata",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SlidingWindowPeriodTicks",
                table: "immediate_job_definition_metadata",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TimeoutTicks",
                table: "immediate_job_definition_metadata",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Delay",
                table: "immediate_job_continuations",
                type: "bigint",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);
        }
    }
}
