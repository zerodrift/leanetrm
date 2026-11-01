using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Crude;

/// <summary>A nonempty UTC delivery interval and its enclosing exclusive date bounds.</summary>
public sealed record CrudeDeliveryWindow
{
    private static readonly DateTimeOffset Earliest = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Latest = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public DateOnly DateFrom => DateOnly.FromDateTime(From.UtcDateTime);
    public DateOnly DateTo => DateOnly.FromDateTime(To.UtcDateTime).AddDays(To.TimeOfDay == TimeSpan.Zero ? 0 : 1);

    public CrudeDeliveryWindow(DateTimeOffset from, DateTimeOffset to)
    {
        if (!TradingInstant.IsSupported(from) || !TradingInstant.IsSupported(to) ||
            from >= to || from < Earliest || to > Latest)
            throw Invalid();
        From = from;
        To = to;
    }

    public static bool IsMonthlyForward(MarketContract contract) =>
        contract.Kind == ContractKind.Forward && contract.Interval is null &&
        contract.DeliveryFrom.Day == 1 && contract.DeliveryFrom < new DateOnly(9999, 12, 1) &&
        contract.DeliveryTo == contract.DeliveryFrom.AddMonths(1);

    public bool IsCoveredBy(MarketContract contract) => IsMonthlyForward(contract) &&
        From >= Midnight(contract.DeliveryFrom) && To <= Midnight(contract.DeliveryTo);

    public void RequireCoveredBy(MarketContract contract)
    {
        if (!IsCoveredBy(contract)) throw Invalid();
    }

    private static DateTimeOffset Midnight(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    private static MarketDataException Invalid() => new("crude_delivery_window",
        "Supply increasing supported UTC delivery instants entirely within the selected monthly forward.");
}
