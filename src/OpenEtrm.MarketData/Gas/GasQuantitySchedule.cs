using System.Collections.Immutable;
using System.Runtime.Serialization;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

// Rules are supplied by the selected reference revision, not reloaded from the
// machine's current timezone database when a historical schedule is generated.
[EconomicDefinition("gas_day_clock_v1")]
public sealed record GasDayClock
{
    private readonly TimeZoneInfo zone;
    [IdentityField] public ReferenceKey Key { get; }
    [IdentityField] public string Rules { get; }
    [IdentityField] public int StartMinute { get; }

    public GasDayClock(ReferenceKey key, string rules, int startMinute)
    {
        Money.RequireKey(key);
        if (string.IsNullOrWhiteSpace(rules) || rules.Length > 65_536 || startMinute is < 0 or >= 1440)
            throw new MarketDataException("gas_day_clock", "Supply captured clock rules and a gas-day start minute within the local day.");
        try { zone = TimeZoneInfo.FromSerializedString(rules); }
        catch (Exception error) when (error is SerializationException or ArgumentException)
        {
            throw new MarketDataException("gas_day_clock", "The captured gas-day clock rules are invalid.");
        }
        Key = key; Rules = rules; StartMinute = startMinute;
    }

    public static GasDayClock Capture(ReferenceKey key, TimeZoneInfo zone, int startMinute)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return new(key, zone.ToSerializedString(), startMinute);
    }

    public DateTimeOffset Boundary(DateOnly operatingDay)
    {
        var local = operatingDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).AddMinutes(StartMinute);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new MarketDataException("gas_day_boundary", "The selected gas-day boundary is missing or ambiguous under its captured rules.");
        try { return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException)
        {
            throw new MarketDataException("gas_day_boundary", "The selected gas-day boundary is outside the supported UTC instant range.");
        }
    }
}

public enum GasQuantityBasis { PerOperatingDay = 1, PerElapsedHour, PeriodTotalEqualOperatingDays }
public enum GasFuelConvention { None = 1, NetEqualsGrossTimesOneMinusFuel, NetEqualsGrossDividedByOnePlusFuel }

public static class GasFuelRule
{
    public static OpenEtrm.Shared.Numerics.ExactRatio DeliveryFactor(GasFuelConvention convention,ExactDecimal fraction)
    {
        var one=ExactDecimal.Parse("1");var scale=ExactDecimal.WorkingScale;
        if(!Enum.IsDefined(convention)||fraction.CompareTo(default)<0||fraction.CompareTo(one)>=0||convention==GasFuelConvention.None&&fraction!=default)
            throw new MarketDataException("gas_fuel_terms","Select a supported fuel convention and fraction in [0,1); no-fuel terms require zero.");
        return convention switch{
            GasFuelConvention.None=>OpenEtrm.Shared.Numerics.ExactRatio.One,
            GasFuelConvention.NetEqualsGrossTimesOneMinusFuel=>new(scale-fraction.ScaledValue,scale),
            GasFuelConvention.NetEqualsGrossDividedByOnePlusFuel=>new(scale,scale+fraction.ScaledValue),
            _=>throw new MarketDataException("gas_fuel_terms","Select an installed fuel convention.")};
    }
}

// Amount is a receipt quantity per selected basis. PerElapsedHour explicitly
// means units of that native quantity per elapsed hour, not a daily entitlement.
[EconomicDefinition("gas_quantity_terms_v1")]
public sealed record GasQuantityTerms
{
    public const int MaximumOperatingDays = 1200;
    [IdentityField] public GasDayClock Clock { get; }
    [IdentityField] public DateOnly From { get; }
    [IdentityField] public DateOnly To { get; }
    [IdentityField] public GasQuantityBasis Basis { get; }
    [IdentityField] public Quantity Receipt { get; }
    [IdentityField] public GasFuelConvention FuelConvention { get; }
    [IdentityField] public ExactDecimal FuelFraction { get; }

    public GasQuantityTerms(GasDayClock clock, DateOnly from, DateOnly to, GasQuantityBasis basis,
        Quantity receipt, GasFuelConvention fuelConvention, ExactDecimal fuelFraction)
    {
        if (clock is null || receipt is null || !Enum.IsDefined(basis) || from >= to ||
            to.DayNumber - from.DayNumber > MaximumOperatingDays || receipt.Amount.CompareTo(default) < 0 ||
            receipt.Specification.Revision != clock.Key.Revision)
            throw new MarketDataException("gas_quantity_terms", "Supply an ordered bounded gas term, captured clock, nonnegative native receipt quantity and explicit quantity basis in one reference revision.");
        Money.RequireKey(receipt.Specification);
        var one = ExactDecimal.Parse("1");
        if (!Enum.IsDefined(fuelConvention) || fuelFraction.CompareTo(default) < 0 || fuelFraction.CompareTo(one) >= 0 ||
            fuelConvention == GasFuelConvention.None && fuelFraction != default)
            throw new MarketDataException("gas_fuel_terms", "Select an explicit fuel convention and fraction in [0,1); no-fuel terms require zero.");
        Clock = clock; From = from; To = to; Basis = basis; Receipt = receipt;
        FuelConvention = fuelConvention; FuelFraction = fuelFraction;
    }
}

public sealed record GasQuantityRow(DateOnly OperatingDay, DateTimeOffset Start, DateTimeOffset End,
    ExactQuantity Receipt, ExactQuantity Delivery, ExactQuantity Fuel);

// A contractual quantity profile, not operational actuals or accepted cash.
// Side compilation can consume these exact quantities and geometry without
// importing power's MW/asset-capacity normalization.
public sealed record GasQuantitySchedule
{
    public GasQuantityTerms Terms { get; }
    public ImmutableArray<GasQuantityRow> Rows { get; }
    public ExactQuantity TotalReceipt { get; }
    public ExactQuantity TotalDelivery { get; }
    public ExactQuantity TotalFuel { get; }

    private GasQuantitySchedule(GasQuantityTerms terms, ImmutableArray<GasQuantityRow> rows)
    {
        Terms = terms; Rows = rows;
        var zero = ExactQuantity.FromQuantity(new(default, terms.Receipt.Specification));
        TotalReceipt = rows.Aggregate(zero, (sum, row) => sum.Add(row.Receipt));
        TotalDelivery = rows.Aggregate(zero, (sum, row) => sum.Add(row.Delivery));
        TotalFuel = rows.Aggregate(zero, (sum, row) => sum.Add(row.Fuel));
    }

    public static GasQuantitySchedule Generate(GasQuantityTerms terms, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(terms);
        cancellation.ThrowIfCancellationRequested();
        var count = terms.To.DayNumber - terms.From.DayNumber;
        var rows = ImmutableArray.CreateBuilder<GasQuantityRow>(count);
        var entered = ExactQuantity.FromQuantity(terms.Receipt);
        var start = terms.Clock.Boundary(terms.From);
        var deliveryFactor=GasFuelRule.DeliveryFactor(terms.FuelConvention,terms.FuelFraction);
        for (var n = 0; n < count; n++)
        {
            cancellation.ThrowIfCancellationRequested();
            var day = terms.From.AddDays(n);
            var end = terms.Clock.Boundary(day.AddDays(1));
            if (end <= start)
                throw new MarketDataException("gas_day_boundary", "The captured gas-day boundaries must form positive ordered UTC intervals.");
            var receipt = terms.Basis switch
            {
                GasQuantityBasis.PerOperatingDay => entered,
                GasQuantityBasis.PerElapsedHour => entered.ScaleFraction((end - start).Ticks, TimeSpan.TicksPerHour),
                GasQuantityBasis.PeriodTotalEqualOperatingDays => entered.ScaleFraction(1, count),
                _ => throw new MarketDataException("gas_quantity_terms", "The gas quantity basis is unsupported.")
            };
            var delivery=receipt.Scale(deliveryFactor);
            rows.Add(new(day, start, end, receipt, delivery, receipt.Add(delivery.Scale(ExactDecimal.Parse("-1")))));
            start = end;
        }
        return new(terms, rows.ToImmutable());
    }
}
