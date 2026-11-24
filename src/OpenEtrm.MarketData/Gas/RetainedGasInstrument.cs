using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

[EconomicDefinition("retained_gas_member_v1")]
public sealed record RetainedGasMember : IRetainedProgramMember
{
    public Guid SideId { get; }
    public BookedSideProgram Program { get; }
    [IdentityField] public ExactDecimal Direction { get; }
    [IdentityField] public ExactRatio Ratio { get; }
    [IdentityField] public string ProgramDigest { get; }
    [IdentityField] public int? SourceSlot { get; }
    [IdentityField] public string? QuantityPolicy { get; }
    [IdentityField] public QuantityPart? Part { get; }
    ExactDecimal IRetainedProgramMember.ValuationCoefficient => Direction;
    ExactRatio? IRetainedProgramMember.ValuationRatio => Ratio;
    int? IRetainedProgramMember.ValuationSourceSlot => SourceSlot;
    string? IRetainedProgramMember.ValuationQuantityPolicy => QuantityPolicy;
    QuantityPart? IRetainedProgramMember.ValuationPart => Part;

    public RetainedGasMember(Guid sideId, ExactDecimal direction, ExactRatio ratio, BookedSideProgram program,
        int? sourceSlot = null, string? quantityPolicy = null, QuantityPart? part = null)
    {
        if (sideId == Guid.Empty || program is null || ratio is null || ratio.Numerator <= 0 ||
            (direction != ExactDecimal.Parse("1") && direction != ExactDecimal.Parse("-1")))
            throw new MarketDataException("gas_membership", "Retained gas members require a program, positive quantity ratio and explicit direction.");
        if(sourceSlot is < 0 || quantityPolicy is not null && (quantityPolicy != GasHeldQuantities.DeliverySchedulePolicy || program.Document.Family != "gas") ||
            part is not null && (program.Document.Family == "gas" || sourceSlot is null))
            throw new MarketDataException("gas_membership", "A delivery policy belongs to a physical member; a quantity part requires a payment with an explicit physical source.");
        SideId = sideId; Direction = direction; Ratio = ratio; Program = program;
        SourceSlot = sourceSlot; QuantityPolicy = quantityPolicy; Part = part;
        ProgramDigest = BookedSideProgram.Digest(program.Write());
    }
}

// Booked programs supply economics; the common PV/factor contracts supply risk.
[EconomicDefinition("retained_gas_instrument_v1")]
public sealed record RetainedGasInstrument : RetainedProgramInstrument<RetainedGasMember>
{
    [IdentityField] public override ImmutableArray<RetainedGasMember> Members { get; }
    [IdentityField] public ImmutableArray<GasValuationAssignment> Assignments { get; }
    // Existing component terms already own this connection. Keep legacy economic
    // bytes, while retaining the connection in the schedule's execution identity.
    public ImmutableArray<int> PhysicalSources { get; }
    public override string RetainedScheduleIdentity => PhysicalSources.IsDefault ? base.RetainedScheduleIdentity :
        base.RetainedScheduleIdentity + "/physical_sources_v1/" + string.Join('/',PhysicalSources.Select(s=>s.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    public ImmutableArray<IndexPriceLookup> PriceLookups { get; }
    public ImmutableArray<SchedulePriceTerm> ActivePrices => PhysicalLifecycle.ActivePrices(Members.Select(m=>m.Program),PriceLookups);
    public override ReferenceKey Currency => Assignments[0].Quote.Currency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Assignments[0].Quote.Quantity);

    public RetainedGasInstrument(Guid id, ImmutableArray<RetainedGasMember> members,
        ImmutableArray<GasValuationAssignment> assignments, ImmutableArray<IndexPriceLookup> priceLookups=default,
        ImmutableArray<int> physicalSources=default) : base(id,members,new(ExactDecimal.Parse("1"),First(assignments).Quote.Quantity),First(assignments).Quote.Currency)
    {
        if (members.IsDefaultOrEmpty || members.Any(m => m is null) || assignments.IsDefaultOrEmpty || assignments.Any(a => a is null) ||
            !members.Any(m => m.Program.Document.Family == "gas") ||
            members.Any(m => m.Program.Document.Family is not ("gas" or "fixed" or "floating")) ||
            assignments.Select(a => (a.Quote.Currency, a.Quote.Quantity)).Distinct().Count() != 1)
            throw new MarketDataException("gas_membership", "Select complete retained physical, fixed or indexed cash programs with their exact compatible assignments.");
        if(priceLookups.IsDefault&&members.Any(m=>m.Program.Document.Instructions.Any(i=>i.Output.Operation==CurvePriceScheduleSource.Operation)))
            throw new MarketDataException("side_market_sources","Bound programs require their exact authorized retained price lookups.");
        var hasQuantityTerms = members.Any(m => m.QuantityPolicy is not null || m.Part is not null);
        if(members.Where((m, slot) => hasQuantityTerms
            ? m.SourceSlot is not {} source || source >= members.Length || members[source].Program.Document.Family != "gas" ||
                m.Program.Document.Family == "gas" && source != slot
            : m.SourceSlot is not null).Any())
            throw new MarketDataException("gas_membership", "Quantity terms require every member's exact physical source; legacy members omit these terms.");
        Members = members; Assignments = assignments.Distinct().ToImmutableArray();
        if(physicalSources.IsDefault&&hasQuantityTerms)physicalSources=[..members.Select(m=>m.SourceSlot!.Value)];
        if(!physicalSources.IsDefault&&(physicalSources.Length!=members.Length||physicalSources.Where((source,slot)=>
            source<0||source>=members.Length||members[source].Program.Document.Family!="gas"||
            members[slot].Program.Document.Family=="gas"&&source!=slot||hasQuantityTerms&&members[slot].SourceSlot!=source).Any()))
            throw new MarketDataException("gas_membership","Every execution entry requires its exact physical source slot, consistent with its quantity terms.");
        PhysicalSources=physicalSources;
        PriceLookups=priceLookups.IsDefault?[..Assignments.Select(a=>a.Lookup).Distinct()]:priceLookups.Distinct().ToImmutableArray();
    }

    private static GasValuationAssignment First(ImmutableArray<GasValuationAssignment> assignments)=>
        !assignments.IsDefaultOrEmpty&&assignments[0] is {} first?first:
        throw new MarketDataException("gas_membership","Select complete retained gas assignment terms before constructing their Side definitions.");

    // Runtime Side execution retains the selected started-delivery measure
    // and original whole-day delivery-mark policy in its explicit model version.
    public override UnitPricingContract Declare(UnitValuationContext context) => RetainedDeclaration(new(Id,
        (context.BindingState is PhysicalCashState ? PhysicalCashState.Policy + "/" : "") +
        (Members.Select(m => m.QuantityPolicy).FirstOrDefault(p => p is not null) is {} policy ? policy + "/" : "") +
        (Members.Any(m => PhysicalLifecycle.ReadsDeliveryMarks(m.Program)) ? PeriodPriceSelector.DeliveryMarkPolicy + "/" : "")+"retained_gas_side_pv_v3",
        PhysicalLifecycle.PriceDependencies(Members.Select(member => member.Program), PriceLookups, context.Snapshot),
        UnitQuantity.Specification, Currency) { Time = ValuationTimeDependency.Pinned,  ScheduleIdentity = RetainedScheduleIdentity });

    private protected override RetainedProgramContext PrepareSides(UnitValuationContext context,CurveBuild basis,CurveBuild scenario)
    {
        var input=new RetainedProgramContext(Sources(basis),scenario);
        if(context.BindingState is not PhysicalCashState cash)return input;
        var quantity=new SignedRatio(cash.Quantity.Amount.ScaledValue,ExactDecimal.WorkingScale);
        ImmutableArray<PhysicalLifecycleGeometry> geometries;
        if(PhysicalSources.IsDefault)
        {
            // Older direct callers have only the pooled, mutually compatible
            // geometry. Persisted and draft compositions supply exact sources.
            var shared=PhysicalLifecycle.PrepareGeometry(Members.Where(m=>m.Program.Document.Family=="gas").Select(m=>m.Program),context.Snapshot);
            geometries=[..Enumerable.Repeat(shared,Members.Length)];
        }
        else
        {
            var owners=PhysicalSources.Distinct().ToDictionary(slot=>slot,slot=>PhysicalLifecycle.PrepareGeometry([Members[slot].Program],context.Snapshot));
            geometries=[..PhysicalSources.Select(slot=>owners[slot])];
        }
        return input with{Cash=cash,Geometries=geometries,
            HeldFactors=[..Members.Select(m=>quantity*new SignedRatio(m.Direction.ScaledValue,ExactDecimal.WorkingScale)*new SignedRatio(m.Ratio.Numerator,m.Ratio.Denominator))]};
    }
    // Each Side owns its own accrued/paid rows. The Instrument only aggregates
    // signed membership amounts in the held binding's exact quantity basis.
    public PhysicalLifecycleValue EvaluateLifecycle(UnitValuationContext context)
    {
        if(context?.BindingState is not PhysicalCashState state)
            throw new MarketDataException(PhysicalLifecycle.ContextRefusal,"Lifecycle valuation requires the held binding's explicit physical cash state.");
        ValidateValuationState(state);
        return PhysicalLifecycle.FromValuation(EvaluateSides(context));
    }
    public override ExactMoney Value(UnitValuationContext context)
    {
        if(context.BindingState is not PhysicalCashState)
        {
            ValidateValuationState(context.BindingState);
            PhysicalLifecycle.RequireUnstarted(Members.Select(m=>m.Program),context.Snapshot.ValuationAt);
            var unstarted=EvaluateSides(context);context.RecordEntries(unstarted);return unstarted.PresentValue;
        }
        var value=EvaluateSides(context);context.RecordEntries(value);var zero=ExactMoney.Parse("0",Currency);
        ExactMoney Component(Func<PhysicalLifecycleValue,ExactMoney> select)=>value.Sides.Aggregate(zero,(sum,side)=>
            sum.Add(select(((RetainedProgramValuation)side.Value).Lifecycle!).Scale(side.QuantityRatio).Scale(side.Coefficient)));
        context.RecordComponents([new(PhysicalLifecycle.UnrealizedComponent,Component(p=>p.Unrealized)),new(PhysicalLifecycle.RealizedUnsettledComponent,Component(p=>p.RealizedUnsettled))]);
        return value.PresentValue;
    }

    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if (state is not PhysicalCashState cash)
        {
            if(Members.Any(m => m.QuantityPolicy is not null || m.Part is not null))
                throw PhysicalDeliveryQuantities.Invalid("An instrument with quantity terms requires its complete held owner-day inputs.");
            base.ValidateValuationState(state); return;
        }
        if (cash.Quantity.Specification != UnitQuantity.Specification || cash.Payments.Any(p => p.Slot >= Members.Length ||
                Members[p.Slot].Program.Document.Family == "gas" || p.Amount.Currency != Currency ||
                !Members[p.Slot].Program.Document.Instructions.Any(i => i.Output is { Operation: "output_guard", Grid: null } && i.Output.PaymentOrdinal == p.Occurrence)))
            throw new MarketDataException("payment_state", "Each payment allocation must name a retained cash occurrence of this instrument in its currency and quantity.");
        PhysicalDeliveryQuantities.RequireSchedules(Members, cash);
    }

    protected override ScheduleMarketSources Sources(CurveBuild basis) => new(basis.Graph.Plan.Context, basis, null, null,
        IntervalKnowledgePolicy.ResolvedCutoff, PendingActualsPolicy.RequireActuals,
        PriceLookups, [], Assignments);
}
