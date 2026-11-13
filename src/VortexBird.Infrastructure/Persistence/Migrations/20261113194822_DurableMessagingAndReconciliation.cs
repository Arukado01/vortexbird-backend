using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VortexBird.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableMessagingAndReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReconcileRequested",
                table: "PaymentCheckouts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MessagingInbox",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Consumer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    LeaseId = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagingInbox", x => new { x.EventId, x.Consumer });
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessagingInbox_Status_LeaseUntil",
                table: "MessagingInbox",
                columns: new[] { "Status", "LeaseUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessagingInbox");

            migrationBuilder.DropColumn(
                name: "ReconcileRequested",
                table: "PaymentCheckouts");
        }
    }
}
