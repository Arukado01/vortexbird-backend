# Piloto de Mercado Pago en ambiente de pruebas

## Estado real

`Infrastructure/Payments/MercadoPagoSandboxClient.cs` implementa llamadas HTTP reales para crear una preferencia de Checkout Pro y consultar un pago, con controles de vendedor, modo de pruebas y correlación. `MercadoPagoWebhookSignature.cs` verifica la firma HMAC del aviso. Las pruebas usan un transporte HTTP controlado: **no se contactó Mercado Pago y no se ejecutó ningún pago**.

El piloto está aislado y **no reemplaza `IPaymentGateway`**, no se registra automáticamente y no expone un webhook público. La saga de la aplicación continúa usando el simulador explícito. Esta separación es deliberada: un checkout alojado necesita persistir su URL, referencia y estado incierto, recibir notificaciones durables y reconciliar resultados antes de consumir inventario. No basta con cambiar un nombre en configuración.

## Controles implementados

- Habilitación explícita mediante `MercadoPagoSandboxOptions.Enabled`; sin token o vendedor esperado, falla antes de enviar HTTP.
- Antes de crear una preferencia, consulta `GET https://api.mercadolibre.com/users/me`. El ID debe coincidir con el vendedor configurado y `tags` debe contener `test_user`. Una cuenta real no pasa este control.
- No se identifica una credencial de prueba por su prefijo. La documentación actual indica que Checkout Pro puede entregar credenciales `APP_USR` para vendedores de prueba.
- La preferencia usa importe y moneda calculados por el backend. La referencia vincula partner y orden; el cliente no debe fijar el importe desde un formulario.
- El checkout se devuelve solo si su URL pertenece a los hosts HTTPS de Mercado Pago permitidos y la preferencia corresponde al vendedor esperado.
- La consulta autoritativa de pago exige `live_mode=false`, vendedor, ID, importe, moneda y referencia exactos. Un aviso o redirect del navegador jamás confirma un pago por sí solo.
- La firma exige `x-signature`, `x-request-id` y el `data.id` numérico del query string. Compara HMAC-SHA256 en tiempo constante; rechaza campos duplicados, digest inválido y avisos fuera de una ventana configurable de cinco minutos. Admite timestamps en segundos o milisegundos.
- El registro opcional `AddMercadoPagoSandboxPilot` configura timeout de diez segundos y deshabilita redirects HTTP. No registra `IPaymentGateway`.
- No recibe ni guarda PAN/CVV; no imprime tokens ni cuerpos de error del proveedor. No reintenta un POST de preferencias: un timeout requiere estado incierto y reconciliación, no una segunda creación automática.

La ventana de firma reduce replay temporal, pero **no reemplaza una inbox persistente con unicidad por evento**. Un webhook legítimo puede duplicarse durante la ventana. El receptor futuro debe persistir, confirmar recepción y procesar idempotentemente.

## Configuración para una integración posterior

Guardar el token y el secreto de firma fuera de Git, mediante variables de entorno en desarrollo o Secrets Manager. Registrar explícitamente el cliente solo después de completar la persistencia y el receptor. El objeto de opciones requiere `AccessToken`, `ExpectedTestSellerId`, `NotificationUrl` HTTPS y `ReturnUrl` HTTPS; no contiene credenciales predeterminadas.

Antes de activar el flujo:

1. Crear o seleccionar vendedor y comprador de prueba y comprobar la API/país habilitados en la cuenta.
2. Añadir persistencia de preferencia, URL y estado `PaymentUnknown`, enlazada con idempotencia de orden.
3. Añadir inbox de notificaciones: validar firma y tópico `payment`, persistir el aviso sin confiar en su estado y consultar `ReadVerifiedPayment`.
4. Aplicar el resultado verificado en la saga mediante una transición idempotente. Reembolsos, pagos inciertos y eventos fuera de orden requieren un caso de uso propio; no están implementados en este piloto.
5. Ejecutar pruebas externas con credenciales de prueba, incluida rotación de secreto, duplicados, timeout y reconciliación. Mantener el simulador como configuración predeterminada hasta verificarlas.

El proveedor recomienda Orders API para nuevas integraciones. Este piloto implementa Preferences API como demostración aislada del contrato de checkout alojado; antes de producción debe evaluarse la migración y disponibilidad en Colombia. No se declara equivalencia con una integración productiva certificada.

## Pruebas reproducibles

```powershell
dotnet run --project tests/PaymentChecks -- --report=tests/PaymentChecks/results.json
```

La suite compila directamente los archivos del adaptador y valida comportamiento HTTP, rechazo de vendedores reales, correlación del pago, destinos de checkout y firmas. No usa una copia de las reglas de producción ni necesita paquetes adicionales.

## Fuentes oficiales verificadas el 13 de noviembre de 2026

- [Cuentas de prueba y prefijo de las credenciales](https://www.mercadopago.com.co/developers/es/docs/checkout-pro-preferences/test-accounts)
- [Consulta de identidad y etiqueta test_user](https://developers.mercadolibre.com.co/consulta-usuarios)
- [Crear una preferencia](https://www.mercadopago.com.co/developers/en/reference/online-payments/checkout-pro-preferences/create-preference/post)
- [Notificaciones: firma, recurso y consulta autoritativa](https://www.mercadopago.com.co/developers/es/docs/checkout-pro-orders/resources/notifications/webhooks)
- [Manifest de firma HMAC](https://www.mercadopago.com.co/developers/es/docs/checkout-api-orders/optional-notifications)
- [Referencia y recomendación de Orders API](https://www.mercadopago.com.co/developers/es/reference/online-payments/checkout-pro-orders/overview)
