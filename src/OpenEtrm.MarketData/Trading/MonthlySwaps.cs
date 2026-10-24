using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public enum MonthlyQuantityBasis { PerMonth = 1, TotalEqualMonths, TotalCalendarDays }

// A strip composes existing independently specified monthly sides. It does not
// infer fixings, calendar adjustments or discount factors from month labels.
[EconomicDefinition("MonthlySwapSide_v1")]
public sealed record MonthlySwapSide : Side
{
    public const string Implementation = "monthly_side_payments_v1";
    public const int MaximumMonths = 1200;
    [IdentityField] public ImmutableArray<Side> Periods { get; }
    [IdentityField] public MonthlyQuantityBasis QuantityBasis { get; }
    public ReferenceKey Currency { get; }
    public Quantity UnitQuantity { get; }
    public DateOnly DeliveryFrom => schedule.Payments[0].DeliveryFrom;
    public DateOnly DeliveryTo => schedule.Payments[^1].DeliveryTo;
    public bool IsFixed => Periods[0] is FixedSide;
    public override DefinitionIdentity PricingBindingIdentity => DefinitionIdentity.For(new MonthlyProjectionBindings(
        Periods.OfType<FloatingSide>().Select(p => p.PricingBinding).ToImmutableArray()));
    internal override ImmutableArray<Side> IdentityComponents => [this, .. Periods];
    private readonly SidePaymentSchedule schedule;
    public override ImmutableArray<CurveRequest> Dependencies => schedule.Dependencies;
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot) => schedule.DependenciesAt(snapshot);

    public MonthlySwapSide(Guid id, ImmutableArray<Side> periods, MonthlyQuantityBasis quantityBasis, string implementation) : base(id)
    {
        if (!Enum.IsDefined(quantityBasis))
            throw new MarketDataException("monthly_quantity_basis", "Select the explicit per-month or total-strip quantity allocation convention.");
        if (implementation != Implementation || periods.IsDefaultOrEmpty || periods.Length > MaximumMonths ||
            periods.Any(p => p is null || p.Id == id) || periods.Select(p => p.Id).Distinct().Count() != periods.Length ||
            !(periods.All(p => p is FixedSide) || periods.All(p => p is FloatingSide))) throw Invalid();
        var plans = periods.Select(p => (SidePaymentSchedule)p.GenerateSchedule()).ToImmutableArray();
        if (plans.Any(p => p.Payments.Length != 1)) throw Invalid();
        var payments = plans.Select(p => p.Payments[0]).ToImmutableArray();
        var first = payments[0];
        for (var n = 0; n < payments.Length; n++)
        {
            var p = payments[n];
            if (p.DeliveryFrom.Year is < 1901 or > 2198 || p.DeliveryFrom.Day != 1 || p.DeliveryFrom.AddMonths(1) != p.DeliveryTo ||
                p.PaymentAt.Year is < 1901 or > 2199 || p.Currency != first.Currency || p.UnitQuantity != first.UnitQuantity ||
                n > 0 && payments[n - 1].DeliveryTo != p.DeliveryFrom) throw Invalid();
        }
        if (periods[0] is FixedSide fixedFirst && periods.Cast<FixedSide>().Any(p => p.QuoteKey != fixedFirst.QuoteKey || p.Price.Unit != fixedFirst.Price.Unit) ||
            periods[0] is FloatingSide floatingFirst && periods.Cast<FloatingSide>().Any(p => p.Index != floatingFirst.Index || p.Quote != floatingFirst.Quote)) throw Invalid();
        Periods = periods; QuantityBasis = quantityBasis; Currency = first.Currency; UnitQuantity = first.UnitQuantity;
        var totalDays = payments.Sum(p => p.DeliveryTo.DayNumber - p.DeliveryFrom.DayNumber);
        var allocated = payments.Select((p, n) => new SidePaymentPlan(p, id, n, quantityBasis switch {
            MonthlyQuantityBasis.PerMonth => new(1, 1, UnitQuantity.Specification),
            MonthlyQuantityBasis.TotalEqualMonths => new(1, payments.Length, UnitQuantity.Specification),
            MonthlyQuantityBasis.TotalCalendarDays => new(p.DeliveryTo.DayNumber - p.DeliveryFrom.DayNumber, totalDays, UnitQuantity.Specification),
            _ => throw new MarketDataException("monthly_quantity_basis", "Unsupported monthly quantity allocation.")
        })).ToImmutableArray();
        schedule = new(id, Currency, UnitQuantity, Implementation, allocated);
    }
    public override SidePaymentSchedule GenerateSchedule() => schedule;
    public override PaymentSideValuation PV(SideValuationContext context) => schedule.Evaluate(context);
    private static MarketDataException Invalid() => new("monthly_swap_periods",
        "Supply one to 1200 complete, ordered, contiguous months of independent fixed or floating sides with exact common quote and quantity references.");
}

[EconomicDefinition("monthly_projection_bindings_v1")]
internal sealed record MonthlyProjectionBindings([property: IdentityField] ImmutableArray<FloatingProjectionBinding> Periods);

[EconomicDefinition("MonthlyFinancialSwap_v1")]
public sealed record MonthlyFinancialSwap : SidedInstrument
{
    public const string Implementation = "monthly_financial_swap_v1";
    [IdentityField] public bool Discounting { get; }
    public MonthlySwapSide Fixed => (MonthlySwapSide)Sides[0].Side;
    public MonthlySwapSide Floating => (MonthlySwapSide)Sides[1].Side;
    public override ReferenceKey Currency => Fixed.Currency;
    public override Quantity UnitQuantity => Fixed.UnitQuantity;
    public override DefinitionIdentity PricingBindingIdentity => Floating.PricingBindingIdentity;
    public MonthlyFinancialSwap(Guid id, ImmutableArray<SideMembership> sides, string implementation, bool discounting) : base(id, sides)
    {
        var one = ExactDecimal.Parse("1"); var minusOne = ExactDecimal.Parse("-1");
        if (implementation != Implementation || sides.Length != 2 || sides[0].Side is not MonthlySwapSide a ||
            sides[1].Side is not MonthlySwapSide b || !a.IsFixed || b.IsFixed || a.Id == b.Id ||
            !(sides[0].Coefficient == one && sides[1].Coefficient == minusOne || sides[0].Coefficient == minusOne && sides[1].Coefficient == one) ||
            a.Currency != b.Currency || a.UnitQuantity != b.UnitQuantity || a.QuantityBasis != b.QuantityBasis ||
            a.DeliveryFrom != b.DeliveryFrom || a.DeliveryTo != b.DeliveryTo ||
            a.IdentityComponents.Select(s => s.Id).Intersect(b.IdentityComponents.Select(s => s.Id)).Any())
            throw new MarketDataException("monthly_swap_instrument", "Pair complete fixed and floating monthly legs with identical delivery/allocation scope, exact currency/quantity and opposite unit coefficients.");
        Discounting = discounting;
    }
    public override InstrumentValuation PV(SideValuationContext context) => FinancialSwapInstrument.RequireDiscounting(base.PV(context), Discounting);
}
