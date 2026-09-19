using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Immediate.Jobs.DistributedAspire.Shared.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BatchableRows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Data = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchableRows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "immediate_fair_queue_groups",
                columns: table => new
                {
                    QueueName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GroupId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastServedSequence = table.Column<long>(type: "bigint", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_immediate_fair_queue_groups", x => new { x.QueueName, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "immediate_job_batches",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    TotalJobs = table.Column<int>(type: "integer", nullable: false),
                    PendingCount = table.Column<int>(type: "integer", nullable: false),
                    SucceededCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    CancelledCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedCount = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_immediate_job_batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "immediate_job_servers",
                columns: table => new
                {
                    WorkerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LastHeartbeat = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: false),
                    ActiveWorkers = table.Column<int>(type: "integer", nullable: false),
                    MaxWorkers = table.Column<int>(type: "integer", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_immediate_job_servers", x => x.WorkerId);
                });

            migrationBuilder.CreateTable(
                name: "immediate_recurring_jobs",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    JobName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    QueueName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Cron = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsCodeDefined = table.Column<bool>(type: "boolean", nullable: false),
                    IsPaused = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunAt = table.Column<long>(type: "bigint", nullable: false),
                    LastRunAt = table.Column<long>(type: "bigint", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_immediate_recurring_jobs", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "immediate_jobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    QueueName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: "default"),
                    JobName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GroupId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Context = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    DueAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    WorkerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    RecurringKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TraceParent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TraceState = table.Column<string>(type: "text", nullable: true),
                    ExecutionTraceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExecutionSpanId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ExecutionStartedAt = table.Column<long>(type: "bigint", nullable: true),
                    BatchHandle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RemainingDependencies = table.Column<int>(type: "integer", nullable: false),
                    FailedDependencies = table.Column<int>(type: "integer", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_immediate_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_immediate_jobs_immediate_job_batches_BatchHandle",
                        column: x => x.BatchHandle,
                        principalTable: "immediate_job_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "immediate_job_continuations",
                columns: table => new
                {
                    ChildJobHandle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ParentKind = table.Column<short>(type: "smallint", nullable: false),
                    ParentId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Delay = table.Column<long>(type: "bigint", maxLength: 32, nullable: false),
                    Trigger = table.Column<short>(type: "smallint", nullable: false),
                    ParentOutcome = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_immediate_job_continuations", x => new { x.ChildJobHandle, x.ParentKind, x.ParentId });
                    table.ForeignKey(
                        name: "FK_immediate_job_continuations_immediate_jobs_ChildJobHandle",
                        column: x => x.ChildJobHandle,
                        principalTable: "immediate_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "immediate_job_executions",
                columns: table => new
                {
                    JobHandle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    WorkerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AcquiredAt = table.Column<long>(type: "bigint", nullable: true),
                    ExecutionStartedAt = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    ExecutionTraceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExecutionSpanId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_immediate_job_executions", x => new { x.JobHandle, x.Attempt });
                    table.ForeignKey(
                        name: "FK_immediate_job_executions_immediate_jobs_JobHandle",
                        column: x => x.JobHandle,
                        principalTable: "immediate_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BatchableRows_Id",
                table: "BatchableRows",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_immediate_job_batches_State_CompletedAt",
                table: "immediate_job_batches",
                columns: new[] { "State", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_immediate_job_continuations_ParentKind_ParentId",
                table: "immediate_job_continuations",
                columns: new[] { "ParentKind", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_immediate_job_servers_ExpiresAt",
                table: "immediate_job_servers",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_immediate_jobs_BatchHandle",
                table: "immediate_jobs",
                column: "BatchHandle");

            migrationBuilder.CreateIndex(
                name: "IX_immediate_jobs_QueueName_State_DueAt_CreatedAt",
                table: "immediate_jobs",
                columns: new[] { "QueueName", "State", "DueAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_immediate_jobs_QueueName_State_GroupId",
                table: "immediate_jobs",
                columns: new[] { "QueueName", "State", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_immediate_jobs_RecurringKey",
                table: "immediate_jobs",
                column: "RecurringKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_immediate_jobs_State_CreatedAt",
                table: "immediate_jobs",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_immediate_jobs_State_DueAt",
                table: "immediate_jobs",
                columns: new[] { "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_immediate_recurring_jobs_IsPaused_NextRunAt",
                table: "immediate_recurring_jobs",
                columns: new[] { "IsPaused", "NextRunAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatchableRows");

            migrationBuilder.DropTable(
                name: "immediate_fair_queue_groups");

            migrationBuilder.DropTable(
                name: "immediate_job_continuations");

            migrationBuilder.DropTable(
                name: "immediate_job_executions");

            migrationBuilder.DropTable(
                name: "immediate_job_servers");

            migrationBuilder.DropTable(
                name: "immediate_recurring_jobs");

            migrationBuilder.DropTable(
                name: "immediate_jobs");

            migrationBuilder.DropTable(
                name: "immediate_job_batches");
        }
    }
}
