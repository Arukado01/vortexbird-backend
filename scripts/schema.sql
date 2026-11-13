CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "ActivityLogs" (
        "Id" uuid NOT NULL,
        "UserId" character varying(2048) NOT NULL,
        "PartnerId" uuid,
        "ChannelId" uuid,
        "Action" character varying(2048) NOT NULL,
        "Module" character varying(2048) NOT NULL,
        "EntityId" character varying(2048) NOT NULL,
        "Result" character varying(2048) NOT NULL,
        "Description" character varying(2048) NOT NULL,
        "CorrelationId" character varying(2048) NOT NULL,
        "RequestId" character varying(2048) NOT NULL,
        "IpAddress" character varying(2048) NOT NULL,
        "UserAgent" character varying(2048) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ActivityLogs" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "IdempotencyRecords" (
        "Id" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "Key" character varying(2048) NOT NULL,
        "RequestHash" character varying(2048) NOT NULL,
        "State" character varying(2048) NOT NULL,
        "OrderId" uuid,
        "ResponseJson" character varying(65536) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "ExpiresAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_IdempotencyRecords" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "IntegrationExecutions" (
        "Id" uuid NOT NULL,
        "IntegrationId" uuid NOT NULL,
        "PartnerId" uuid,
        "Operation" character varying(2048) NOT NULL,
        "Status" character varying(2048) NOT NULL,
        "DurationMs" double precision NOT NULL,
        "Error" character varying(2048),
        "CorrelationId" character varying(2048) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_IntegrationExecutions" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Integrations" (
        "Id" uuid NOT NULL,
        "Name" character varying(2048) NOT NULL,
        "System" character varying(2048) NOT NULL,
        "Mode" character varying(2048) NOT NULL,
        "Status" character varying(2048) NOT NULL,
        "FailureMode" character varying(2048) NOT NULL,
        "LatencyMs" double precision NOT NULL,
        "LastCheckedAt" timestamp with time zone,
        CONSTRAINT "PK_Integrations" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "OutboxMessages" (
        "Id" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "Type" character varying(2048) NOT NULL,
        "Payload" character varying(65536) NOT NULL,
        "Status" character varying(2048) NOT NULL,
        "Attempts" integer NOT NULL,
        "NextAttemptAt" timestamp with time zone NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "ProcessedAt" timestamp with time zone,
        "LastError" character varying(2048),
        "CorrelationId" character varying(2048) NOT NULL,
        "Version" integer NOT NULL,
        CONSTRAINT "PK_OutboxMessages" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Partners" (
        "Id" uuid NOT NULL,
        "Name" character varying(2048) NOT NULL,
        "Code" character varying(2048) NOT NULL,
        "Enabled" boolean NOT NULL,
        "RequestsPerMinute" integer NOT NULL,
        CONSTRAINT "PK_Partners" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Permissions" (
        "Id" uuid NOT NULL,
        "Name" character varying(2048) NOT NULL,
        "Module" character varying(2048) NOT NULL,
        CONSTRAINT "PK_Permissions" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "ProcessedMessages" (
        "Id" uuid NOT NULL,
        "Consumer" character varying(2048) NOT NULL,
        "ProcessedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ProcessedMessages" PRIMARY KEY ("Id", "Consumer")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Products" (
        "Id" uuid NOT NULL,
        "Sku" character varying(2048) NOT NULL,
        "Name" character varying(2048) NOT NULL,
        "Category" character varying(2048) NOT NULL,
        "Active" boolean NOT NULL,
        CONSTRAINT "PK_Products" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Roles" (
        "Id" uuid NOT NULL,
        "Name" character varying(2048) NOT NULL,
        "Description" character varying(2048) NOT NULL,
        "Protected" boolean NOT NULL,
        CONSTRAINT "PK_Roles" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Users" (
        "Id" uuid NOT NULL,
        "Subject" character varying(2048) NOT NULL,
        "Email" character varying(2048) NOT NULL,
        "DisplayName" character varying(2048) NOT NULL,
        "Enabled" boolean NOT NULL,
        "PartnerId" uuid,
        CONSTRAINT "PK_Users" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "WebhookDeliveries" (
        "Id" uuid NOT NULL,
        "SubscriptionId" uuid NOT NULL,
        "EventId" uuid NOT NULL,
        "Status" character varying(2048) NOT NULL,
        "Attempts" integer NOT NULL,
        "StatusCode" integer,
        "CreatedAt" timestamp with time zone NOT NULL,
        "LastError" character varying(2048),
        CONSTRAINT "PK_WebhookDeliveries" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Channels" (
        "Id" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "Name" character varying(2048) NOT NULL,
        "Code" character varying(2048) NOT NULL,
        "Enabled" boolean NOT NULL,
        CONSTRAINT "PK_Channels" PRIMARY KEY ("Id"),
        CONSTRAINT "AK_Channels_Id_PartnerId" UNIQUE ("Id", "PartnerId"),
        CONSTRAINT "FK_Channels_Partners_PartnerId" FOREIGN KEY ("PartnerId") REFERENCES "Partners" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "WebhookSubscriptions" (
        "Id" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "Url" character varying(2048) NOT NULL,
        "EventType" character varying(2048) NOT NULL,
        "Enabled" boolean NOT NULL,
        "SigningSecret" character varying(2048) NOT NULL,
        CONSTRAINT "PK_WebhookSubscriptions" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_WebhookSubscriptions_Partners_PartnerId" FOREIGN KEY ("PartnerId") REFERENCES "Partners" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "RolePermissions" (
        "RoleId" uuid NOT NULL,
        "PermissionId" uuid NOT NULL,
        CONSTRAINT "PK_RolePermissions" PRIMARY KEY ("RoleId", "PermissionId"),
        CONSTRAINT "FK_RolePermissions_Permissions_PermissionId" FOREIGN KEY ("PermissionId") REFERENCES "Permissions" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_RolePermissions_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "UserRoles" (
        "UserId" uuid NOT NULL,
        "RoleId" uuid NOT NULL,
        CONSTRAINT "PK_UserRoles" PRIMARY KEY ("UserId", "RoleId"),
        CONSTRAINT "FK_UserRoles_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_UserRoles_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Orders" (
        "Id" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "ChannelId" uuid NOT NULL,
        "StoreId" character varying(2048) NOT NULL,
        "CustomerId" character varying(2048) NOT NULL,
        "Status" character varying(2048) NOT NULL,
        "PaymentStatus" character varying(2048) NOT NULL,
        "Total" numeric(18,2) NOT NULL,
        "Currency" character varying(2048) NOT NULL,
        "CorrelationId" character varying(2048) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Orders" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Orders_Channels_ChannelId_PartnerId" FOREIGN KEY ("ChannelId", "PartnerId") REFERENCES "Channels" ("Id", "PartnerId") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Prices" (
        "Id" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "ChannelId" uuid NOT NULL,
        "Sku" character varying(2048) NOT NULL,
        "CustomerId" character varying(2048),
        "Amount" numeric(18,2) NOT NULL,
        "Currency" character varying(2048) NOT NULL,
        CONSTRAINT "PK_Prices" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Prices_Channels_ChannelId_PartnerId" FOREIGN KEY ("ChannelId", "PartnerId") REFERENCES "Channels" ("Id", "PartnerId") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Stocks" (
        "Id" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "ChannelId" uuid NOT NULL,
        "StoreId" character varying(2048) NOT NULL,
        "Sku" character varying(2048) NOT NULL,
        "OnHand" integer NOT NULL,
        "Reserved" integer NOT NULL,
        "Version" bigint NOT NULL,
        CONSTRAINT "PK_Stocks" PRIMARY KEY ("Id"),
        CONSTRAINT ck_stock CHECK ("OnHand" >= 0 AND "Reserved" >= 0 AND "Reserved" <= "OnHand"),
        CONSTRAINT "FK_Stocks_Channels_ChannelId_PartnerId" FOREIGN KEY ("ChannelId", "PartnerId") REFERENCES "Channels" ("Id", "PartnerId") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "OrderHistories" (
        "Id" uuid NOT NULL,
        "OrderId" uuid NOT NULL,
        "Status" character varying(2048) NOT NULL,
        "Description" character varying(2048) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_OrderHistories" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_OrderHistories_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "OrderItems" (
        "Id" uuid NOT NULL,
        "OrderId" uuid NOT NULL,
        "Sku" character varying(2048) NOT NULL,
        "Quantity" integer NOT NULL,
        "UnitPrice" numeric(18,2) NOT NULL,
        CONSTRAINT "PK_OrderItems" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_OrderItems_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "Payments" (
        "Id" uuid NOT NULL,
        "OrderId" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "Provider" character varying(2048) NOT NULL,
        "Reference" character varying(2048) NOT NULL,
        "Amount" numeric(18,2) NOT NULL,
        "Status" character varying(2048) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Payments" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Payments_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE TABLE "InventoryReservations" (
        "Id" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "OrderId" uuid NOT NULL,
        "StockId" uuid NOT NULL,
        "Sku" character varying(2048) NOT NULL,
        "Quantity" integer NOT NULL,
        "Status" character varying(2048) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "ExpiresAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_InventoryReservations" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_InventoryReservations_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_InventoryReservations_Stocks_StockId" FOREIGN KEY ("StockId") REFERENCES "Stocks" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_ActivityLogs_PartnerId_CreatedAt" ON "ActivityLogs" ("PartnerId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_ActivityLogs_UserId_CreatedAt" ON "ActivityLogs" ("UserId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Channels_PartnerId_Code" ON "Channels" ("PartnerId", "Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_IdempotencyRecords_PartnerId_Key" ON "IdempotencyRecords" ("PartnerId", "Key");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_InventoryReservations_OrderId_StockId" ON "InventoryReservations" ("OrderId", "StockId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_InventoryReservations_StockId" ON "InventoryReservations" ("StockId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_OrderHistories_OrderId" ON "OrderHistories" ("OrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_OrderItems_OrderId" ON "OrderItems" ("OrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_Orders_ChannelId_PartnerId" ON "Orders" ("ChannelId", "PartnerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_Orders_PartnerId_CreatedAt" ON "Orders" ("PartnerId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_Orders_Status_CreatedAt" ON "Orders" ("Status", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_OutboxMessages_Status_NextAttemptAt" ON "OutboxMessages" ("Status", "NextAttemptAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Partners_Code" ON "Partners" ("Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_Payments_OrderId" ON "Payments" ("OrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Payments_Reference" ON "Payments" ("Reference");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Permissions_Name" ON "Permissions" ("Name");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_Prices_ChannelId_PartnerId" ON "Prices" ("ChannelId", "PartnerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Prices_PartnerId_ChannelId_Sku_CustomerId" ON "Prices" ("PartnerId", "ChannelId", "Sku", "CustomerId") NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Products_Sku" ON "Products" ("Sku");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_RolePermissions_PermissionId" ON "RolePermissions" ("PermissionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Roles_Name" ON "Roles" ("Name");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_Stocks_ChannelId_PartnerId" ON "Stocks" ("ChannelId", "PartnerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Stocks_PartnerId_ChannelId_StoreId_Sku" ON "Stocks" ("PartnerId", "ChannelId", "StoreId", "Sku");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_UserRoles_RoleId" ON "UserRoles" ("RoleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Users_Email" ON "Users" ("Email");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_Users_Subject" ON "Users" ("Subject");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_WebhookDeliveries_Status_CreatedAt" ON "WebhookDeliveries" ("Status", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE UNIQUE INDEX "IX_WebhookDeliveries_SubscriptionId_EventId" ON "WebhookDeliveries" ("SubscriptionId", "EventId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    CREATE INDEX "IX_WebhookSubscriptions_PartnerId" ON "WebhookSubscriptions" ("PartnerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113155946_InitialIntegrationPlatform') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261113155946_InitialIntegrationPlatform', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113160538_OperationalReplicas') THEN
    ALTER TABLE "OutboxMessages" ADD "LeaseId" character varying(2048) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113160538_OperationalReplicas') THEN
    CREATE TABLE "Customers" (
        "Id" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "ExternalId" character varying(2048) NOT NULL,
        "DisplayName" character varying(2048) NOT NULL,
        CONSTRAINT "PK_Customers" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Customers_Partners_PartnerId" FOREIGN KEY ("PartnerId") REFERENCES "Partners" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113160538_OperationalReplicas') THEN
    CREATE TABLE "OmsReplicas" (
        "Id" uuid NOT NULL,
        "OrderId" uuid NOT NULL,
        "PartnerId" uuid NOT NULL,
        "ChannelId" uuid NOT NULL,
        "ExternalOrderNumber" character varying(2048) NOT NULL,
        "FulfillmentState" character varying(2048) NOT NULL,
        "LinesJson" jsonb NOT NULL,
        "SynchronizedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_OmsReplicas" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_OmsReplicas_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113160538_OperationalReplicas') THEN
    CREATE UNIQUE INDEX "IX_Customers_PartnerId_ExternalId" ON "Customers" ("PartnerId", "ExternalId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113160538_OperationalReplicas') THEN
    CREATE UNIQUE INDEX "IX_OmsReplicas_OrderId" ON "OmsReplicas" ("OrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113160538_OperationalReplicas') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261113160538_OperationalReplicas', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113235959_ReportingViews') THEN
    CREATE VIEW vw_dashboard_order_metrics AS SELECT "PartnerId", "ChannelId", ("CreatedAt" AT TIME ZONE 'UTC')::date AS day, count(*) AS orders, sum("Total") AS gross_total FROM "Orders" GROUP BY 1,2,3;
    CREATE VIEW vw_inventory_summary AS SELECT "PartnerId", "ChannelId", "StoreId", count(*) AS skus, sum("OnHand") AS on_hand, sum("Reserved") AS reserved, sum("OnHand"-"Reserved") AS available FROM "Stocks" GROUP BY 1,2,3;
    CREATE VIEW vw_payment_summary AS SELECT "PartnerId", "Status", count(*) AS transactions, sum("Amount") AS amount FROM "Payments" GROUP BY 1,2;
    CREATE VIEW vw_partner_activity AS SELECT "PartnerId", "Module", "Result", count(*) AS actions FROM "ActivityLogs" GROUP BY 1,2,3;
    CREATE VIEW vw_integration_health AS SELECT i."Id", i."System", i."Status", count(e."Id") AS executions, avg(e."DurationMs") AS average_duration_ms FROM "Integrations" i LEFT JOIN "IntegrationExecutions" e ON e."IntegrationId"=i."Id" GROUP BY i."Id",i."System",i."Status";
    CREATE VIEW vw_order_status_distribution AS SELECT "PartnerId", "ChannelId", "Status", count(*) AS orders FROM "Orders" GROUP BY 1,2,3;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261113235959_ReportingViews') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261113235959_ReportingViews', '10.0.12');
    END IF;
END $EF$;
COMMIT;

