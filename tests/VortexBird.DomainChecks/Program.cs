using VortexBird.Application.Contracts;
using VortexBird.Application.Validation;
using VortexBird.Domain;
using VortexBird.Domain.Entities;

var cases = new (string Name, Action Run)[]
{
    ("Reservation reduces available stock without consuming on-hand", () =>
    {
        var stock = new Stock { OnHand = 10 };
        stock.Reserve(4);
        Equal(10, stock.OnHand);
        Equal(4, stock.Reserved);
        Equal(6, stock.Available);
        Equal(1L, stock.Version);
    }),
    ("Reservation rejects zero and negative quantities without mutation", () =>
    {
        var stock = new Stock { OnHand = 10 };
        Throws<ArgumentOutOfRangeException>(() => stock.Reserve(0));
        Throws<ArgumentOutOfRangeException>(() => stock.Reserve(-1));
        Equal(10, stock.Available);
        Equal(0L, stock.Version);
    }),
    ("Oversell fails and preserves existing reservations", () =>
    {
        var stock = new Stock { OnHand = 10, Reserved = 8 };
        var error = Throws<DomainException>(() => stock.Reserve(3));
        Equal("insufficient_stock", error.Code);
        Equal(8, stock.Reserved);
        Equal(0L, stock.Version);
    }),
    ("Exact available quantity can be reserved", () =>
    {
        var stock = new Stock { OnHand = 10, Reserved = 8 };
        stock.Reserve(2);
        Equal(0, stock.Available);
    }),
    ("Compensation releases reserved units without changing on-hand", () =>
    {
        var stock = new Stock { OnHand = 10, Reserved = 7 };
        stock.Release(3);
        Equal(4, stock.Reserved);
        Equal(10, stock.OnHand);
        Equal(6, stock.Available);
        Equal(1L, stock.Version);
    }),
    ("Release rejects over-release, zero and negative quantities", () =>
    {
        var stock = new Stock { OnHand = 10, Reserved = 2 };
        Throws<ArgumentOutOfRangeException>(() => stock.Release(3));
        Throws<ArgumentOutOfRangeException>(() => stock.Release(0));
        Throws<ArgumentOutOfRangeException>(() => stock.Release(-1));
        Equal(2, stock.Reserved);
        Equal(0L, stock.Version);
    }),
    ("Commit consumes a reservation and stock once", () =>
    {
        var stock = new Stock { OnHand = 10, Reserved = 4 };
        stock.Commit(4);
        Equal(6, stock.OnHand);
        Equal(0, stock.Reserved);
        Equal(6, stock.Available);
        Throws<ArgumentOutOfRangeException>(() => stock.Commit(4));
        Equal(6, stock.OnHand);
    }),
    ("Commit rejects invalid quantities", () =>
    {
        var stock = new Stock { OnHand = 10, Reserved = 2 };
        Throws<ArgumentOutOfRangeException>(() => stock.Commit(3));
        Throws<ArgumentOutOfRangeException>(() => stock.Commit(0));
        Throws<ArgumentOutOfRangeException>(() => stock.Commit(-1));
        Equal(10, stock.OnHand);
        Equal(2, stock.Reserved);
    }),
    ("Successful saga reaches delivery with chronological state history", () =>
    {
        var order = new Order();
        var at = DateTimeOffset.Parse("2026-11-13T12:00:00Z");
        var states = new[] { OrderStates.Reserved, OrderStates.AwaitingPayment, OrderStates.Confirmed, OrderStates.Shipped, OrderStates.Delivered };
        foreach (var state in states) order.TransitionTo(state, $"Transition {state}", at = at.AddSeconds(1));
        Equal(OrderStates.Delivered, order.Status);
        Equal(5, order.Timeline.Count);
        Equal(at, order.UpdatedAt);
        True(order.Timeline.All(entry => entry.OrderId == order.Id));
        True(order.Timeline.Select(entry => entry.Status).SequenceEqual(states));
    }),
    ("Rejected payment can compensate and cancel", () =>
    {
        var order = new Order();
        var at = DateTimeOffset.UtcNow;
        foreach (var state in new[] { OrderStates.Reserved, OrderStates.AwaitingPayment, OrderStates.Compensating, OrderStates.Cancelled })
            order.TransitionTo(state, "Rejected payment saga", at);
        Equal(OrderStates.Cancelled, order.Status);
        Equal(4, order.Timeline.Count);
    }),
    ("Compensation failure is represented as failed", () =>
    {
        var order = new Order { Status = OrderStates.Reserved };
        order.TransitionTo(OrderStates.Compensating, "Adapter failed", DateTimeOffset.UtcNow);
        order.TransitionTo(OrderStates.Failed, "Manual reconciliation required", DateTimeOffset.UtcNow);
        Equal(OrderStates.Failed, order.Status);
    }),
    ("Invalid order transition leaves status and history unchanged", () =>
    {
        var order = new Order();
        var before = order.UpdatedAt;
        var exception = Throws<DomainException>(() => order.TransitionTo(OrderStates.Delivered, "Skip payment", before.AddMinutes(1)));
        Equal("invalid_order_transition", exception.Code);
        Equal(OrderStates.Pending, order.Status);
        Equal(before, order.UpdatedAt);
        Equal(0, order.Timeline.Count);
    }),
    ("Terminal states reject reactivation and duplicate transitions", () =>
    {
        foreach (var terminal in new[] { OrderStates.Cancelled, OrderStates.Failed, OrderStates.Delivered })
        {
            var order = new Order { Status = terminal };
            Throws<DomainException>(() => order.TransitionTo(OrderStates.Pending, "Replay", DateTimeOffset.UtcNow));
            Throws<DomainException>(() => order.TransitionTo(terminal, "Replay", DateTimeOffset.UtcNow));
        }
    }),
    ("Confirmed order allows compensation before shipment", () =>
    {
        var order = new Order { Status = OrderStates.Confirmed };
        order.TransitionTo(OrderStates.Compensating, "Cancel before dispatch", DateTimeOffset.UtcNow);
        order.TransitionTo(OrderStates.Cancelled, "Refund and release completed", DateTimeOffset.UtcNow);
        Equal(OrderStates.Cancelled, order.Status);
    }),
    ("Shipped order cannot be cancelled by the reservation saga", () =>
    {
        var order = new Order { Status = OrderStates.Shipped };
        Throws<DomainException>(() => order.TransitionTo(OrderStates.Compensating, "Invalid cancellation", DateTimeOffset.UtcNow));
    }),
    ("Valid boundary order request is accepted", () =>
    {
        var request = ValidRequest() with { StoreId = new string('s', 80), CustomerId = new string('c', 120), Items = [new(new string('x', 80), 1000)] };
        True(CreateOrderRequestValidator.Validate(request).IsValid);
        CreateOrderRequestValidator.ValidateAndThrow(request);
    }),
    ("Null request returns a field error", () =>
    {
        var result = CreateOrderRequestValidator.Validate(null);
        True(!result.IsValid && result.Errors.ContainsKey("request"));
    }),
    ("Invalid identifiers and string lengths produce independent errors", () =>
    {
        var result = CreateOrderRequestValidator.Validate(ValidRequest() with
        {
            PartnerId = Guid.Empty, ChannelId = Guid.Empty, StoreId = new string('s', 81), CustomerId = new string('c', 121)
        });
        Equal(4, result.Errors.Count);
    }),
    ("Empty and null order lines are rejected", () =>
    {
        True(!CreateOrderRequestValidator.Validate(ValidRequest() with { Items = [] }).IsValid);
        True(!CreateOrderRequestValidator.Validate(ValidRequest() with { Items = null! }).IsValid);
        True(!CreateOrderRequestValidator.Validate(ValidRequest() with { Items = [null!] }).IsValid);
    }),
    ("Maximum order line count is enforced", () =>
    {
        var lines = Enumerable.Range(1, 100).Select(index => new OrderLineRequest($"SKU-{index}", 1)).ToList();
        True(CreateOrderRequestValidator.Validate(ValidRequest() with { Items = lines }).IsValid);
        lines.Add(new("SKU-101", 1));
        True(CreateOrderRequestValidator.Validate(ValidRequest() with { Items = lines }).Errors.ContainsKey("items"));
    }),
    ("Duplicate SKU variation cannot bypass one-line-per-SKU validation", () =>
    {
        var result = CreateOrderRequestValidator.Validate(ValidRequest() with { Items = [new("SKU-1", 1), new(" sku-1 ", 2)] });
        True(result.Errors.ContainsKey("items[1].sku"));
    }),
    ("Invalid quantities and SKUs are rejected", () =>
    {
        foreach (var quantity in new[] { -1, 0, 1001 })
            True(!CreateOrderRequestValidator.Validate(ValidRequest() with { Items = [new("SKU-1", quantity)] }).IsValid);
        True(!CreateOrderRequestValidator.Validate(ValidRequest() with { Items = [new(" ", 1)] }).IsValid);
        True(!CreateOrderRequestValidator.Validate(ValidRequest() with { Items = [new(new string('s', 81), 1)] }).IsValid);
    }),
    ("Unsupported payment result is rejected", () =>
    {
        True(CreateOrderRequestValidator.Validate(ValidRequest() with { PaymentOutcome = "PENDING" }).IsValid);
        True(!CreateOrderRequestValidator.Validate(ValidRequest() with { PaymentOutcome = "force-success" }).IsValid);
        True(!CreateOrderRequestValidator.Validate(ValidRequest() with { PaymentOutcome = null! }).IsValid);
    }),
    ("Validation exception exposes read-only field errors", () =>
    {
        var error = Throws<RequestValidationException>(() => CreateOrderRequestValidator.ValidateAndThrow(null));
        True(error.Errors.ContainsKey("request"));
        var dictionary = (IDictionary<string, IReadOnlyList<string>>)error.Errors;
        Throws<NotSupportedException>(() => dictionary.Add("injected", new[] { "mutable" }));
        var messages = (IList<string>)error.Errors["request"];
        Throws<NotSupportedException>(() => messages.Add("mutable"));
    })
};

var failed = 0;
foreach (var test in cases)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}"); }
}
Console.WriteLine($"{cases.Length - failed}/{cases.Length} checks passed.");
return failed == 0 ? 0 : 1;

static CreateOrderRequest ValidRequest() => new(Guid.NewGuid(), Guid.NewGuid(), "BOG-01", "CUSTOMER-01", [new("SKU-1", 1)]);
static void True(bool condition) { if (!condition) throw new Exception("Expected true."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
static TException Throws<TException>(Action action) where TException : Exception
{
    try { action(); } catch (TException exception) { return exception; }
    throw new Exception($"Expected {typeof(TException).Name}.");
}
