using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;

internal static class FacilityQuantityChecks
{
    internal static void Run()
    {
        var specification = new ReferenceKey(new(Guid.NewGuid(), Guid.NewGuid()), "synthetic_native_energy");
        Quantity Q(string amount) => new(ExactDecimal.Parse(amount), specification);
        FacilityQuantityBalance Balance(string source, params string[] claims) =>
            FacilityQuantityBalance.Evaluate(specification, Q(source), [..claims.Select(Q)], Q("90"));
        var actual = Balance("100", "60", "25", "10");
        Equal(actual.Allocated, "95"); Equal(actual.Available!, "5"); Equal(actual.Excess!, "10");
        if (actual.Constraint != FacilityConstraintState.Breached) throw new Exception("Allocating an actual must preserve its operating breach.");
        var corrected = Balance("90", "60", "25", "10");
        Equal(corrected.Available!, "-5");
        if (corrected.Constraint != FacilityConstraintState.WithinLimit) throw new Exception("Conservation and operating limits are distinct checks.");
        if (FacilityQuantityBalance.Evaluate(specification, Q("100"), [], null).Constraint != FacilityConstraintState.Unassessed)
            throw new Exception("A missing limit cannot report successful compliance.");
        if (FacilityQuantityBalance.Evaluate(specification, null, [], Q("90")).Available is not null)
            throw new Exception("A missing observation is not zero physical supply.");
        Equal(Balance("1", "0.1", "0.2", "0.3").Available!, "0.4");
        Reject(() => Balance("-1"));
        Reject(() => corrected.RequireAvailable(),"facility_quantity_overallocated");
        var target=new FacilityTargetCapacity(Guid.NewGuid(),1,ExactQuantity.FromQuantity(Q("5")).Scale(new OpenEtrm.Shared.Numerics.ExactRatio(1,3)));
        target.Require(1,Q("1.5"));
        Reject(()=>target.Require(1,Q("2")),"facility_quantity_target");
        Reject(()=>target.Require(2,Q("1")),"facility_quantity_conflict");
        Reject(() => FacilityQuantityBalance.Evaluate(specification, Q("1"), default, Q("90")));
        Reject(() => FacilityQuantityBalance.Evaluate(specification, Q("1"), [], new(ExactDecimal.Parse("1"), specification with {Code="other_basis"})));
        Console.WriteLine("PASS facility quantities: source conservation, preserved actual breach, missing-input assessment and exact native arithmetic");
    }
    private static void Equal(Quantity value, string expected)
    { if (value.Amount.ToString()!=expected) throw new Exception($"Expected {expected}, got {value.Amount}"); }
    private static void Reject(Action action,string code="facility_quantity_input")
    { try { action(); } catch(MarketDataException e) when(e.Code==code) { return; } throw new Exception("Expected explicit facility input refusal."); }
}
