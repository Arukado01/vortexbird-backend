using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace VortexBird.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IntegrationDbContext))]
[Migration("20261113235959_ReportingViews")]
public sealed class ReportingViews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE VIEW vw_dashboard_order_metrics AS SELECT "PartnerId", "ChannelId", ("CreatedAt" AT TIME ZONE 'UTC')::date AS day, count(*) AS orders, sum("Total") AS gross_total FROM "Orders" GROUP BY 1,2,3;
        CREATE VIEW vw_inventory_summary AS SELECT "PartnerId", "ChannelId", "StoreId", count(*) AS skus, sum("OnHand") AS on_hand, sum("Reserved") AS reserved, sum("OnHand"-"Reserved") AS available FROM "Stocks" GROUP BY 1,2,3;
        CREATE VIEW vw_payment_summary AS SELECT "PartnerId", "Status", count(*) AS transactions, sum("Amount") AS amount FROM "Payments" GROUP BY 1,2;
        CREATE VIEW vw_partner_activity AS SELECT "PartnerId", "Module", "Result", count(*) AS actions FROM "ActivityLogs" GROUP BY 1,2,3;
        CREATE VIEW vw_integration_health AS SELECT i."Id", i."System", i."Status", count(e."Id") AS executions, avg(e."DurationMs") AS average_duration_ms FROM "Integrations" i LEFT JOIN "IntegrationExecutions" e ON e."IntegrationId"=i."Id" GROUP BY i."Id",i."System",i."Status";
        CREATE VIEW vw_order_status_distribution AS SELECT "PartnerId", "ChannelId", "Status", count(*) AS orders FROM "Orders" GROUP BY 1,2,3;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP VIEW IF EXISTS vw_dashboard_order_metrics, vw_inventory_summary, vw_payment_summary, vw_partner_activity, vw_integration_health, vw_order_status_distribution;");
}
