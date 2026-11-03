using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Lng;

[EconomicDefinition("retained_lng_member_v1")]
public sealed record RetainedLngMember
{
    public Guid SideId { get; }
    public BookedSideProgram Program { get; }
    [IdentityField] public ExactDecimal Coefficient { get; }
    [IdentityField] public string ProgramDigest { get; }
    public RetainedLngMember(Guid sideId, ExactDecimal coefficient, BookedSideProgram program)
    {
        if (sideId == Guid.Empty || program is null ||
            coefficient != ExactDecimal.Parse("1") && coefficient != ExactDecimal.Parse("-1"))
            throw new MarketDataException("lng_membership", "A retained LNG member requires its exact program and explicit unit direction.");
        SideId = sideId; Coefficient = coefficient; Program = program;
        ProgramDigest = BookedSideProgram.Digest(program.Write());
    }
}

// Executes admitted content directly; no current Side or replacement generator is loaded.
[EconomicDefinition("retained_lng_instrument_v1")]
public sealed record RetainedLngInstrument : Instrument
{
    [IdentityField] public ImmutableArray<RetainedLngMember> Members { get; }
    [IdentityField] public LngValuationAssignment Assignment { get; }
    [IdentityField] public LngIncoterms Incoterms { get; }
    public ImmutableArray<SchedulePriceTerm> ActivePrices => [new(ExactDecimal.Parse("1"), null, Assignment.Lookup)];
    public override ReferenceKey Currency => Assignment.Quote.Currency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Assignment.Energy.Key);
    public override bool SupportsQuantityScaling => true;
    internal override bool RequiresNativeProgramMarket => true;
    public override string RetainedScheduleIdentity => "retained_side_programs_v1/" + string.Join('/', Members.Select(m => m.ProgramDigest));
    public override DefinitionIdentity PricingBindingIdentity => DefinitionIdentity.For(Assignment);

    public RetainedLngInstrument(Guid id, LngValuationAssignment assignment, LngIncoterms incoterms,
        ImmutableArray<RetainedLngMember> members) : base(id)
    {
        if (assignment is null || incoterms is null || incoterms.Edition.Revision != assignment.Key.Revision ||
            members.IsDefault || members.Length != 2 || members.Any(m => m is null) ||
            members[0].Program.Document.Family != "lng" || members[1].Program.Document.Family != "fixed" ||
            members[0].Program.Document.PricingBindingIdentity != DefinitionIdentity.For(assignment).Canonical)
            throw new MarketDataException("lng_membership", "Select the exact retained LNG cargo, independent fixed consideration and saved Incoterms.");
        LngProgramContract.RequireCash(members[1].Program.Document, assignment, members[0].Program.Document.Grids[0]);
        Assignment = assignment; Incoterms = incoterms; Members = members;
    }
    public override void ValidateBinding(Quantity quantity, OpenEtrm.MarketData.Power.DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity, coverage);
        if (quantity is null || quantity.Specification != UnitQuantity.Specification || quantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("lng_quantity", "Bind a positive total in the exact retained gross discharge-energy specification.");
    }
    public override UnitPricingContract Declare(UnitValuationContext context) => new(Id, "retained_lng_unit_pv_v1",
        PeriodPriceSelector.ProjectionAt(Assignment.Lookup, context.Snapshot) is {} market ? [market] : [],
        UnitQuantity.Specification, Currency) { ScheduleIdentity = RetainedScheduleIdentity };

    public override ExactMoney Value(UnitValuationContext context)
    {
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "Retained LNG implements contractual present value only.");
        var (basis, scenario) = context.RetainedProgramMarkets(Declare(context).Dependencies);
        var sources = ScheduleMarketSources.ForPhysicalLng(basis, [Assignment]);
        var total = ExactMoney.FromMoney(new(default, Currency));
        foreach (var member in Members)
        {
            var value = sources.Evaluate(member.Program, member.SideId, scenario, context.DiscountProvider, context.Cancellation);
            total = total.Add(value.PresentValue.Scale(member.Coefficient));
        }
        return total;
    }
}
