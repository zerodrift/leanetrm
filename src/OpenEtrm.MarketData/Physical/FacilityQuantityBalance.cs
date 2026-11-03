using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Physical;

public enum FacilityConstraintState { Unassessed = 1, WithinLimit, Breached }

// A same-basis native-interval assessment. Allocation never creates another flow
// or cures an observed operating breach; corrections may expose negative capacity.
public sealed record FacilityQuantityBalance(Quantity Allocated, Quantity? Available,
    FacilityConstraintState Constraint, Quantity? Excess)
{
    public const string Implementation = "native_interval_maximum_v1";

    public FacilityQuantityBalance RequireAvailable()
    {
        if(Available is null)throw Invalid();
        if(Available.Amount.CompareTo(default)<0)
            throw new MarketDataException("facility_quantity_overallocated","The allocations exceed the reported native quantity.");
        return this;
    }

    public static FacilityQuantityBalance Evaluate(ReferenceKey specification, Quantity? source,
        ImmutableArray<Quantity> allocations, Quantity? maximum)
    {
        Money.RequireKey(specification);
        if (allocations.IsDefault) throw Invalid();
        var values = allocations.Concat(new[] {source, maximum}.OfType<Quantity>());
        if (values.Any(q => q is null || q.Specification != specification || q.Amount.CompareTo(default) < 0)) throw Invalid();
        var total = allocations.Aggregate(new Quantity(default, specification), (sum, q) => sum.Add(q));
        var available = source is null ? null : new Quantity(source.Amount - total.Amount, specification);
        if (source is null || maximum is null) return new(total, available, FacilityConstraintState.Unassessed, null);
        var excess = source.Amount.CompareTo(maximum.Amount) > 0 ? source.Amount - maximum.Amount : default;
        return new(total, available, excess.CompareTo(default) > 0 ? FacilityConstraintState.Breached : FacilityConstraintState.WithinLimit,
            new(excess, specification));
    }

    private static MarketDataException Invalid() => new("facility_quantity_input", "Supply nonnegative quantities in one exact native specification.");
}

public sealed record FacilityTargetCapacity(Guid ObligationId,int TradeRevision,ExactQuantity Maximum)
{
    public void Require(int revision,Quantity quantity)
    {
        if(ObligationId==Guid.Empty||TradeRevision<1||Maximum is null||Maximum.NumeratorValue<=0)
            throw new MarketDataException("facility_quantity_input","Load a qualified target and its exact retained quantity.");
        if(revision!=TradeRevision)throw new MarketDataException("facility_quantity_conflict","The target trade revision changed.");
        if(quantity is null||quantity.Specification!=Maximum.Specification||quantity.Amount.CompareTo(default)<=0)
            throw new MarketDataException("facility_quantity_target","Use a positive allocation in the target's exact native quantity specification.");
        var exact=ExactQuantity.FromQuantity(quantity);
        if(exact.NumeratorValue*Maximum.DenominatorValue>Maximum.NumeratorValue*exact.DenominatorValue)
            throw new MarketDataException("facility_quantity_target","The allocation exceeds the target's retained native quantity.");
    }
}
