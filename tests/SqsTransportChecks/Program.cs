using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using VortexBird.Infrastructure.Messaging;

var results = new List<object>();
var failures = 0;
async Task Check(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, status = "failed", error = ex.GetType().Name + ": " + ex.Message }); Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
void True(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
const string queueUrl = "https://sqs.us-east-2.amazonaws.com/123456789012/qa-events";
var options = new MessagingOptions { Provider = "sqs", Region = "us-east-2", QueueUrl = queueUrl, VisibilityTimeoutSeconds = 120 };
var message = new IntegrationEnvelope(Guid.NewGuid(), Guid.NewGuid(), "order.created", 1, "{\"orderId\":\"12345678-1234-1234-1234-123456789012\",\"label\":\"café\"}", "qa-trace", DateTimeOffset.Parse("2026-11-13T00:00:00Z"));
var emulatorUrl = args.FirstOrDefault(arg => arg.StartsWith("--elasticmq=", StringComparison.Ordinal))?[12..];
var emulatorOnly = args.Contains("--emulator-only", StringComparer.Ordinal);
if (emulatorOnly && emulatorUrl is null) throw new ArgumentException("--emulator-only requires --elasticmq=<loopback origin>.");
if (emulatorOnly) return await FinishChecks();

AmazonSQSClient Client(FixtureHandler handler)
{
    var config = MessagingRegistration.BuildClientConfig(options, development: false);
    config.MaxErrorRetry = 0;
    config.HttpClientFactory = new FixtureHttpClientFactory(handler);
    return new AmazonSQSClient(new SessionAWSCredentials("QA-ACCESS-KEY-NOT-REAL", "qa-secret-not-a-credential", "qa-session-not-a-credential"), config);
}
var body = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var bodyMd5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
using var handler = new FixtureHandler([
    (HttpStatusCode.OK,"{\"MessageId\":\"aws-message-1\"}"),
    (HttpStatusCode.OK,JsonSerializer.Serialize(new { Messages = new[] { new { ReceiptHandle = "opaque-receipt", Body = body, MD5OfBody = bodyMd5, Attributes = new { ApproximateReceiveCount = "3" } } } })),
    (HttpStatusCode.OK,"{}"),(HttpStatusCode.OK,"{}")
]);
using var client = Client(handler);
var transport = new SqsQueueTransport(client, options);
await Check("Official AmazonSQSClient signs and unmarshals SendMessage without a socket", async () => {
    await transport.Publish(message, CancellationToken.None);
    True(handler.Calls.Count == 1);
    var call = handler.Calls[0];
    True(call.Method == "POST" && call.Url == "https://sqs.us-east-2.amazonaws.com/");
    True(call.Target == "AmazonSQS.SendMessage" && call.ContentType == "application/x-amz-json-1.0");
    True(call.Authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal) && call.Authorization.Contains("/us-east-2/sqs/aws4_request", StringComparison.Ordinal));
    True(call.Authorization.Contains("Signature=", StringComparison.Ordinal) && call.AmzDate.Length == 16 && call.SessionToken == "qa-session-not-a-credential");
    using var payload = JsonDocument.Parse(call.Body);
    True(payload.RootElement.GetProperty("QueueUrl").GetString() == queueUrl);
    var envelope = JsonSerializer.Deserialize<IntegrationEnvelope>(payload.RootElement.GetProperty("MessageBody").GetString()!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    True(envelope == message && !payload.RootElement.TryGetProperty("MessageGroupId", out _));
});
await Check("ReceiveMessage preserves opaque receipt, envelope and receive count", async () => {
    var received = await transport.Receive(CancellationToken.None);
    True(received.Count == 1 && received[0] == new ReceivedEvent("opaque-receipt", body, 3));
    var call = handler.Calls[1];
    using var payload = JsonDocument.Parse(call.Body);
    True(call.Target == "AmazonSQS.ReceiveMessage" && payload.RootElement.GetProperty("WaitTimeSeconds").GetInt32() == 20 && payload.RootElement.GetProperty("VisibilityTimeout").GetInt32() == 120);
});
await Check("DeleteMessage signs the exact acknowledgement receipt", async () => {
    await transport.Acknowledge("opaque-receipt", CancellationToken.None);
    var call = handler.Calls[2]; using var payload = JsonDocument.Parse(call.Body);
    True(call.Target == "AmazonSQS.DeleteMessage" && payload.RootElement.GetProperty("ReceiptHandle").GetString() == "opaque-receipt");
});
await Check("ChangeMessageVisibility bounds delay and preserves the receipt", async () => {
    await transport.Retry("opaque-receipt", 50000, CancellationToken.None);
    var call = handler.Calls[3]; using var payload = JsonDocument.Parse(call.Body);
    True(call.Target == "AmazonSQS.ChangeMessageVisibility" && payload.RootElement.GetProperty("VisibilityTimeout").GetInt32() == 43200 && payload.RootElement.GetProperty("ReceiptHandle").GetString() == "opaque-receipt");
});
await Check("FIFO publish supplies stable event deduplication and tenant group IDs", async () => {
    using var http = new FixtureHandler([(HttpStatusCode.OK,"{\"MessageId\":\"fifo-1\"}")]); using var aws = Client(http);
    await new SqsQueueTransport(aws, new MessagingOptions { Provider="sqs", Region=options.Region, QueueUrl=queueUrl+".fifo" }).Publish(message,CancellationToken.None);
    using var payload=JsonDocument.Parse(http.Calls.Single().Body);
    True(payload.RootElement.GetProperty("MessageGroupId").GetString()==message.PartnerId.ToString() && payload.RootElement.GetProperty("MessageDeduplicationId").GetString()==message.Id.ToString());
});
await Check("Unconfirmed publish does not report success", async () => {
    using var http=new FixtureHandler([(HttpStatusCode.OK,"{}")]); using var aws=Client(http);
    try { await new SqsQueueTransport(aws,options).Publish(message,CancellationToken.None); } catch(IOException) { return; }
    throw new Exception("Missing MessageId was accepted");
});
await Check("AWS error status propagates without provider response body", async () => {
    using var http=new FixtureHandler([(HttpStatusCode.Forbidden,"{\"__type\":\"AccessDenied\",\"message\":\"do-not-copy-provider-body\"}")]); using var aws=Client(http);
    try { await new SqsQueueTransport(aws,options).Receive(CancellationToken.None); }
    catch(AmazonServiceException error) { True(error.StatusCode==HttpStatusCode.Forbidden && !error.ToString().Contains("do-not-copy-provider-body",StringComparison.Ordinal)); return; }
    throw new Exception("AWS failure was accepted");
});
await Check("An empty receive batch is represented without invented events", async () => {
    using var http=new FixtureHandler([(HttpStatusCode.OK,"{}")]); using var aws=Client(http);
    True((await new SqsQueueTransport(aws,options).Receive(CancellationToken.None)).Count==0);
});
await Check("Only Development can configure an emulator origin", () =>
{
    var local = new MessagingOptions { Provider = "sqs", Region = "us-east-2", QueueUrl = "http://elasticmq:9324/000000000000/demo", ServiceUrl = "http://elasticmq:9324" };
    var config = MessagingRegistration.BuildClientConfig(local, development: true);
    True(new Uri(config.ServiceURL).GetLeftPart(UriPartial.Authority) == "http://elasticmq:9324" && config.AuthenticationRegion == "us-east-2");
    try { MessagingRegistration.BuildClientConfig(local, development: false); }
    catch (InvalidOperationException) { return Task.CompletedTask; }
    throw new Exception("Production emulator endpoint was accepted.");
});
return await FinishChecks();

async Task<int> FinishChecks()
{
    (string QueueUrl, string DeadLetterQueueUrl)? queues = null;
    if (emulatorUrl is not null)
        await Check("Create dedicated ElasticMQ queues for this run", async () =>
        {
            queues = await ElasticMqChecks.Run(emulatorUrl, message, Check);
        });
    var report = JsonSerializer.Serialize(new
    {
        measuredAt = DateTimeOffset.UtcNow,
        transport = emulatorOnly ? "Official AWSSDK.SQS with loopback ElasticMQ"
            : emulatorUrl is null ? "Official AWSSDK.SQS with an isolated HTTP fixture" : "Official AWSSDK.SQS with an isolated HTTP fixture and loopback ElasticMQ",
        emulatorAttempted = emulatorUrl is not null, awsContacted = false,
        queueUrl = queues?.QueueUrl, deadLetterQueueUrl = queues?.DeadLetterQueueUrl,
        passed = results.Count - failures, failed = failures, results
    }, new JsonSerializerOptions { WriteIndented = true });
    var output = args.FirstOrDefault(x => x.StartsWith("--report=", StringComparison.Ordinal))?[9..];
    if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, report); }
    Console.WriteLine($"{results.Count - failures}/{results.Count} SQS transport checks passed; AWS was not contacted.");
    return failures == 0 ? 0 : 1;
}

sealed record HttpCall(string Method,string Url,string Target,string ContentType,string Authorization,string AmzDate,string SessionToken,string Body);
sealed class FixtureHttpClientFactory(FixtureHandler handler) : Amazon.Runtime.HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig)=>new(handler,disposeHandler:false);
    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig)=>false;
    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig)=>true;
}
sealed class FixtureHandler((HttpStatusCode Status,string Body)[] responses) : HttpMessageHandler
{
    public List<HttpCall> Calls {get;}=[];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        string Header(string name)=>request.Headers.TryGetValues(name,out var values)?string.Join(",",values):"";
        Calls.Add(new(request.Method.Method,request.RequestUri!.AbsoluteUri,Header("X-Amz-Target"),request.Content!.Headers.ContentType!.MediaType!,Header("Authorization"),Header("X-Amz-Date"),Header("X-Amz-Security-Token"),await request.Content.ReadAsStringAsync(ct)));
        if(Calls.Count>responses.Length)throw new InvalidOperationException("Unexpected SDK HTTP call; no network fallback exists.");
        var fixture=responses[Calls.Count-1];var response=new HttpResponseMessage(fixture.Status){Content=new StringContent(fixture.Body,Encoding.UTF8,"application/x-amz-json-1.0")};
        response.Headers.TryAddWithoutValidation("x-amzn-requestid","qa-aws-request");return response;
    }
}
