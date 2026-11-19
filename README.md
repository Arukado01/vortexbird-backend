# VortexBird Integration Platform — Backend

API .NET 10 con PostgreSQL para demostrar la interoperabilidad de AndesRetail. Los sistemas ERP, WMS, OMS, CRM y pagos predeterminados son **simuladores explícitos**. No procesa cobros reales ni declara SLOs medidos.

## Ejecutar

La opción recomendada es `docker compose` desde el repositorio hermano `vortexbird-infrastructure`. Para ejecución directa se requieren .NET10 SDK, PostgreSQL17+ y Redis opcional.

1. Define las variables de `.env.example` en el proceso. `.NET` no carga archivos `.env` automáticamente. Genera claves locales aleatorias; no uses los textos `REPLACE_*` como credenciales.
2. Configura `Database__Initialize=true`, `Seed__Enabled=true` y `ASPNETCORE_ENVIRONMENT=Development` para aplicar migraciones y datos iniciales. En producción ejecuta la imagen con `--migrate` como tarea separada; consulta [migraciones, roles de base y bootstrap OIDC](README-deployment.md).
3. Ejecuta `dotnet run --project src/VortexBird.API --no-launch-profile --urls http://localhost:5080`.
4. En Development, abre `/swagger` para consultar y ejecutar operaciones de la API. El documento JSON está en `/openapi/v1.json`. Con Compose, usa el puerto `API_PORT` de `vortexbird-infrastructure/.env`. `/health/ready` comprueba la conexión a PostgreSQL y rechaza despliegues con migraciones pendientes.
5. Usa el frontend o `scripts/Seed-Demo.ps1` para generar órdenes **a través de los casos de uso reales** y llenar los gráficos.

El seed crea un partner, dos canales, 48 productos, 96 posiciones de inventario, 96 precios, 31 clientes CRM y nueve roles. Identificadores: partner `11111111-1111-1111-1111-111111111111`, canal web `22222222-2222-2222-2222-222222222222`, tienda `BOG-01`, SKU `AND-0001`, cliente `CUST-001`. No se inventan transacciones para hacer que el dashboard parezca activo.

## ECS Fargate

El piloto AWS separa API y worker con la misma imagen. `Workers__Enabled=false` deja a la API escribir el outbox. El worker lo publica y consume con `Workers__Enabled=true` y su ECS Task Role. El argumento `--readiness-probe` verifica localmente la conexión PostgreSQL mediante `/health/ready`. Consulta [el plan de ECS](../vortexbird-infrastructure/terraform-ecs/README.md).

## Identidad

Para probar rutas protegidas en Swagger con el modo demo, ejecuta `POST /api/v1/auth/demo-login` con `DEMO_EMAIL` y `DEMO_PASSWORD` de `.env`. Copia el campo `accessToken` de la respuesta, pégalo en **Authorize** sin el prefijo `Bearer` y prueba `GET /api/v1/orders`. El token dura diez minutos.

Keycloak es el proveedor normal OIDC. El servicio valida firma, issuer, audience y expiración independientemente del gateway. `Authentication__Authority` puede usar DNS interno y `Authentication__ValidIssuer` debe coincidir con el issuer que ve el navegador. `Authentication__PublicAuthority` permite una dirección pública diferente para el portal. `Seed__AdminSubject` debe ser el `sub` del usuario Keycloak inicial. Alta, recuperación, cambio de contraseña, sesión y refresh rotation pertenecen a Keycloak; la tabla Users mantiene autorización/contexto de la aplicación, nunca contraseñas.

La autenticación alternativa de demostración requiere **Development**, `Authentication__DemoEnabled=true`, una clave de al menos32 caracteres y `Demo__Password` de al menos12. Está prohibida al arrancar en Production. No tiene refresh tokens propios: expira a los10minutos. El portal conserva el access token en memoria. Todos los tokens requieren usuario local habilitado; las cuentas OIDC se vinculan por `subject`, sin vinculación automática insegura por correo.

Los roles/permisos son dinámicos en PostgreSQL. Se consultan por request, de modo que una revocación no espera al vencimiento del JWT. Un token restringido por partner/canal intersecta los permisos de la cuenta; un claim malformado nunca convierte la sesión en global. Solo `identity_type=human` recibe RBAC de sesión; las demás identidades además necesitan scopes de acción. Las operaciones que agregan datos de todo un partner (webhooks, usuarios, auditoría y eventos) rechazan identidades limitadas a canales. No se permite editar/eliminar SUPER_ADMIN ni modificar usuarios protegidos desde las rutas generales.

## Flujo y persistencia

`POST /api/v1/orders` exige `Idempotency-Key` y devuelve202 con una orden `reserved`. Un lock transaccional PostgreSQL por partner+clave y un índice único evitan duplicados entre instancias. Se guarda hash del payload y respuesta inicial. Reutilizar la clave con otro cuerpo devuelve409; el replay conserva el identificador original. Ventana24h; las claves expiradas no se reciclan automáticamente.

El contrato de creación rechaza campos desconocidos. En Development con `Payments__Provider=Simulator`, `GET /api/v1/auth/config` anuncia `simulationEnabled` y habilita `POST /api/v1/simulations/orders` con `paymentOutcome` y `POST /api/v1/payments/{id}/simulate` para resolver pagos pendientes. Ambas rutas requieren autorización y alcance de partner. El OpenAPI público excluye las rutas de simulación. Revisa [el flujo de checkout y conciliación](README-payments.md).

El handler CQRS valida cliente mediante `ICustomerAdapter`/CRM, consulta precio con `IPricingAdapter`/ERP y reserva mediante `IInventoryAdapter`/WMS. Las actualizaciones condicionales del stock y los constraints evitan sobreventa. La reserva y outbox se confirman atómicamente. Los simuladores usan el mismo PostgreSQL y sus contratos externos se transforman dentro del ACL; **una integración real remota requiere la saga de reserva externa descrita en el assessment**, no compartir esta transacción SQL con ERP/WMS.

El worker procesa `order.created`, llama `IPaymentGateway` fuera de la transacción y persiste aprobación, rechazo o espera. Un pago aprobado consume la reserva; el rechazo libera inventario. Las reservas pendientes expiran a15min y se compensan. `order.confirmed`, despacho y entrega actualizan una réplica OMS persistida consultable en `/api/v1/integrations/oms/orders`. El CRM valida clientes sembrados, no acepta cualquier identificador.

El outbox usa `FOR UPDATE SKIP LOCKED`, leases con token de propiedad, reintentos exponenciales+jitter, cinco intentos y estado `dead_letter`. Los consumidores deduplican por evento+consumidor. Las entregas HTTP son at-least-once: el receptor debe deduplicar `X-Event-Id`. Una suscripción exige HTTPS/dominio público/puerto443; se valida DNS en la conexión para bloquear SSRF y no se siguen redirects. La firma es HMAC-SHA256 de `timestamp.payload`; el secreto se devuelve solo al crear la suscripción.

`Messaging__Provider=sqs` habilita publicación y consumo mediante AWSSDK.SQS, credenciales de runtime, SigV4 e inbox persistente. Solo se confirma el mensaje después del resultado durable; un evento debe coincidir con su outbox canónico. El proveedor predeterminado es `local`. Configuración, emulador, recuperación y límites están en `README-messaging.md`.

El proveedor opcional `MercadoPagoSandbox` guarda checkout e inbox, consulta el resultado autoritativo y concilia pagos duplicados o tardíos. El retorno del navegador no aprueba una orden. Las credenciales deben pertenecer a una cuenta de prueba verificada; `README-payments.md` describe habilitación, callbacks y compensaciones.

## API principal

| Recurso | Operaciones |
|---|---|
| `/auth/config`, `/auth/me`, `/auth/demo-login` | Configuración pública, sesión autorizada, login solo demo |
| `/partners`, `/channels`, `/products`, `/users`, `/roles`, `/permissions` | Listados paginados y administración autorizada |
| `/roles/{id}/permissions`, `/users/{id}/roles` | Asignaciones efectivas de permisos/roles |
| `/customers` | Clientes CRM por partner |
| `/inventory`, `/inventory/stock`, `/inventory/{id}` | Listado, consulta canónica, actualización validada |
| `/pricing`, `/pricing/quote`, `/pricing/{id}` | Listado, cotización efectiva por cliente, precio |
| `/orders`, `/orders/{id}`, `/orders/{id}/cancel`, `/orders/{id}/status` | Crear/consultar, compensar, despacho/entrega |
| `/payments` | Consultar transacciones |
| `/payments/orders/{orderId}/checkout`, `/payments/orders/{orderId}/reconcile` | Consultar checkout y solicitar conciliación sandbox autorizada |
| `/payments/inbox`, `/payments/inbox/{id}/retry` | Revisar y reintentar notificaciones fallidas con autorización global |
| `/payments/mercadopago/webhook` | Callback firmado, limitado y persistido antes de confirmar recepción |
| `/integrations`, `/integrations/{id}/test`, `/integrations/oms/orders` | Estado, fallos controlados y réplica OMS |
| `/events`, `/events/{id}/retry`, `/webhooks`, `/webhook-deliveries` | Outbox, DLQ y suscripciones |
| `/activity-logs`, `/dashboard`, `/settings` | Auditoría filtrada, agregaciones SQL y configuración de lectura |

Listas: `page`, `pageSize` (10 por defecto; máximo100), `search` donde aplica; respuesta `items,page,pageSize,totalCount,totalPages,hasNextPage,hasPreviousPage`. Filtros específicos constan en OpenAPI. Las rutas de actualización validan DTOs y no permiten modificar IDs ni secretos mediante mass assignment.

## Caché y operación

`/inventory/stock` usa Redis TTL10s; `/pricing/quote` TTL60s con clave por partner+canal+SKU+cliente. Crear una orden consulta las fuentes autoritativas sin confiar en stock/precio cacheados. Redis no disponible provoca fallback a PostgreSQL; no devuelve stock inventado. El cliente RESP2 valida longitudes y UTF-8, admite `rediss://`, AUTH y selección de base; fuera de Development exige TLS y credenciales. No omite la validación del certificado. Pooling y validación real con ElastiCache siguen pendientes. Los bulkheads limitan16 llamadas simultáneas por adaptador y el circuito abre30s tras tres fallos.

Los logs JSON incluyen correlation-id/request-id y duración; se crean `ActivitySource` y `Meter` para instrumentación. Auditoría append-only desde todas las sobrecargas SaveChanges y sin cuerpos, tokens o credenciales. Un límite local protege login/API. Además, los presupuestos configurables de partner/canal se consumen atómicamente en PostgreSQL usando el reloj de DB y un lock por partner. Las consultas agregadas consumen sus canales autorizados; un error DB no deshabilita las cuotas. Falta medir concurrencia y contención entre réplicas.

## Pruebas

```sh
dotnet restore VortexBird.sln
dotnet build VortexBird.sln --no-restore
dotnet run --project tests/VortexBird.DomainChecks --no-build
dotnet run --project tests/VortexBird.Tests --no-build
```

Se comprobaron24 invariantes de dominio/validación y21 de arquitectura, autorización, idempotencia, índices/FKs y SSRF. Las verificaciones HTTP adicionales usan TestServer sin red y mantienen la validación real JWT. `tests/Integration.ps1` prueba API+PostgreSQL, replay, conflicto de payload y compensación; requiere una base dedicada. CI levanta PostgreSQL y ejecuta ese escenario. No se presenta una prueba contra un servidor PostgreSQL inaccesible como aprobada. Los resultados ampliados están en el repositorio de documentación.

La revisión anterior pasó también 50 controles de seguridad, 26 de API/JWT/callbacks, 61 de pagos, 17 de cuotas, 32 de Redis, 20 de mensajería y nueve del cliente oficial AWSSDK.SQS. Son 260 comprobaciones backend con transporte controlado o sin red; no equivalen a probar proveedores desplegados. `../verify.ps1` ejecuta estas suites y las 15 comprobaciones frontend.

En el entorno restringido actual se restauraron dependencias desde el cache disponible; `dotnet build -m:1 -nr:false -p:UseSharedCompilation=false` evita problemas de build-server. Si el directorio personal no es escribible, define `DOTNET_CLI_HOME` en un directorio de trabajo.

## Estado y límites explícitos

- PostgreSQL, EF, migraciones, autorización, CQRS, saga, outbox/DLQ local, ACLs persistidos y portal están implementados. Entornos/proveedores externos no se presentan como conectados.
- El transporte local y el opcional SQS están implementados. El consumidor SQS con PostgreSQL externo, ElasticMQ y AWS todavía requiere verificación integrada.
- No se descargaron Serilog, FluentValidation ni OpenTelemetry SDK por restricciones de red; se usan logging JSON, validadores de aplicación y Activity/Meter de .NET. No se afirma haber instalado esas librerías.
- El checkout Mercado Pago sandbox está integrado en código, pero requiere credenciales verificadas de prueba y una URL HTTPS de callbacks. El simulador y el HTTP controlado no equivalen a validación externa. Consulta `README-payments.md`.
- Los secretos de webhooks están restringidos en API pero aún requieren cifrado en reposo por aplicación/KMS antes de producción. No usar credenciales reales en esta demo.
- No se han medido 99.9%, p95<300ms ni tráfico10x. Los escenarios de carga y el plan AWS se entregan para ejecutarlos en un entorno habilitado.
- Los workflows de despliegue requieren OIDC IAM, repositorios ECR, cluster y environment GitHub configurados. Ningún workflow de este árbol autoriza por sí mismo crear infraestructura facturable.

Convenciones: `feat:`, `fix:`, `docs:`, `test:`, `chore:`. Ramas `main`, `develop`, `feature/*`, `fix/*`, `hotfix/*`. Revisar migraciones y rollback antes de integrar cambios de esquema.

## Validación integral del 17 de noviembre de 2026

Se recompiló la solución sin errores ni advertencias y se ejecutaron nueve suites locales: 24 controles de dominio, 21 de arquitectura/modelo, 50 de seguridad, 26 de límites HTTP/JWT, 61 de pagos, 17 de cuotas, 32 de caché, 20 de mensajería y 9 del transporte SQS. Los 260 controles pasaron. La evidencia actual está en `test-results/backend-current.json` y `../qa/backend-validation-2026-11-17.json`.

Los listados administrativos y operativos usan 10 registros por defecto. `/settings` informa el proveedor de pagos registrado en runtime, y `/health/ready` devuelve 503 si PostgreSQL no está accesible o quedan migraciones pendientes. Estas correcciones compilan; su recorrido HTTP sobre una base real requiere levantar el entorno descrito a continuación.

El motor PostgreSQL nativo se inició y registró `database system is ready to accept connections` en `127.0.0.1:55432`. Una consulta posterior con `pg_ctl status` devolvió `no server running`, por lo que no se acredita un servicio persistente. El cliente oficial `psql` no pudo abrir esa conexión desde la sesión restringida: `Permission denied (10013)`. No se crearon bases ni se ejecutaron migraciones sobre los datos existentes. No se da por superada la integración API/PostgreSQL, la concurrencia real, la carga ni el recorrido local completo en esta ejecución. Ejecutar el arranque nativo o Docker desde una sesión de usuario con acceso normal a localhost, crear una base dedicada y correr `tests/Integration.ps1`; la prueba verifica replay concurrente, conflicto de payload y compensación de la saga. No contiene pagos reales.
También se corrigió por revisión de código una carrera en la resolución de pagos simulados: la autorización precargaba entidades antes del lock PostgreSQL, y `Resolve` podía reutilizar ese estado anterior. Ahora limpia el seguimiento y vuelve a leer bajo el lock. `tests/Integration.ps1` incluye ocho resoluciones concurrentes y exige una sola liquidación y siete conflictos, además de una única transición `confirmed`; este escenario requiere PostgreSQL y no se declara ejecutado en la sesión bloqueada.
## Preparación de despliegue: migrator y bootstrap OIDC

La imagen incorpora un job `--migrate` separado del servidor y un bootstrap inicial `--bootstrap-admin` explícito. El migrator usa credenciales PostgreSQL distintas de API/worker, comprueba que runtime carece de propiedad/administración y aplica permisos por tabla: auditoría append-only e historial EF solo lectura. El bootstrap requiere el `sub` real y un issuer HTTPS coincidente, no crea contraseñas ni usa el seed demo. Detalles, variables, códigos de salida y limitaciones están en [README-deployment.md](README-deployment.md).

API, worker y job exigen TLS `VerifyFull` fuera del entorno Development local o del servicio `postgres` del Compose de desarrollo. La imagen incluye el bundle CA oficial de RDS descargado durante build. Esta sesión no verificó el build Docker, la descarga del bundle ni un handshake RDS: la descarga directa quedó bloqueada por DNS.