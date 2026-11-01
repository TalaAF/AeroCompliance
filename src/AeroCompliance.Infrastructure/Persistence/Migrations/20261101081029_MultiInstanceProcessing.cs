using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroCompliance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiInstanceProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "claim_token",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claim_until",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "failed_at",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.CreateTable(
                name: "ai_processing_jobs",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_routing_key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    result_payload = table.Column<string>(type: "jsonb", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_processing_jobs", x => x.request_id);
                    table.CheckConstraint("CK_ai_processing_jobs_state", "state IN ('processing', 'result_ready')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_failed_at_next_attempt_at_claim_until",
                table: "outbox_messages",
                columns: new[] { "failed_at", "next_attempt_at", "claim_until" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_processing_jobs_state_lease_until",
                table: "ai_processing_jobs",
                columns: new[] { "state", "lease_until" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_processing_jobs");

            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_failed_at_next_attempt_at_claim_until",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "claim_token",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "claim_until",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "failed_at",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "outbox_messages");
        }
    }
}
