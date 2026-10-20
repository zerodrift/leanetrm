namespace OpenEtrm.MarketData.Trading;

public readonly record struct PartyKey(Guid CatalogId, Guid Id, int Revision);

public sealed record LegalEntity
{
    public PartyKey Key { get; }
    public string Code { get; }
    public string Name { get; }
    public LegalEntity(PartyKey key, string code, string name)
    { PartyDefinition.Validate(key, code, name); Key = key; Code = code; Name = name; }
}

public sealed record Counterparty
{
    public PartyKey Key { get; }
    public string Code { get; }
    public string Name { get; }
    public Counterparty(PartyKey key, string code, string name)
    { PartyDefinition.Validate(key, code, name); Key = key; Code = code; Name = name; }
}

public sealed record TradeBookingTerms
{
    public DateOnly TradeDate { get; }
    public LegalEntity LegalEntity { get; }
    public Counterparty Counterparty { get; }
    public TradeBookingTerms(DateOnly tradeDate, LegalEntity legalEntity, Counterparty counterparty)
    {
        if (legalEntity is null || counterparty is null || legalEntity.Key.CatalogId != counterparty.Key.CatalogId ||
            tradeDate.Year is < 1901 or > 2199)
            throw new MarketDataException("trade_party", "Supply an explicit supported trade date and exact party versions from the trade catalog.");
        TradeDate = tradeDate; LegalEntity = legalEntity; Counterparty = counterparty;
    }
}

internal static class PartyDefinition
{
    private const int MaximumNameLength = 80;
    internal static void Validate(PartyKey key, string code, string name)
    {
        if (key.CatalogId == Guid.Empty || key.Id == Guid.Empty || key.Revision <= 0 || string.IsNullOrWhiteSpace(code) ||
            code.Length > 48 || !char.IsAsciiLetterLower(code[0]) || code.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_') ||
            string.IsNullOrWhiteSpace(name) || name.EnumerateRunes().Take(MaximumNameLength + 1).Count() > MaximumNameLength || name.Contains('\0'))
            throw new MarketDataException("trade_party", "A party requires an exact catalog/identity/version, business code and name.");
    }
}
