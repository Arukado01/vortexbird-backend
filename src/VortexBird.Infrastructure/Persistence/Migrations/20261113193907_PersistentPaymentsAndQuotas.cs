using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VortexBird.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistentPaymentsAndQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_OrderId",
                table: "Payments");

            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "Channels",
                type: "integer",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Orders_Id_PartnerId_ChannelId",
                table: "Orders",
                columns: new[] { "Id", "PartnerId", "ChannelId" });

            migrationBuilder.CreateTable(
                name: "PaymentCheckouts",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ExpectedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    PreferenceId = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CheckoutUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AcceptedPaymentId = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentCheckouts", x => x.OrderId);
                    table.CheckConstraint("ck_checkout_amount", "\"ExpectedAmount\" > 0 AND \"Currency\" = 'COP'");
                    table.ForeignKey(
                        name: "FK_PaymentCheckouts_Orders_OrderId_PartnerId_ChannelId",
                        columns: x => new { x.OrderId, x.PartnerId, x.ChannelId },
                        principalTable: "Orders",
                        principalColumns: new[] { "Id", "PartnerId", "ChannelId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentInbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentInbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuotaCounters",
                columns: table => new
                {
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Minute = table.Column<long>(type: "bigint", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotaCounters", x => new { x.PartnerId, x.BudgetKey });
                    table.CheckConstraint("ck_quota_count", "\"Count\" >= 0");
                    table.ForeignKey(
                        name: "FK_QuotaCounters_Partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentReceipts",
                columns: table => new
                {
                    ProviderPaymentId = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderStatus = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Disposition = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReceipts", x => x.ProviderPaymentId);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_PaymentCheckouts_OrderId",
                        column: x => x.OrderId,
                        principalTable: "PaymentCheckouts",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId",
                table: "Payments",
                column: "OrderId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_channel_quota",
                table: "Channels",
                sql: "\"RequestsPerMinute\" >= 10 AND \"RequestsPerMinute\" <= 100000");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCheckouts_ExternalReference",
                table: "PaymentCheckouts",
                column: "ExternalReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCheckouts_NextAttemptAt_LeaseUntil",
                table: "PaymentCheckouts",
                columns: new[] { "NextAttemptAt", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCheckouts_OrderId_PartnerId_ChannelId",
                table: "PaymentCheckouts",
                columns: new[] { "OrderId", "PartnerId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCheckouts_PreferenceId",
                table: "PaymentCheckouts",
                column: "PreferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInbox_NotificationKey",
                table: "PaymentInbox",
                column: "NotificationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentInbox_Status_NextAttemptAt",
                table: "PaymentInbox",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_OrderId_Disposition",
                table: "PaymentReceipts",
                columns: new[] { "OrderId", "Disposition" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentInbox");

            migrationBuilder.DropTable(
                name: "PaymentReceipts");

            migrationBuilder.DropTable(
                name: "QuotaCounters");

            migrationBuilder.DropTable(
                name: "PaymentCheckouts");

            migrationBuilder.DropIndex(
                name: "IX_Payments_OrderId",
                table: "Payments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Orders_Id_PartnerId_ChannelId",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_channel_quota",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RequestsPerMinute",
                table: "Channels");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId",
                table: "Payments",
                column: "OrderId");
        }
    }
}
