using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Lng;

[EconomicDefinition("retained_lng_member_v1")]
public sealed record RetainedLngMember : IRetainedProgramMember
{
    ExactDecimal IRetainedProgramMember.ValuationCoefficient => Coefficient;
    OpenEtrm.Shared.Numerics.ExactRatio? IRetainedProgramMember.ValuationRatio => null;
    int? IRetainedProgramMember.ValuationSourceSlot => null;
    string? IRetainedProgramMember.ValuationQuantityPolicy => null;
    QuantityPart? IRetainedProgramMember.ValuationPart => null;
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

// Each Side executes its admitted content directly without a replacement generator.
[EconomicDefinition("retained_lng_instrument_v1")]
public sealed record RetainedLngInstrument : RetainedProgramInstrument<RetainedLngMember>
{
    [IdentityField] public override ImmutableArray<RetainedLngMember> Members { get; }
    [IdentityField] public LngValuationAssignment Assignment { get; }
    [IdentityField] public LngIncoterms Incoterms { get; }
    public ImmutableArray<SchedulePriceTerm> ActivePrices => [new(ExactDecimal.Parse("1"), null, Assignment.Lookup)];
    public override ReferenceKey Currency => Assignment.Quote.Currency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Assignment.Energy.Key);
    public override DefinitionIdentity PricingBindingIdentity => DefinitionIdentity.For(Assignment);

    public RetainedLngInstrument(Guid id, LngValuationAssignment assignment, LngIncoterms incoterms,
        ImmutableArray<RetainedLngMember> members) : base(id,members,new(ExactDecimal.Parse("1"),Required(assignment).Energy.Key),Required(assignment).Quote.Currency)
    {
        if (assignment is null || incoterms is null || incoterms.Edition.Revision != assignment.Key.Revision ||
            members.IsDefault || members.Length != 2 || members.Any(m => m is null) ||
            members[0].Program.Document.Family != "lng" || members[1].Program.Document.Family != "fixed" ||
            members[0].Program.Document.PricingBindingIdentity != DefinitionIdentity.For(assignment).Canonical)
            throw new MarketDataException("lng_membership", "Select the exact retained LNG cargo, independent fixed consideration and saved Incoterms.");
        LngProgramContract.RequireCash(members[1].Program.Document, assignment, members[0].Program.Document.Grids[0]);
        Assignment = assignment; Incoterms = incoterms; Members = members;
    }
    private static LngValuationAssignment Required(LngValuationAssignment? assignment)=>assignment??
        throw new MarketDataException("lng_membership","Select the exact retained source assignment before constructing its Side definitions.");

    public override void ValidateBinding(Quantity quantity, OpenEtrm.MarketData.Power.DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity, coverage);
        if (quantity is null || quantity.Specification != UnitQuantity.Specification || quantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("lng_quantity", "Bind a positive total in the exact retained gross discharge-energy specification.");
    }
    public override UnitPricingContract Declare(UnitValuationContext context) => RetainedDeclaration(new(Id, "retained_lng_side_pv_v2",
        PeriodPriceSelector.ProjectionAt(Assignment.Lookup, context.Snapshot) is {} market ? [market] : [],
        UnitQuantity.Specification, Currency) { Time = ValuationTimeDependency.Pinned,  ScheduleIdentity = RetainedScheduleIdentity });

    protected override ScheduleMarketSources Sources(CurveBuild basis) => ScheduleMarketSources.ForPhysicalLng(basis,[Assignment]);
}
