using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Immediate.Jobs.DistributedAspire.Shared.Migrations;

/// <inheritdoc />
[SuppressMessage("Design", "CA1062:Validate arguments of public methods")]
public sealed partial class RemoveStoredJobQueues : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "IX_immediate_jobs_QueueName_State_DueAt_CreatedAt",
			table: "immediate_jobs");

		migrationBuilder.DropIndex(
			name: "IX_immediate_jobs_QueueName_State_GroupId",
			table: "immediate_jobs");

		migrationBuilder.DropColumn(
			name: "QueueName",
			table: "immediate_recurring_jobs");

		migrationBuilder.DropColumn(
			name: "QueueName",
			table: "immediate_jobs");

		migrationBuilder.CreateIndex(
			name: "IX_immediate_jobs_JobName_State_DueAt_CreatedAt",
			table: "immediate_jobs",
			columns: ["JobName", "State", "DueAt", "CreatedAt"]);

		migrationBuilder.CreateIndex(
			name: "IX_immediate_jobs_JobName_State_GroupId",
			table: "immediate_jobs",
			columns: ["JobName", "State", "GroupId"]);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "IX_immediate_jobs_JobName_State_DueAt_CreatedAt",
			table: "immediate_jobs");

		migrationBuilder.DropIndex(
			name: "IX_immediate_jobs_JobName_State_GroupId",
			table: "immediate_jobs");

		migrationBuilder.AddColumn<string>(
			name: "QueueName",
			table: "immediate_recurring_jobs",
			type: "character varying(256)",
			maxLength: 256,
			nullable: false,
			defaultValue: "");

		migrationBuilder.AddColumn<string>(
			name: "QueueName",
			table: "immediate_jobs",
			type: "character varying(256)",
			maxLength: 256,
			nullable: false,
			defaultValue: "default");

		migrationBuilder.CreateIndex(
			name: "IX_immediate_jobs_QueueName_State_DueAt_CreatedAt",
			table: "immediate_jobs",
			columns: ["QueueName", "State", "DueAt", "CreatedAt"]);

		migrationBuilder.CreateIndex(
			name: "IX_immediate_jobs_QueueName_State_GroupId",
			table: "immediate_jobs",
			columns: ["QueueName", "State", "GroupId"]);
	}
}
