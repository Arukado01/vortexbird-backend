namespace VortexBird.Domain.Entities;

public class OmsReplica
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid PartnerId { get; set; }
    public Guid ChannelId { get; set; }
    public string ExternalOrderNumber { get; set; } = "";
    public string FulfillmentState { get; set; } = "";
    public string LinesJson { get; set; } = "[]";
    public DateTimeOffset SynchronizedAt { get; set; } = DateTimeOffset.UtcNow;
}
