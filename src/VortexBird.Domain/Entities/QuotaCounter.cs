namespace VortexBird.Domain.Entities;

// One replaceable minute counter per tenant/budget, rather than one row per minute.
public class QuotaCounter
{
    public Guid PartnerId { get; set; }
    public string BudgetKey { get; set; } = "";
    public long Minute { get; set; }
    public int Count { get; set; }
}
