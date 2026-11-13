namespace VortexBird.Application.Abstractions;

/// <summary>
/// Intersects application-owned tenant membership with signed token restrictions.
/// An absent claim preserves database membership; a malformed or contradictory
/// claim never widens access. Channel restrictions require a resolved partner.
/// Database enabled-state and channel ownership are checked by the request actor.
/// </summary>
public sealed class AuthorizationScope
{
    private AuthorizationScope(Guid? partnerId, Guid[] channelIds)
    {
        PartnerId = partnerId;
        ChannelIds = Array.AsReadOnly(channelIds);
    }

    public Guid? PartnerId { get; }
    public IReadOnlyList<Guid> ChannelIds { get; }
    public bool IsGlobal => PartnerId is null && ChannelIds.Count == 0;

    public static AuthorizationScope Resolve(
        Guid? databasePartnerId,
        string? claimedPartnerId,
        IEnumerable<string>? claimedChannelIds)
    {
        if (databasePartnerId == Guid.Empty)
            throw new AuthorizationScopeException("La pertenencia al partner no es válida.");

        Guid? tokenPartnerId = null;
        if (claimedPartnerId is not null)
        {
            if (!Guid.TryParse(claimedPartnerId, out var parsed) || parsed == Guid.Empty)
                throw new AuthorizationScopeException("El token contiene un partner inválido.");
            tokenPartnerId = parsed;
        }

        if (databasePartnerId.HasValue && tokenPartnerId.HasValue && databasePartnerId != tokenPartnerId)
            throw new AuthorizationScopeException("El partner del token no coincide con la cuenta.");

        var effectivePartner = databasePartnerId ?? tokenPartnerId;
        var channels = new HashSet<Guid>();
        foreach (var claim in claimedChannelIds ?? [])
        {
            if (!Guid.TryParse(claim, out var channel) || channel == Guid.Empty)
                throw new AuthorizationScopeException("El token contiene un canal inválido.");
            channels.Add(channel);
        }

        if (channels.Count > 0 && !effectivePartner.HasValue)
            throw new AuthorizationScopeException("Un token restringido por canal requiere un partner.");

        return new AuthorizationScope(effectivePartner, channels.Order().ToArray());
    }
}

public sealed class AuthorizationScopeException(string message) : Exception(message);
