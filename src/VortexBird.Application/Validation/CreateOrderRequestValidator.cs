using System.Collections.ObjectModel;
using VortexBird.Application.Contracts;

namespace VortexBird.Application.Validation;

/// <summary>
/// Dependency-free boundary validation. Domain invariants and transactional
/// stock validation remain authoritative after this inexpensive validation.
/// </summary>
public static class CreateOrderRequestValidator
{
    public const int MaximumLines = 100;
    public const int MaximumQuantity = 1000;

    public static RequestValidationResult Validate(CreateOrderRequest? request)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Error(string field, string message)
        {
            if (!errors.TryGetValue(field, out var messages)) errors[field] = messages = [];
            messages.Add(message);
        }

        void RequiredLength(string field, string? value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value)) Error(field, "El campo es obligatorio.");
            else if (value.Length > maximumLength) Error(field, $"La longitud máxima es {maximumLength} caracteres.");
        }

        if (request is null)
        {
            Error("request", "El cuerpo de la solicitud es obligatorio.");
        }
        else
        {
            if (request.PartnerId == Guid.Empty) Error("partnerId", "Debe especificar un partner válido.");
            if (request.ChannelId == Guid.Empty) Error("channelId", "Debe especificar un canal válido.");
            RequiredLength("storeId", request.StoreId, 80);
            RequiredLength("customerId", request.CustomerId, 120);

            if (request.Items is null || request.Items.Count == 0)
                Error("items", "La orden debe contener al menos un producto.");
            else if (request.Items.Count > MaximumLines)
                Error("items", $"La orden admite como máximo {MaximumLines} líneas.");
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < request.Items.Count; index++)
                {
                    var item = request.Items[index];
                    if (item is null)
                    {
                        Error($"items[{index}]", "La línea no puede estar vacía.");
                        continue;
                    }
                    RequiredLength($"items[{index}].sku", item.Sku, 80);
                    if (!string.IsNullOrWhiteSpace(item.Sku) && !seen.Add(item.Sku.Trim()))
                        Error($"items[{index}].sku", "No se permiten SKU duplicados; consolide su cantidad en una sola línea.");
                    if (item.Quantity is < 1 or > MaximumQuantity)
                        Error($"items[{index}].quantity", $"La cantidad debe estar entre 1 y {MaximumQuantity}.");
                }
            }
        }

        var immutable = errors.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value.AsReadOnly(),
            StringComparer.Ordinal);
        return new RequestValidationResult(new ReadOnlyDictionary<string, IReadOnlyList<string>>(immutable));
    }

    public static void ValidateAndThrow(CreateOrderRequest? request)
    {
        var result = Validate(request);
        if (!result.IsValid) throw new RequestValidationException(result.Errors);
    }
}

public sealed record RequestValidationResult(IReadOnlyDictionary<string, IReadOnlyList<string>> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class RequestValidationException(IReadOnlyDictionary<string, IReadOnlyList<string>> errors)
    : Exception("La solicitud contiene errores de validación.")
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Errors { get; } = errors;
}
