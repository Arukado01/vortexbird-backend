using System.Globalization;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace VortexBird.Infrastructure.Messaging;

public sealed class SqsQueueTransport(IAmazonSQS client, MessagingOptions options) : IQueueTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task Publish(IntegrationEnvelope message, CancellationToken ct)
    {
        message.Validate();
        var request = new SendMessageRequest
        {
            QueueUrl = options.QueueUrl,
            MessageBody = JsonSerializer.Serialize(message, Json)
        };
        if (options.QueueUrl.EndsWith(".fifo", StringComparison.Ordinal))
        {
            request.MessageGroupId = message.PartnerId.ToString();
            request.MessageDeduplicationId = message.Id.ToString();
        }
        var response = await Call(() => client.SendMessageAsync(request, ct));
        if (string.IsNullOrWhiteSpace(response.MessageId))
            throw new IOException("SQS did not confirm a message identifier.");
    }

    public async Task<IReadOnlyList<ReceivedEvent>> Receive(CancellationToken ct)
    {
        var response = await Call(() => client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = options.QueueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 20,
            VisibilityTimeout = options.VisibilityTimeoutSeconds,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        }, ct));
        return response.Messages?.Select(message =>
        {
            if (string.IsNullOrWhiteSpace(message.ReceiptHandle))
                throw new IOException("SQS did not return a receipt handle.");
            var count = message.Attributes?.GetValueOrDefault("ApproximateReceiveCount");
            var attempts = int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 1;
            return new ReceivedEvent(message.ReceiptHandle, message.Body ?? "", attempts);
        }).ToArray() ?? [];
    }

    public async Task Acknowledge(string receiptHandle, CancellationToken ct) =>
        await Call(() => client.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = options.QueueUrl,
            ReceiptHandle = receiptHandle
        }, ct));

    public async Task Retry(string receiptHandle, int delaySeconds, CancellationToken ct) =>
        await Call(() => client.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = options.QueueUrl,
            ReceiptHandle = receiptHandle,
            VisibilityTimeout = Math.Clamp(delaySeconds, 0, 43200)
        }, ct));

    private static async Task<T> Call<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (AmazonServiceException error)
        {
            // Response bodies and provider error text can reach worker logs.
            throw new AmazonServiceException("SQS request failed.", error.ErrorType, "SqsRequestFailed", error.RequestId, error.StatusCode);
        }
    }
}
