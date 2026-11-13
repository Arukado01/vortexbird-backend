# VortexBird Integration Platform — Backend

API .NET 10 con PostgreSQL para demostrar la interoperabilidad de AndesRetail. Los sistemas ERP, WMS, OMS, CRM y pagos predeterminados son **simuladores explícitos**. No procesa cobros reales ni declara SLOs medidos.

## Ejecutar

La opción recomendada es `docker compose` desde el repositorio hermano `vortexbird-infrastructure`. Para ejecución directa se requieren .NET10 SDK, PostgreSQL17+ y Redis opcional.

1. Define las variables de `.env.example` en el proceso. `.NET` no carga archivos `.env` automáticamente. Genera claves locales aleatorias; no uses los textos `REPLACE_*` como credenciales.
2. Configura `Database__Initialize=true`, `Seed__Enabled=true` y `ASPNETCORE_ENVIRONMENT=Development` para aplicar migraciones y datos iniciales. En producción ejecuta el script de migración revisado como una tarea separada.
3. Ejecuta `dotnet run --project src/VortexBird.API --no-launch-profile --urls http://localhost:5080`.
4. OpenAPI: `http://localhost:5080/openapi/v1.json` (solo Development). Liveness `/health/live`; readiness `/health/ready` comprueba PostgreSQL.
5. Usa el frontend o `scripts/Seed-Demo.ps1` para generar órdenes **a través de los casos de uso reales** y llenar los gráficos.

El seed crea un partner, dos canales, 48 productos, 96 posiciones de inventario, 96 precios, 31 clientes CRM y nueve roles. Identificadores: partner `11111111-1111-1111-1111-111111111111`, canal web `22222222-2222-2222-2222-222222222222`, tienda `BOG-01`, SKU `AND-0001`, cliente `CUST-001`. No se inventan transacciones para hacer que el dashboard parezca activo.

## Identidad

Keycloak es el proveedor normal OIDC. El servicio valida firma, issuer, audience y expiración independientemente del gateway. `Authentication__Authority` puede usar DNS interno y `Authentication__ValidIssuer` debe coincidir con el issuer que ve el navegador. `Authentication__PublicAuthority` permite una dirección pública diferente para el portal. `Seed__AdminSubject` debe ser el `sub` del usuario Keycloak inicial. Alta, recuperación, cambio de contraseña, sesión y refresh rotation pertenecen a Keycloak; la tabla Users mantiene autorización/contexto de la aplicación, nunca contraseñas.

La autenticación alternativa de demostración requiere **Development**, `Authentication__DemoEnabled=true`, una clave de al menos32 caracteres y `Demo__Password` de al menos12. Está prohibida al arrancar en Production. No tiene refresh tokens propios: expira a los10minutos. El portal conserva el access token en memoria. Todos los tokens requieren usuario local habilitado; las cuentas OIDC se vinculan por `subject`, sin vinculación automática insegura por correo.

Los roles/permisos son dinámicos en PostgreSQL. Se consultan por request, de modo que una revocación no espera al vencimiento del JWT. Un token restringido por partner/canal intersecta los permisos de la cuenta; un claim malformado nunca convierte la sesión en global. Solo `identity_type=human` recibe RBAC de sesión; las demás identidades además necesitan scopes de acción. Las operaciones que agregan datos de todo un partner (webhooks, usuarios, auditoría y eventos) rechazan identidades limitadas a canales. No se permite editar/eliminar SUPER_ADMIN ni modificar usuarios protegidos desde las rutas generales.

## Flujo y persistencia

`POST /api/v1/orders` exige `Idempotency-Key` y devuelve202 con una orden `reserved`. Un lock transaccional PostgreSQL por partner+clave y un índice único evitan duplicados entre instancias. Se guarda hash del payload y respuesta inicial. Reutilizar la clave con otro cuerpo devuelve409; el replay conserva el identificador original. Ventana24h; las claves expiradas no se reciclan automáticamente.

El handler CQRS valida cliente mediante `ICustomerAdapter`/CRM, consulta precio con `IPricingAdapter`/ERP y reserva mediante `IInventoryAdapter`/WMS. Las actualizaciones condicionales del stock y los constraints evitan sobreventa. La reserva y outbox se confirman atómicamente. Los simuladores usan el mismo PostgreSQL y sus contratos externos se transforman dentro del ACL; **una integración real remota requiere la saga de reserva externa descrita en el assessment**, no compartir esta transacción SQL con ERP/WMS.

El worker procesa `order.created`, llama `IPaymentGateway` fuera de la transacción y persiste aprobación, rechazo o espera. Un pago aprobado consume la reserva; el rechazo libera inventario. Las reservas pendientes expiran a15min y se compensan. `order.confirmed`, despacho y entrega actualizan una réplica OMS persistida consultable en `/api/v1/integrations/oms/orders`. El CRM valida clientes sembrados, no acepta cualquier identificador.

El outbox usa `FOR UPDATE SKIP LOCKED`, leases con token de propiedad, reintentos exponenciales+jitter, cinco intentos y estado `dead_letter`. Los consumidores deduplican por evento+consumidor. Las entregas HTTP son at-least-once: el receptor debe deduplicar `X-Event-Id`. Una suscripción exige HTTPS/dominio público/puerto443; se valida DNS en la conexión para bloquear SSRF y no se siguen redirects. La firma es HMAC-SHA256 de `timestamp.payload`; el secreto se devuelve solo al crear la suscripción.

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
| `/payments`, `/payments/{id}/simulate` | Consultar y resolver pagos pendientes del simulador |
| `/integrations`, `/integrations/{id}/test`, `/integrations/oms/orders` | Estado, fallos controlados y réplica OMS |
| `/events`, `/events/{id}/retry`, `/webhooks`, `/webhook-deliveries` | Outbox, DLQ y suscripciones |
| `/activity-logs`, `/dashboard`, `/settings` | Auditoría filtrada, agregaciones SQL y configuración de lectura |

Listas: `page`, `pageSize` (máximo100), `search` donde aplica; respuesta `items,page,pageSize,totalCount,totalPages,hasNextPage,hasPreviousPage`. Filtros específicos constan en OpenAPI. Las rutas de actualización validan DTOs y no permiten modificar IDs ni secretos mediante mass assignment.

## Caché y operación

`/inventory/stock` usa Redis TTL10s; `/pricing/quote` TTL60s con clave por partner+canal+SKU+cliente. Crear una orden consulta las fuentes autoritativas sin confiar en stock/precio cacheados. Redis no disponible provoca fallback a PostgreSQL; no devuelve stock inventado. El cliente RESP incluido es mínimo para la demo TCP local; sustituir por un cliente Redis mantenido con TLS/pooling/telemetría antes de conectar ElastiCache productivo. Los bulkheads limitan16 llamadas simultáneas por adaptador y el circuito abre30s tras tres fallos.

Los logs JSON incluyen correlation-id/request-id y duración; se crean `ActivitySource` y `Meter` para instrumentación. Auditoría append-only desde todas las sobrecargas SaveChanges y sin cuerpos, tokens o credenciales. El rate limiter en proceso protege login/API y aplica `RequestsPerMinute` del partner habilitado, compartiendo cuota entre sus identidades y limitando también canales del token. Los límites distribuidos entre réplicas deben completarse en gateway/Redis antes de escalar horizontalmente.

## Pruebas

```sh
dotnet restore VortexBird.sln
dotnet build VortexBird.sln --no-restore
dotnet run --project tests/VortexBird.DomainChecks --no-build
dotnet run --project tests/VortexBird.Tests --no-build
```

Se comprobaron24 invariantes de dominio/validación y19 de arquitectura, autorización, idempotencia, índices/FKs y SSRF. Las verificaciones HTTP adicionales usan TestServer sin red y mantienen la validación real JWT. `tests/Integration.ps1` prueba API+PostgreSQL, replay, conflicto de payload y compensación; requiere una base dedicada. CI levanta PostgreSQL y ejecuta ese escenario. No se presenta una prueba contra un servidor PostgreSQL inaccesible como aprobada. Los resultados ampliados están en el repositorio de documentación.

En el entorno restringido actual se restauraron dependencias desde el cache disponible; `dotnet build -m:1 -nr:false -p:UseSharedCompilation=false` evita problemas de build-server. Si el directorio personal no es escribible, define `DOTNET_CLI_HOME` en un directorio de trabajo.

## Estado y límites explícitos

- PostgreSQL, EF, migraciones, autorización, CQRS, saga, outbox/DLQ local, ACLs persistidos y portal están implementados. Entornos/proveedores externos no se presentan como conectados.
- La cola ejecutable predeterminada es el outbox PostgreSQL. SQS es el objetivo AWS, aún necesita transporte desplegado/verificación real.
- No se descargaron Serilog, FluentValidation ni OpenTelemetry SDK por restricciones de red; se usan logging JSON, validadores de aplicación y Activity/Meter de .NET. No se afirma haber instalado esas librerías.
- Mercado Pago requiere credenciales verificadas de prueba y una URL HTTPS de callbacks; el simulador no equivale a validación con el proveedor. Consulta la documentación del adaptador sandbox si está incluida.
- Los secretos de webhooks están restringidos en API pero aún requieren cifrado en reposo por aplicación/KMS antes de producción. No usar credenciales reales en esta demo.
- No se han medido 99.9%, p95<300ms ni tráfico10x. Los escenarios de carga y el plan AWS se entregan para ejecutarlos en un entorno habilitado.
- Los workflows de despliegue requieren OIDC IAM, repositorios ECR, cluster y environment GitHub configurados. Ningún workflow de este árbol autoriza por sí mismo crear infraestructura facturable.

Convenciones: `feat:`, `fix:`, `docs:`, `test:`, `chore:`. Ramas `main`, `develop`, `feature/*`, `fix/*`, `hotfix/*`. Revisar migraciones y rollback antes de integrar cambios de esquema.
