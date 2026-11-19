-- psql supplies both order IDs as quoted literals. All checks are read-only.
WITH expected(order_id, event_type) AS (
    VALUES (:'rejected'::uuid, 'order.created'),
           (:'rejected'::uuid, 'payment.rejected'),
           (:'approved'::uuid, 'order.created'),
           (:'approved'::uuid, 'payment.approved'),
           (:'approved'::uuid, 'order.confirmed')
), events AS (
    SELECT e.order_id, e.event_type,
           count(o."Id") AS copies,
           count(*) FILTER (WHERE o."Status" = 'processed' AND o."ProcessedAt" IS NOT NULL
               AND i."Status" = 'completed' AND i."CompletedAt" IS NOT NULL
               AND p."ProcessedAt" IS NOT NULL) AS completed
    FROM expected e
    LEFT JOIN "OutboxMessages" o ON o."Payload"::jsonb->>'orderId' = e.order_id::text AND o."Type" = e.event_type
    LEFT JOIN "MessagingInbox" i ON i."EventId" = o."Id" AND i."Consumer" = 'integration-router-v1'
    LEFT JOIN "ProcessedMessages" p ON p."Id" = o."Id" AND p."Consumer" = i."Consumer"
    GROUP BY e.order_id, e.event_type
), reservations AS (
    SELECT "OrderId", count(*) AS rows, sum("Quantity") AS units, min("Status") AS status
    FROM "InventoryReservations"
    WHERE "OrderId" IN (:'rejected'::uuid, :'approved'::uuid)
    GROUP BY "OrderId"
)
SELECT json_build_object(
    'durableEvents', (SELECT count(*) FROM events WHERE copies = 1 AND completed = 1),
    'released', EXISTS (SELECT 1 FROM reservations WHERE "OrderId" = :'rejected'::uuid AND rows = 1 AND units = 1 AND status = 'released'),
    'committed', EXISTS (SELECT 1 FROM reservations WHERE "OrderId" = :'approved'::uuid AND rows = 1 AND units = 1 AND status = 'committed'),
    'confirmedTransitions', (SELECT count(*) FROM "OrderHistories" WHERE "OrderId" = :'approved'::uuid AND "Status" = 'confirmed')
);
