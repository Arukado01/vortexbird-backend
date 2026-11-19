# Mensajería: outbox, inbox y SQS

Hay dos transportes ejecutables: `Messaging__Provider=local` (predeterminado) y `sqs`. Ambos usan la misma ruta de negocio y un inbox PostgreSQL persistido. AWS no fue invocado ni provisionado durante estas pruebas.

## Flujo

1. La transacción de la orden escribe el outbox.
2. El dispatcher reclama una fila con `FOR UPDATE SKIP LOCKED`, token de lease y vencimiento. Publica fuera de la transacción.
3. En modo local llama al consumidor; en modo SQS, publica y marca `published` únicamente cuando recibe `MessageId`.
4. El consumidor verifica el evento contra el outbox de confianza **antes** de reservar el identificador en el inbox, evitando envenenamiento mediante IDs válidos con contenido falso.
5. El inbox tiene PK `(EventId,Consumer)`, hash del contenido, intentos, estado y lease. Un evento completado no repite efectos; una ejecución concurrente conserva el mensaje para reintento.
6. La saga/adaptadores procesan el evento. Tras persistir la finalización y reconciliar el outbox a `processed`, se confirma SQS con el `ReceiptHandle` de esa recepción.
7. Un fallo de ack puede causar redelivery, pero el inbox completado evita repetir el caso de uso. Un worker con lease perdido no puede sobrescribir el estado del propietario nuevo ni confirmar su entrega.

La entrega externa de webhooks sigue siendo *at-least-once*: sus receptores deben deduplicar `X-Event-Id`. No se promete exactamente una ejecución de un efecto HTTP externo si el proceso cae después de enviarlo y antes de guardar su confirmación.

Los mensajes inválidos o fallidos nunca se eliminan silenciosamente. Se cambia su visibilidad con backoff. La política de redrive de la cola debe enviarlos a una DLQ después de cinco recepciones; el inbox además detiene efectos tras `MaxAttempts`. El endpoint administrativo de replay debe restablecer el inbox `dead_letter` y el outbox dentro de una transacción, sin borrar finalizaciones exitosas.

Un timeout de publicación es ambiguo: SQS podría haber aceptado el mensaje. La publicación puede repetirse y el inbox deduplica; la finalización del consumidor prevalece sobre cualquier estado previo de reintento/DLQ del productor. Si una cola FIFO se utiliza, la clave de deduplicación es EventId; una republicación inmediata puede ser suprimida por la ventana de deduplicación de SQS. Para replay operativo inmediato, usar cola estándar o respetar esa ventana.

## SDK y protocolo

La infraestructura utiliza `AWSSDK.SQS` 4.0.100.26, que resuelve `AWSSDK.Core` 4.0.103. `AmazonSQSClient` serializa y firma SendMessage, ReceiveMessage, DeleteMessage y ChangeMessageVisibility. `IQueueTransport` mantiene a los casos de uso independientes del SDK. El adaptador valida el envelope y procesa una entrega por lectura, con polling de 20 s y un timeout de 30 s.

La URL en AWS sigue el patrón `https://sqs.<región>.amazonaws.com/<cuenta>/<cola>`. La configuración de producción usa el endpoint regional del SDK. `ServiceUrl` se admite en Development para localhost, 127.0.0.1 o elasticmq con el mismo origen que la cola, sin query, credenciales ni fragmentos. El emulador recibe credenciales locales de prueba.

### Identidad AWS

El piloto ECS asigna al Task Role del worker las cuatro acciones sobre la cola de eventos. El Execution Role inyecta solo el secreto del worker. El SDK obtiene credenciales temporales de contenedor mediante la cadena de proveedores. API y frontend tienen Task Roles separados y carecen de permisos sobre SQS.

EKS conserva Pod Identity como alternativa. IRSA requiere además AWSSDK.SecurityToken y una validación propia, pendiente para ese entorno.

## Configuración

```text
Messaging__Provider=sqs
Messaging__Region=us-east-2
Messaging__QueueUrl=https://sqs.us-east-2.amazonaws.com/<ACCOUNT_ID>/<QUEUE_NAME>
Messaging__VisibilityTimeoutSeconds=120
Messaging__MaxAttempts=5
```

Para ElasticMQ local:

```text
ASPNETCORE_ENVIRONMENT=Development
Messaging__ServiceUrl=http://elasticmq:9324
Messaging__QueueUrl=http://elasticmq:9324/000000000000/vortexbird-events
```

`AddIntegrationMessaging(configuration, environment)` registra los servicios; `Workers__Enabled=false` conserva los componentes pero no inicia polling. La integración de modelo es `MessagingModelConfiguration.ConfigureMessaging(modelBuilder)`. Aplicar la migración antes de encender los workers. El dispatcher reclama un mensaje por lease; continúa con una pausa de10ms cuando encuentra trabajo y espera2s solo cuando no hay, evitando imponer artificialmente0.5eventos/s. La limpieza de reservas ocurre cada5s. No se ha medido capacidad bajo carga.

## Verificación

`tests/MessagingChecks`: veinte verificaciones de orquestación sin red: ack posterior a persistencia, duplicados, leases, fallos de handler/ack, poison messages, tamaño UTF-8 y metadatos EF. `tests/SqsTransportChecks` ejecuta el SDK de servicio oficial con un HTTP handler de prueba; sus resultados deben leerse en el informe actualizado. Para verificar también el protocolo en un proceso ElasticMQ dedicado y enlazado exclusivamente a loopback:

`powershell
dotnet run --project tests/SqsTransportChecks -- --elasticmq=http://127.0.0.1:19324
`

La prueba crea una cola y una DLQ efímeras. Comprueba visibilidad y reentrega, acuse, agotamiento y republicación controlada. El emulador no acredita StartMessageMoveTask, permisos IAM ni persistencia PostgreSQL del consumidor.

Fuentes oficiales consultadas:

- https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html
- https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html
- https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/ReceiveMessage.html
- https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/SQS/TDeleteMessageRequest.html
