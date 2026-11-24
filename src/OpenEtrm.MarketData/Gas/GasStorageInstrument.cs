using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.MarketData.Gas;

// One gas day of a booked contract: its bounds, the month it is paid with and the curve contracts its flows are priced at.
public sealed record GasStorageContractDay(DeliveryInterval GasDay, DateOnly PaymentFrom, DateOnly PaymentTo,
    CurveRequest Injection, CurveRequest Withdrawal, ExactDecimal InjectionAdjustment=default, ExactDecimal WithdrawalAdjustment=default);
// Commodity charges are per unit delivered in or out; the capacity charge is per unit of contracted capacity per month.
public sealed record GasStorageCharges(UnitPrice Injection, UnitPrice Withdrawal, UnitPrice MonthlyCapacity);

// A held storage contract valued on its plan. The plan must be one the entitlement admits; its daily quantities
// are then literal terms of ordinary operating Sides, which own every schedule, price and payment:
// withdrawals sold and injections bought at their curve contracts, and the three charges at fixed prices.
[EconomicDefinition("gas_storage_instrument_v1")]
public sealed record GasStorageInstrument : SidedInstrument
{
    public GasStoragePlanResult Plan { get; }
    [IdentityField] public override ReferenceKey Currency { get; }
    [IdentityField] public override Quantity UnitQuantity { get; }
    public override bool SupportsQuantityScaling => false;
    public override bool SupportsFixedUnitBinding => true;
    internal override bool SupportsSettlement => false;

    private GasStorageInstrument(Guid id, GasStoragePlanResult plan, QuoteSpecification quote, ImmutableArray<SideMembership> sides) : base(id, sides)
    {
        Plan = plan; Currency = quote.Currency; UnitQuantity = new(ExactDecimal.Parse("1"), quote.Quantity);
    }

    public static GasStorageInstrument Compose(Guid id, GasStorageEntitlement entitlement, GasStorageState opening, GasStorageTerminal terminal,
        QuoteSpecification quote, PaymentRule payment, GasStorageCharges charges, ImmutableArray<GasStorageContractDay> days, ImmutableArray<GasStorageDay> plan)
    {
        if (quote is null || charges is null || payment is null || days.IsDefaultOrEmpty || plan.IsDefault || days.Length != plan.Length ||
            quote.Quantity != entitlement?.Quantity || days.Zip(plan).Any(p => p.First is null || p.Second is null || p.First.GasDay != p.Second.GasDay))
            throw new MarketDataException("gas_storage_terms", "Supply the contract's quote in its own quantity, its charges and payment rule, and one plan row for each gas day.");
        var admitted = GasStoragePlans.Evaluate(entitlement, opening, terminal, plan, GasStoragePlans.Implementation);
        var unit = new Quantity(ExactDecimal.Parse("1"), quote.Quantity);
        var calendars = new SideCalendarPolicy(CalendarBinding.NotApplicable(), CalendarBinding.NotApplicable(), CalendarBinding.NotApplicable(), SideCalendarPolicy.Implementation);
        OperatingSideTerms Terms(IEnumerable<OperatingSidePeriod> periods) =>
            new(unit, quote, payment, ComponentKind.Payment, [.. periods], OperatingSideTerms.SupportedImplementation, calendars);
        IEnumerable<OperatingSidePeriod> Flows(Func<GasStorageDay, Quantity> quantity, Func<GasStorageContractDay, CurveRequest>? projection,
            Func<GasStorageContractDay,ExactDecimal>? adjustment=null) => days.Select((day,i)=>{
                var amount=new OperatingQuantityTerm(null,quantity(plan[i]));
                if(adjustment is not null && adjustment(day)!=default && projection is not null)
                    return new LocationalOperatingSidePeriod(day.GasDay,day.PaymentFrom,day.PaymentTo,amount,projection(day),adjustment(day));
                return new OperatingSidePeriod(day.GasDay,day.PaymentFrom,day.PaymentTo,amount,projection?.Invoke(day));
            });
        // Capacity is charged once for each payment month the term touches, on that month's first gas day.
        var months = days.GroupBy(day => day.PaymentFrom).Select(month => month.First())
            .Select(day => new OperatingSidePeriod(day.GasDay, day.PaymentFrom, day.PaymentTo, new(null, entitlement.MaximumInventory), null));
        Guid Side(int n) => OperationIdentity.For("gas_storage_side_v1", id, n);
        var one = ExactDecimal.Parse("1"); var minus = ExactDecimal.Parse("-1");
        return new(id, admitted, quote, [
            new(new FloatingSide(Side(0), Terms(Flows(day => day.Withdrawal, day => day.Withdrawal, day => day.WithdrawalAdjustment))), one),
            new(new FloatingSide(Side(1), Terms(Flows(day => day.Injection, day => day.Injection, day => day.InjectionAdjustment))), minus),
            new(new FixedSide(Side(2), Terms(Flows(day => day.Injection, null)), new FlatPrice(charges.Injection)), minus),
            new(new FixedSide(Side(3), Terms(Flows(day => day.Withdrawal, null)), new FlatPrice(charges.Withdrawal)), minus),
            new(new FixedSide(Side(4), Terms(months), new FlatPrice(charges.MonthlyCapacity)), minus)]);
    }

    // The instant a month's flows and charges are paid under the contract's payment rule.
    public static DateTimeOffset PaymentAt(PaymentRule payment, DateOnly from, DateOnly to) => FixedSide.PaymentBoundary(payment, from, to);

    public override void ValidateBinding(Quantity quantity, DeliveryCoverage? coverage)
    {
        if (quantity != UnitQuantity)
            throw new MarketDataException("gas_storage_quantity", "A storage contract is one exclusive right, bound exactly once.");
        base.ValidateBinding(quantity, coverage);
    }
    // Once the first gas day begins, planned flows become measured ones this profile does not own.
    protected internal override void RequireBeforeValuation(SideValuationContext context)
    {
        if (Plan.Opening.At <= context.Snapshot.ValuationAt)
            throw new MarketDataException("gas_storage_started", "A started storage term needs recorded flows and inventory, which this profile does not own.");
    }
}
