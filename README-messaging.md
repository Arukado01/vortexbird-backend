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

`AWSSDK.Core 4.0.102.19` proporciona `AmazonServiceClient`, cadena de credenciales, firma SigV4, transporte HTTP, clock-skew y reintentos. El código del proyecto únicamente serializa las cuatro operaciones JSON documentadas: SendMessage, ReceiveMessage, DeleteMessage y ChangeMessageVisibility. **No contiene una implementación propia de SigV4.** El paquete específico `AWSSDK.SQS` no estaba disponible en el cache y su descarga fue bloqueada; reemplazar este pequeño binding por el cliente de servicio oficial es una evolución compatible con `IQueueTransport`.

La URL productiva debe ser `https://sqs.<región>.amazonaws.com/<cuenta de 12 dígitos>/<cola>`, concordante con la región configurada. Se rechazan redirects, userinfo y destinos arbitrarios. La respuesta y el envelope tienen límites de bytes. El `ServiceUrl` de emulación solo se admite en Development, por HTTP, para localhost/127.0.0.1/elasticmq y con el mismo origen que QueueUrl. El cliente local usa credenciales dummy dedicadas y nunca resuelve credenciales AWS reales del equipo.

### Credenciales EKS

La cadena oficial admite credenciales temporales del runtime. **IRSA requiere además `AWSSDK.SecurityToken`, ausente en este entorno**: se verificó el fallo de carga del assembly antes de cualquier solicitud. Configurar EKS Pod Identity/credenciales de contenedor con el agente oficial es una alternativa; debe validarse en AWS después de aprobar el despliegue. No declarar IRSA operativo sin incluir y probar el paquete STS oficial. No introducir claves AWS permanentes en Git ni variables del frontend.

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

`tests/MessagingChecks`: veinte verificaciones de orquestación sin red: ack posterior a persistencia, duplicados, leases, fallos de handler/ack, poison messages, tamaño UTF-8 y metadatos EF. `tests/SqsTransportChecks` verifica el pipeline real AWS Core con un HTTP handler de prueba; sus resultados deben leerse en el informe actualizado. Las pruebas sin sockets no equivalen a una ejecución contra SQS, ElasticMQ o PostgreSQL conectado por Npgsql.

Fuentes oficiales consultadas:

- https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html
- https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html
- https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/ReceiveMessage.html
- https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/SQS/TDeleteMessageRequest.html
