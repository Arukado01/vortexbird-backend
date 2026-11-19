# Migraciones y primera identidad OIDC

La imagen de backend admite dos modos operativos. Sin argumentos inicia la API y los workers configurados; con `--migrate` o `--bootstrap-admin` ejecuta un trabajo finito que **no inicia HTTP, workers, autenticación demo ni datos de demostración**.

## Contrato del trabajo ECS

Usar la misma imagen por digest que API y worker. El entrypoint continúa siendo `dotnet VortexBird.API.dll`.

| Operación | `command` ECS | Resultado |
|---|---|---|
| Release normal | `["--migrate"]` | Migra EF y aplica los permisos runtime revisados. |
| Primera instalación | `["--migrate", "--bootstrap-admin"]` | Migra y vincula el primer administrador OIDC explícito. |
| Bootstrap sobre esquema vigente | `["--bootstrap-admin"]` | Falla si existen migraciones pendientes. |

Exit codes: `0` completado; `2` configuración inválida; `1` fallo de conexión, migración, permisos, bootstrap o timeout. El límite del trabajo es diez minutos. Un lock de sesión PostgreSQL evita dos trabajos simultáneos; las migraciones usan además las transacciones de EF. Un fallo posterior a una migración no revierte automáticamente cambios de esquema ya confirmados. El release debe detenerse y conservar los task definitions anteriores. El rollback de servicios no es un downgrade automático de la base: usar migraciones compatibles y recuperación revisada.

Variables del job:

| Variable | Valor/origen |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ConnectionStrings__Database` | Secrets Manager: conexión del **migrator**, nunca la del master RDS ni la de runtime. |
| `Database__RuntimeRoles__0` | Nombre del rol PostgreSQL de API, existente. |
| `Database__RuntimeRoles__1` | Nombre del rol PostgreSQL del worker, existente. Admite 1–8 roles distintos. |
| `Authentication__DemoEnabled` | `false` |
| `Database__Initialize` | `false` |
| `Seed__Enabled` | `false` |
| `Workers__Enabled` | `false`; el job no registra workers de todas formas. |

Para el primer administrador añadir `Bootstrap__AdminSubject`, `Bootstrap__AdminEmail`, `Bootstrap__AdminDisplayName`, `Bootstrap__OidcIssuer` y `Authentication__ValidIssuer`. El issuer debe ser HTTPS y coincidir exactamente entre ambos valores (se tolera solo la barra final). `AdminSubject` es el `sub` real de la cuenta en el IdP; **no se obtiene ni se adivina desde el correo**. El job no crea la cuenta Keycloak, no define contraseña, no llama al IdP y no verifica por sí solo que la cuenta pueda iniciar sesión. Esa prueba debe realizarse después del despliegue.

Bootstrap crea únicamente el rol protegido `SUPER_ADMIN`, el catálogo de permisos y el primer usuario global. No crea catálogo comercial, partners, pedidos ni otros usuarios. Solo permite repetir la operación para el mismo administrador habilitado, global y con correo concordante. Rechaza otra identidad, un administrador previo distinto, promoción de un usuario existente, reactivación o reasignación de subject/correo. Los permisos del administrador protegido se amplían únicamente con el catálogo de permisos conocido por esta versión. La cuenta `demo-admin` está prohibida en este flujo.

## PostgreSQL: propietario de migración y runtime

`scripts/provision-postgres.sql` es una operación inicial de DBA para una **base nueva dedicada**. Se detiene antes de modificar objetos si los roles o la base ya existen. No contiene credenciales. Ejecutar desde un cliente PostgreSQL 17 autorizado con `psql -X -f scripts/provision-postgres.sql`; la conexión administrativa se proporciona con las variables estándar `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, `PGDATABASE`, `PGSSLMODE=verify-full` y `PGSSLROOTCERT` apuntando al bundle CA autorizado. El script no debe ejecutarse como parte de cada release.

Configurar en el proceso: `VORTEXBIRD_DATABASE_NAME`, `VORTEXBIRD_MIGRATOR_ROLE`, `VORTEXBIRD_API_ROLE`, `VORTEXBIRD_WORKER_ROLE` y sus contraseñas aleatorias independientes de al menos 32 caracteres: `VORTEXBIRD_MIGRATOR_PASSWORD`, `VORTEXBIRD_API_PASSWORD`, `VORTEXBIRD_WORKER_PASSWORD`. No pasarlas mediante argumentos `-v`, archivos versionados ni salidas de consola. Al terminar, guardar las conexiones en secretos separados y eliminar esas variables de la sesión.

El script crea los tres roles, la base cuyo dueño es migrator y retira permisos públicos solo de esa base nueva. API y worker no reciben propiedad, membresías ni administración del cluster. Si el aprovisionamiento falla después de crear roles, exige revisión del estado parcial; no borra objetos ni reintenta sobre recursos existentes.

El Terraform actual crea inicialmente una base `vortexbird` con propietario master RDS. Esa base no satisface el contrato del migrator. Para mantenerla intacta, aprovisionar una base nueva, por ejemplo `VORTEXBIRD_DATABASE_NAME=vortexbird_app`, y usar `Database=vortexbird_app` en los tres secretos. No apuntar el job a la base inicial ni transferir su propiedad automáticamente.

El job exige que migrator sea el propietario dedicado de la base sin `SUPERUSER`, `CREATEDB`, `CREATEROLE`, `REPLICATION` ni `BYPASSRLS`. Runtime no puede ser propietario de base/esquema/tablas, tener membresías, crear objetos ni usar tablas temporales. Valida los permisos efectivos, incluidos `PUBLIC` y grants de columnas. No ejecuta `GRANT ... ON ALL TABLES`, no concede secuencias y falla si el modelo EF incorpora tablas/esquemas/secuencias que no tengan una política revisada.

| Datos | Permiso runtime |
|---|---|
| Tablas del modelo | SELECT; INSERT/UPDATE solo donde los flujos actuales lo necesitan. |
| `Roles`, `UserRoles`, `RolePermissions` | DELETE para retirar roles/asignaciones. |
| `ActivityLogs` | SELECT e INSERT; sin UPDATE, DELETE ni TRUNCATE. |
| `__EFMigrationsHistory` | Solo SELECT para readiness. |
| Clientes CRM | Solo SELECT en runtime; carga externa mediante proceso administrativo separado. |
| Vistas, secuencias y objetos fuera del modelo | Sin grants automáticos. |

## TLS con RDS

API, worker y job exigen `SSL Mode=VerifyFull` fuera de Development sobre localhost o el servicio `postgres` del Compose local. Para RDS, el Dockerfile descarga el [bundle CA oficial](https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem), lo incluye como `/app/certs/aws-rds-global-bundle.pem` y registra su SHA-256 durante el build. La imagen desplegada por digest conserva esos bytes; reconstruir y validar una nueva imagen cuando AWS rote certificados. Una descarga fallida detiene el build.

Las conexiones RDS deben incluir `SSL Mode=VerifyFull;Root Certificate=/app/certs/aws-rds-global-bundle.pem`. Si no se especifica Root Certificate, la política usa esa ruta para endpoints `.rds.amazonaws.com` y falla si falta el archivo. Para DNS personalizados, especificar explícitamente el certificado raíz adecuado. No existe fallback a `TrustServerCertificate` ni a TLS sin verificación de hostname.

La inclusión del bundle no prueba la conexión a RDS. Esta sesión no pudo descargarlo (DNS restringido), construir Docker ni verificar un handshake AWS. El runner autorizado debe construir la imagen, conservar el hash del bundle y ejecutar la tarea con TLS completo. Referencia de permisos efectivos: [PostgreSQL 17](https://www.postgresql.org/docs/17/functions-info.html).

## Verificación y límite actual

Los controles sin red validan configuración, aislamiento de roles, política de grants y reglas de bootstrap. No sustituyen ejecutar `provision-postgres.sql`, migrar una base real con roles separados, repetir bootstrap, comprobar la denegación de UPDATE/DELETE de auditoría desde runtime y autenticar al administrador en el IdP. Estos pasos siguen siendo una puerta del despliegue; no se declaran realizados por existir este código.

## Aceptación local con base separada

Desde el repositorio backend puede ejecutarse `pwsh -File scripts/Invoke-LocalBusinessChecks.ps1 -EnvFile ../vortexbird-infrastructure/.env -PostgresPort 55433 -ApiPort 5089`. El lanzador principal puede invocarlo con `-RunBusinessTests` después de iniciar los servicios. Requiere backend compilado en Release, PowerShell 7 y PostgreSQL nativo disponible con permiso para crear una base.

El helper crea exclusivamente una nueva base `vortexbird_acceptance_<fechaUTC>_<identificador>`, usa `127.0.0.1`, fuerza pagos simulados/mensajería local, desactiva Redis para este escenario y restaura las variables del proceso al finalizar. No selecciona la base de la plataforma ni elimina la base de prueba. Comprueba idempotencia concurrente, conflicto de payload, compensación, ocho resoluciones simultáneas con una sola liquidación y agregación del dashboard. Guarda un reporte JSON sin contraseñas y conserva la base incluso al fallar. La integración directa acepta únicamente `vortexbird_ci`, `vortexbird_test_*` o `vortexbird_acceptance_*` en loopback.

En esta sesión solo se comprobaron sintaxis y rechazos preventivos de destinos inseguros; la ejecución del helper contra PostgreSQL continúa pendiente.