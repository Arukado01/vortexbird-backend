using System.Net;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Transform;

namespace VortexBird.Infrastructure.Messaging;

/// <summary>Only JSON protocol marshalling lives here. AWS Core owns credentials, SigV4, retry and HTTP.</summary>
public sealed class SqsJsonClient : AmazonServiceClient
{
    private sealed class SqsMetadata : IServiceMetadata { public string ServiceId => "SQS"; public IDictionary<string, string> OperationNameMapping { get; } = new Dictionary<string, string>(); }
    private static readonly IServiceMetadata Metadata = new SqsMetadata();
    protected override IServiceMetadata ServiceMetadata => Metadata;
    public SqsJsonClient(SqsClientConfig config) : base(config) { }
    public SqsJsonClient(AWSCredentials credentials, SqsClientConfig config) : base(credentials, config) { }
    protected override void CustomizeRuntimePipeline(RuntimePipeline pipeline)
    {
        base.CustomizeRuntimePipeline(pipeline);
        pipeline.AddHandlerBefore<Signer>(new SqsAuthSchemeHandler());
    }
    public Task<SqsJsonResponse> Send(string operation, object payload, CancellationToken ct) => InvokeAsync<SqsJsonResponse>(new SqsJsonRequest(operation, JsonSerializer.Serialize(payload)), new InvokeOptions { RequestMarshaller = new SqsJsonMarshaller(), ResponseUnmarshaller = new SqsJsonUnmarshaller() }, ct);
}
public sealed class SqsAuthSchemeHandler : BaseAuthResolverHandler
{
    private sealed class SigV4Option : IAuthSchemeOption
    {
        public string SchemeId { get; set; } = "aws.auth#sigv4";
        public string ShortName => "sigv4";
    }
    // AWS Core resolves the identity and creates its own signer from this Smithy auth scheme.
    protected override List<IAuthSchemeOption> ResolveAuthOptions(IExecutionContext context) => [new SigV4Option()];
}
public sealed class SqsClientConfig : ClientConfig
{
    public SqsClientConfig() : base(new DefaultConfigurationProvider([new DefaultConfiguration { Name = DefaultConfigurationMode.Standard, RetryMode = RequestRetryMode.Standard, ConnectTimeout = TimeSpan.FromSeconds(3) }]))
    {
        AuthenticationServiceName = "sqs";
        ServiceId = "SQS";
        Timeout = TimeSpan.FromSeconds(30);
        MaxErrorRetry = 2;
        AllowAutoRedirect = false;
        DisableLogging = true;
    }
    public override string ServiceVersion => "2012-11-05";
    public override string RegionEndpointServiceName => "sqs";
    public override string UserAgent => "VortexBird.SQS/1.0 AWSSDK.Core";
    public override Endpoint DetermineServiceOperationEndpoint(ServiceOperationEndpointParameters parameters) => new(ServiceURL ?? $"https://sqs.{AuthenticationRegion ?? RegionEndpoint?.SystemName ?? "us-east-2"}.amazonaws.com");
}
public sealed class SqsJsonRequest(string operation, string body) : AmazonWebServiceRequest
{
    public string Operation { get; } = operation;
    public string Body { get; } = body;
}
public sealed class SqsJsonResponse : AmazonWebServiceResponse
{
    public string Body { get; init; } = "{}";
}
public sealed class SqsJsonMarshaller : IMarshaller<IRequest, AmazonWebServiceRequest>
{
    private static readonly HashSet<string> Allowed = ["SendMessage", "ReceiveMessage", "DeleteMessage", "ChangeMessageVisibility"];
    public IRequest Marshall(AmazonWebServiceRequest request)
    {
        var input = (SqsJsonRequest)request;
        if (!Allowed.Contains(input.Operation)) throw new ArgumentException("Unsupported SQS operation.");
        var output = new DefaultRequest(input, "AmazonSQS") { HttpMethod = "POST", ResourcePath = "/", Content = Encoding.UTF8.GetBytes(input.Body) };
        output.Headers["Content-Type"] = "application/x-amz-json-1.0";
        output.Headers["X-Amz-Target"] = "AmazonSQS." + input.Operation;
        return output;
    }
}
public sealed class SqsJsonUnmarshaller : JsonResponseUnmarshaller
{
    public override AmazonWebServiceResponse Unmarshall(JsonUnmarshallerContext context)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = context.Stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (bytes.Length + count > 1048576) throw new IOException("SQS response exceeded the configured message limit.");
            bytes.Write(buffer, 0, count);
        }
        return new SqsJsonResponse { Body = bytes.Length == 0 ? "{}" : Encoding.UTF8.GetString(bytes.ToArray()) };
    }
    public override AmazonServiceException UnmarshallException(JsonUnmarshallerContext context, Exception innerException, HttpStatusCode statusCode)
    {
        // Preserve HTTP status for AWS retry policy, but never copy message bodies or credentials into exceptions.
        return new AmazonServiceException("SQS request failed.", innerException, ErrorType.Unknown, "SqsRequestFailed", context.ResponseData.GetHeaderValue("x-amzn-requestid"), statusCode);
    }
}

public sealed class SqsQueueTransport(SqsJsonClient client, MessagingOptions options) : IQueueTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task Publish(IntegrationEnvelope message, CancellationToken ct)
    {
        message.Validate();
        var payload = new Dictionary<string, object> { ["QueueUrl"] = options.QueueUrl, ["MessageBody"] = JsonSerializer.Serialize(message, Json) };
        if (options.QueueUrl.EndsWith(".fifo", StringComparison.Ordinal)) { payload["MessageGroupId"] = message.PartnerId.ToString(); payload["MessageDeduplicationId"] = message.Id.ToString(); }
        var response = await client.Send("SendMessage", payload, ct);
        using var document = JsonDocument.Parse(response.Body);
        if (!document.RootElement.TryGetProperty("MessageId", out var id) || string.IsNullOrWhiteSpace(id.GetString())) throw new IOException("SQS did not confirm a message identifier.");
    }
    public async Task<IReadOnlyList<ReceivedEvent>> Receive(CancellationToken ct)
    {
        var response = await client.Send("ReceiveMessage", new { QueueUrl = options.QueueUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 20, VisibilityTimeout = options.VisibilityTimeoutSeconds, MessageSystemAttributeNames = new[] { "ApproximateReceiveCount" } }, ct);
        using var document = JsonDocument.Parse(response.Body);
        if (!document.RootElement.TryGetProperty("Messages", out var messages)) return [];
        return messages.EnumerateArray().Select(m => new ReceivedEvent(m.GetProperty("ReceiptHandle").GetString()!, m.GetProperty("Body").GetString()!, m.TryGetProperty("Attributes", out var attributes) && attributes.TryGetProperty("ApproximateReceiveCount", out var count) && int.TryParse(count.GetString(), out var value) ? value : 1)).ToArray();
    }
    public async Task Acknowledge(string receiptHandle, CancellationToken ct) => await client.Send("DeleteMessage", new { QueueUrl = options.QueueUrl, ReceiptHandle = receiptHandle }, ct);
    public async Task Retry(string receiptHandle, int delaySeconds, CancellationToken ct) => await client.Send("ChangeMessageVisibility", new { QueueUrl = options.QueueUrl, ReceiptHandle = receiptHandle, VisibilityTimeout = Math.Clamp(delaySeconds, 0, 43200) }, ct);
}
