using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using VortexBird.Infrastructure.Messaging;

internal static class ElasticMqChecks
{
    public static async Task<(string QueueUrl, string DeadLetterQueueUrl)> Run(string serviceUrl, IntegrationEnvelope message, Func<string, Func<Task>, Task> check)
    {
        var endpoint = new Uri(serviceUrl);
        if (endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.GetLeftPart(UriPartial.Authority) != serviceUrl)
            throw new ArgumentException("The standalone emulator test requires an HTTP IPv4 loopback origin.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var options = new MessagingOptions
        {
            Provider = "sqs", Region = "us-east-2", ServiceUrl = serviceUrl,
            QueueUrl = serviceUrl + "/000000000000/bootstrap"
        };
        using var client = new AmazonSQSClient(new BasicAWSCredentials("emulator-only", "emulator-only"),
            MessagingRegistration.BuildClientConfig(options, development: true));
        var suffix = Guid.NewGuid().ToString("N");
        var dead = (await client.CreateQueueAsync("vortexbird-dlq-qa-" + suffix, ct)).QueueUrl;
        var deadAttributes = await client.GetQueueAttributesAsync(dead, ["QueueArn"], ct);
        var queue = (await client.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "vortexbird-events-qa-" + suffix,
            Attributes = new()
            {
                ["RedrivePolicy"] = JsonSerializer.Serialize(new { deadLetterTargetArn = deadAttributes.QueueARN, maxReceiveCount = 5 })
            }
        }, ct)).QueueUrl;
        if (dead != $"{serviceUrl}/000000000000/vortexbird-dlq-qa-{suffix}" ||
            queue != $"{serviceUrl}/000000000000/vortexbird-events-qa-{suffix}")
            throw new InvalidDataException("ElasticMQ did not return this run's loopback queue URLs.");
        var transport = new SqsQueueTransport(client, new MessagingOptions { QueueUrl = queue, VisibilityTimeoutSeconds = 30 });

        async Task<Message[]> Poll(string url, int wait = 1)
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, WaitTimeSeconds = wait, MaxNumberOfMessages = 1
            }, ct);
            return response.Messages?.ToArray() ?? [];
        }

        await check("ElasticMQ holds unacknowledged delivery, redelivers after visibility, and deletes after ack", async () =>
        {
            await transport.Publish(message, ct);
            var first = (await transport.Receive(ct)).Single();
            Require(first.ReceiveCount == 1 && (await Poll(queue, 0)).Length == 0);
            await transport.Retry(first.ReceiptHandle, 1, ct);
            // Drop the first receipt as a stopped consumer would.
            var second = (await transport.Receive(ct)).Single();
            Require(second.ReceiveCount == 2 && second.ReceiptHandle != first.ReceiptHandle && second.Body == first.Body);
            await transport.Acknowledge(second.ReceiptHandle, ct);
            Require((await Poll(queue)).Length == 0);
        });

        await check("ElasticMQ moves an exhausted delivery to DLQ and accepts a controlled replay", async () =>
        {
            var replay = message with { Id = Guid.NewGuid() };
            await transport.Publish(replay, ct);
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var received = (await transport.Receive(ct)).Single();
                Require(received.ReceiveCount == attempt);
                await transport.Retry(received.ReceiptHandle, 0, ct);
            }
            Require((await Poll(queue)).Length == 0);
            var failed = (await Poll(dead)).Single();
            Require(failed.Body.Contains(replay.Id.ToString(), StringComparison.OrdinalIgnoreCase));
            await transport.Publish(replay, ct);
            await client.DeleteMessageAsync(dead, failed.ReceiptHandle, ct);
            var recovered = (await transport.Receive(ct)).Single();
            Require(recovered.ReceiveCount == 1 && recovered.Body == failed.Body && (await Poll(dead)).Length == 0);
            await transport.Acknowledge(recovered.ReceiptHandle, ct);
        });
        return (queue, dead);
    }

    private static void Require(bool expected)
    {
        if (!expected) throw new InvalidOperationException("ElasticMQ assertion failed.");
    }
}
