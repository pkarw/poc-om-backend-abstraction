namespace OpenMercato.Modules.Catalog.Lib;

/// <summary>
/// Catalog price resolution — the port of upstream <c>lib/pricing.ts</c> (<c>selectBestPrice</c> path).
/// Given a set of candidate price rows and a <see cref="PricingContext"/>, filters by applicability
/// (quantity window, validity dates, channel/user/customer scope, offer) and picks the most specific one
/// by the upstream specificity score + tie-breaks. The registered-resolver / event-hook extension points
/// (<c>registerCatalogPricingResolver</c>, <c>catalog.pricing.resolve.*</c>) are empty in practice, so the
/// default resolution IS <see cref="SelectBest"/>; those hooks are deferred (PARITY-TODO).
/// </summary>
public static class CatalogPricing
{
    /// <summary>Resolution context (upstream <c>PricingContext</c>).</summary>
    public sealed record PricingContext(
        Guid? ChannelId, Guid? OfferId, Guid? UserId, Guid? UserGroupId,
        Guid? CustomerId, Guid? CustomerGroupId, int Quantity, DateTimeOffset Date);

    /// <summary>A flattened candidate price row with the fields resolution needs (price + its price-kind
    /// code/promotion flag + the linked offer's channel).</summary>
    public sealed record Candidate(
        Guid Id, Guid? VariantId, Guid? ProductId, Guid? OfferId, Guid PriceKindId,
        string? PriceKindCode, bool PriceKindIsPromotion, Guid? OfferChannelId,
        string Kind, string CurrencyCode, int MinQuantity, int? MaxQuantity,
        decimal? UnitPriceNet, decimal? UnitPriceGross, decimal? TaxRate, decimal? TaxAmount,
        Guid? ChannelId, Guid? UserId, Guid? UserGroupId, Guid? CustomerId, Guid? CustomerGroupId,
        DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

    public static Guid? ResolveChannelId(Candidate row)
    {
        if (row.OfferId is null) return row.ChannelId;
        return row.ChannelId ?? row.OfferChannelId;
    }

    /// <summary>Resolved price-kind code (upstream <c>resolvePriceKindCode</c>): the kind's code, else the
    /// row's <c>kind</c> string.</summary>
    public static string ResolveKindCode(Candidate row) =>
        !string.IsNullOrEmpty(row.PriceKindCode) ? row.PriceKindCode! : row.Kind ?? string.Empty;

    private static bool MatchesContext(Candidate row, PricingContext ctx)
    {
        if (row.MinQuantity > 0 && ctx.Quantity < row.MinQuantity) return false;
        if (row.MaxQuantity is { } max && max > 0 && ctx.Quantity > max) return false;
        if (row.StartsAt is { } starts && ctx.Date < starts) return false;
        if (row.EndsAt is { } ends && ctx.Date > ends) return false;

        var channel = ResolveChannelId(row);
        if (row.ChannelId is not null || (row.OfferId is not null && channel is not null))
        {
            if (channel is not null && ctx.ChannelId is not null && channel != ctx.ChannelId) return false;
            if (channel is not null && ctx.ChannelId is null) return false;
        }
        if (row.UserId is not null && ctx.UserId != row.UserId) return false;
        if (row.UserGroupId is not null && ctx.UserGroupId != row.UserGroupId) return false;
        if (row.CustomerId is not null && ctx.CustomerId != row.CustomerId) return false;
        if (row.CustomerGroupId is not null && ctx.CustomerGroupId != row.CustomerGroupId) return false;
        if (ctx.OfferId is not null && row.OfferId is not null && row.OfferId != ctx.OfferId) return false;
        return true;
    }

    private static int Score(Candidate row)
    {
        var kind = ResolveKindCode(row);
        var score = 0;
        if (kind == "custom") score += 5;
        else if (kind == "tier") score += 3;
        else if (kind == "promotion" || row.PriceKindIsPromotion) score += 4;
        else score += 2;
        if (row.VariantId is not null) score += 8;
        if (row.OfferId is not null) score += 6;
        if (row.ChannelId is not null) score += 5;
        if (row.UserId is not null) score += 5;
        if (row.UserGroupId is not null) score += 4;
        if (row.CustomerId is not null) score += 4;
        if (row.CustomerGroupId is not null) score += 3;
        if (row.MinQuantity > 1) score += 1;
        return score;
    }

    /// <summary>Filter candidates to those applicable in the context, then pick the most specific by score
    /// desc → startsAt desc → the direction-dependent minQuantity tie-break (upstream <c>selectBestPrice</c>).</summary>
    public static Candidate? SelectBest(IReadOnlyList<Candidate> rows, PricingContext ctx)
    {
        var candidates = rows.Where(r => MatchesContext(r, ctx)).ToList();
        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) =>
        {
            var scoreDiff = Score(b) - Score(a);
            if (scoreDiff != 0) return scoreDiff;
            var startA = a.StartsAt?.UtcTicks ?? 0;
            var startB = b.StartsAt?.UtcTicks ?? 0;
            if (startA != startB) return startB.CompareTo(startA);
            // Within the same kind the more specific tier wins (higher minQuantity); across kinds keep the
            // ascending order so the higher-scoreBase kind still wins a cross-kind score collision.
            if (ResolveKindCode(a) == ResolveKindCode(b))
                return (b.MinQuantity <= 0 ? 1 : b.MinQuantity) - (a.MinQuantity <= 0 ? 1 : a.MinQuantity);
            return (a.MinQuantity <= 0 ? 1 : a.MinQuantity) - (b.MinQuantity <= 0 ? 1 : b.MinQuantity);
        });
        return candidates[0];
    }
}
