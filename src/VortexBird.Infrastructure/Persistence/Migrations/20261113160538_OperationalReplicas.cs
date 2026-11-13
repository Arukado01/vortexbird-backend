using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VortexBird.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationalReplicas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LeaseId",
                table: "OutboxMessages",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_Partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OmsReplicas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalOrderNumber = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    FulfillmentState = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    LinesJson = table.Column<string>(type: "jsonb", maxLength: 2048, nullable: false),
                    SynchronizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OmsReplicas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OmsReplicas_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PartnerId_ExternalId",
                table: "Customers",
                columns: new[] { "PartnerId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OmsReplicas_OrderId",
                table: "OmsReplicas",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "OmsReplicas");

            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "OutboxMessages");
        }
    }
}
