# Mercado Pago sandbox: checkout, recepción y conciliación

El flujo está implementado y conectado a `IPaymentGateway`, conservando **Simulator como configuración predeterminada**. No se activó Mercado Pago, no se usaron credenciales reales y no se hizo ningún pago externo. La evidencia disponible comprende compilación, transporte HTTP controlado, reglas de transición y pruebas del límite HTTP; no equivale a una sesión de sandbox ni a pruebas de concurrencia contra PostgreSQL desplegado.

## Flujo implementado

1. La orden y sus reservas se crean atómicamente con `order.created`. El consumidor inicia la saga. El contrato público rechaza `paymentOutcome`. Las rutas de simulación se registran exclusivamente en Development con Simulator. El proveedor externo recibe un `PaymentRequest` sin resultado simulado.
2. Antes del POST de preferencias se guardan `PaymentCheckout` y `Payment` con la referencia `partnerId:orderId`, importe y moneda del servidor, vencimiento de reserva y lease. Un bloqueo transaccional por orden impide crear dos intenciones.
3. Se comprueba `/users/me`: ID configurado y etiqueta `test_user`. El prefijo `APP_USR` no identifica una cuenta de pruebas. Solo entonces se crea el checkout alojado, con vencimiento de hasta quince minutos, sin recibir datos de tarjeta.
4. La URL se persiste y solo se expone mientras la orden tiene reserva/pago pendiente y no ha expirado. Si se pierde la respuesta, queda `unknown`. **El POST no se repite**: la búsqueda por referencia recupera y verifica preferencia, vendedor, importe, moneda y vencimiento. Ausencia o ambigüedad nunca autoriza otra creación automática.
5. El webhook valida HMAC, timestamp, `data.id`, topic, vendedor y modo de pruebas. Persiste una inbox mínima antes de responder HTTP 200. La clave única deriva del manifest firmado canónico: variantes equivalentes de la firma deduplican.
6. El worker consulta el pago autoritativo, exige `live_mode=false`, vendedor, ID, importe, COP y referencia exactos, y aplica el resultado bajo el mismo bloqueo de orden. Una notificación o retorno del navegador nunca confirma un pago.
7. Una aprobación válida consume inventario una sola vez y genera outbox `payment.approved` y `order.confirmed`. El rechazo libera reservas. Una aprobación tardía, sin reserva válida o de un segundo pago genera un intento durable de reembolso; no consume existencias de nuevo.
8. El reembolso verifica el pago antes del POST, utiliza clave de idempotencia estable por orden/pago y vuelve a consultar después. Solo `refunded` del recurso autoritativo confirma compensación. Un crash o timeout conserva `refund_pending` y permite reintentar con la misma clave.

`PaymentReceipt` conserva cada pago independientemente: el segundo pago aprobado se reembolsa sin cancelar el primero aceptado. Estados pendientes/rechazados fuera de orden no degradan aprobaciones; una aprobación antigua no revierte un reembolso. Un reembolso antes del despacho restituye las reservas comprometidas una vez y cancela la orden. Si ya hubo despacho/entrega, no repone inventario automáticamente: el checkout queda en revisión. Los contracargos requieren revisión operativa.

## Endpoints

| Endpoint | Acceso y resultado |
|---|---|
| `GET /api/v1/payments/orders/{orderId}/checkout` | `payments.read` y alcance partner/canal; `{orderId,provider,status,checkoutUrl,expiresAt,paymentStatus,canRetry:false}` |
| `POST /api/v1/payments/orders/{orderId}/reconcile` | `payments.manage` y alcance partner/canal; reprograma conciliación durable, devuelve 202 con el mismo DTO, sin declarar aprobación |
| `POST /api/v1/payments/mercadopago/webhook?data.id={paymentId}` | Sin JWT; exige `x-signature` y `x-request-id`; deshabilitado=404, firma inválida=401, envelope inválido=400, más de 32 KiB=413 |
| `GET /api/v1/payments/inbox` | Operador global con `payments.manage`; inspecciona dead-letter sin cuerpos ni secretos |
| `POST /api/v1/payments/inbox/{id}/retry` | Operador global con `payments.manage`; reprograma una recepción agotada si sandbox está habilitado |

Proveedor en DTO: `simulator` o `mercadopago-sandbox`. Estados externos: `creating`, `pending`, `unknown`, `approved`, `cancelled`, `refund_pending`, `refunded`, `manual_review`. La URL es utilizable con `pending` y pago pendiente. Hosts permitidos: `www.mercadopago.com.co`, `www.mercadopago.com` y `sandbox.mercadopago.com`, siempre HTTPS443 sin credenciales ni fragmento.

## Registro y configuración

`AddPaymentProcessing(configuration)` se registra después del gateway simulado; `api.MapPaymentEndpoints()` agrega rutas autenticadas y `app.MapMercadoPagoWebhook()` agrega recepción firmada. El modelo incorpora `PaymentModelConfiguration.ConfigurePayments(b)` y migraciones. El proveedor no se activa desde el frontend.

| Variable | Valor o propósito |
|---|---|
| `Payments__Provider` | `Simulator` por defecto; `MercadoPagoSandbox` para activación explícita |
| `Payments__Sandbox__Enabled` | Debe ser `true`, además del proveedor seleccionado; configuración incompleta impide iniciar |
| `Payments__Sandbox__AccessToken` | Credencial del vendedor de prueba, suministrada mediante almacén de secretos |
| `Payments__Sandbox__ExpectedTestSellerId` | ID positivo del vendedor de prueba esperado |
| `Payments__Sandbox__WebhookSecret` | Secreto de firma de la aplicación, mínimo 32 caracteres; nunca versionar |
| `Payments__Sandbox__NotificationUrl` | URL HTTPS pública exacta del endpoint de recepción |
| `Payments__Sandbox__ReturnUrl` | URL HTTPS pública del frontend; el retorno solo permite consultar estado |
| `Workers__Enabled` | Habilitado para consumir inbox y ejecutar conciliación |

El cliente rechaza URLs HTTP, IP literales, localhost, dominios internos y destinos con credenciales/fragmentos; deshabilita redirects HTTP y limita cada solicitud a diez segundos. No registra tokens ni cuerpos de error del proveedor. Respuestas limitadas a 128 KiB. La búsqueda acepta hasta 50 resultados completos y verifica cada pago por ID; exceso, inconsistencias o referencias ajenas fallan hacia revisión.

El worker usa leases de dos minutos para inbox y tres para checkout, deadlines menores y finalización condicionada al token. Reintenta con backoff exponencial limitado a quince minutos; doce fallos consecutivos producen dead-letter o revisión manual. El polling automático cubre treinta días: cada treinta segundos con reserva vigente y cada cinco minutos después. Webhooks y conciliación solicitada por operador permiten recuperar órdenes más antiguas. Un POST incierto sin preferencia encontrada mantiene `unknown`; la expiración de reserva cancela la orden y una aprobación posterior se reembolsa. Las búsquedas externas tienen ventanas de retención propias y pueden exigir revisión manual.

## Validación y límites reales

```powershell
dotnet restore tests/PaymentChecks/PaymentChecks.csproj
dotnet run --project tests/PaymentChecks -- --report=tests/PaymentChecks/results.json
```

`PaymentChecks` referencia infraestructura real. Comprueba vendedor de prueba, prohibición de pagos live, correlación, expiración, recuperación sin POST, búsquedas completas, duplicados, reembolso con clave estable y lectura posterior, HMAC/replay canónico, transiciones monotónicas y configuración deshabilitada. Usa `HttpMessageHandler` controlado y reglas de producción; **no ejecuta la saga contra PostgreSQL ni contacta Mercado Pago**. `ApiBoundaryChecks` verifica rechazos HTTP con TestServer. La validación del esquema SQL se reporta por separado en infraestructura.

Para validar una sesión externa aún se requieren vendedor/comprador de prueba habilitados para Colombia, credenciales/secretos de esa aplicación, endpoints HTTPS accesibles y ejecutar checkout→webhook→consulta→compensación contra la cuenta de prueba. También corresponde ejecutar carreras de callbacks/cancelación/despacho sobre PostgreSQL antes del despliegue. Estas verificaciones dependen de infraestructura/cuenta; el flujo de implementación está disponible.

Se eligió Checkout Pro Preferences para materializar el contrato de checkout alojado. El proveedor también recomienda Orders API para nuevas integraciones; se debe revisar disponibilidad y migración para uso productivo. No se declara certificación productiva ni sandbox externo verificado.

## Referencias oficiales

- [Cuentas de prueba](https://www.mercadopago.com.co/developers/es/docs/checkout-pro-preferences/test-accounts)
- [Identidad y test_user](https://developers.mercadolibre.com.co/consulta-usuarios)
- [Crear preferencia](https://www.mercadopago.com.co/developers/en/reference/online-payments/checkout-pro-preferences/create-preference/post)
- [Buscar preferencias](https://www.mercadopago.com.co/developers/en/reference/online-payments/checkout-pro-preferences/search-preferences/get)
- [Buscar pagos](https://www.mercadopago.com.co/developers/en/reference/online-payments/checkout-pro-preferences/search-payments/get)
- [Reembolso e idempotencia](https://www.mercadopago.com.co/developers/en/reference/online-payments/checkout-pro/create-refund/post)
- [Notificaciones y recurso autoritativo](https://www.mercadopago.com.co/developers/es/docs/checkout-pro-orders/resources/notifications/webhooks)
- [Manifest HMAC](https://www.mercadopago.com.co/developers/es/docs/checkout-api-orders/optional-notifications)

### Verificación concurrente de la simulación (17-11-2026)

La resolución del simulador relee orden y pago después de adquirir el lock de la orden. Las entidades cargadas durante la autorización no pueden conservar un estado pendiente anterior a otra resolución. La prueba `tests/Integration.ps1` intenta ocho aprobaciones simultáneas: debe aceptar una y devolver conflicto para las otras siete, con una sola transición final. Su ejecución integrada está pendiente en esta sesión por el bloqueo de sockets locales (10013); no se sustituyó por una prueba de pagos productivos.