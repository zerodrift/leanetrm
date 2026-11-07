using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// Commodity definitions retain their own identity fields and membership rules.
public interface IRetainedProgramMember
{
    Guid SideId { get; }
    BookedSideProgram Program { get; }
    string ProgramDigest { get; }
    ExactDecimal ValuationCoefficient { get; }
    ExactRatio? ValuationRatio { get; }
}

// Shared contractual PV mechanics for independently valued retained programs.
// Source admission, units and physical policies stay with the concrete model.
public abstract record RetainedProgramInstrument<TMember> : Instrument where TMember : IRetainedProgramMember
{
    public abstract ImmutableArray<TMember> Members { get; }
    public override bool SupportsQuantityScaling => true;
    internal override bool RequiresNativeProgramMarket => true;
    public override string RetainedScheduleIdentity => "retained_side_programs_v1/" + string.Join('/', Members.Select(m => m.ProgramDigest));

    protected RetainedProgramInstrument(Guid id) : base(id) { }
    protected abstract ScheduleMarketSources Sources(CurveBuild basis);

    public override ExactMoney Value(UnitValuationContext context)
    {
        var evaluations = EvaluateMembers(context);
        var total = ExactMoney.FromMoney(new(default, Currency));
        for (var n = 0; n < Members.Length; n++) total = total.Add(Signed(Members[n], evaluations[n].PresentValue));
        return total;
    }

    // One unscaled evaluation per member, in member order, from the shared base and scenario markets.
    protected ImmutableArray<BookedSideEvaluation> EvaluateMembers(UnitValuationContext context)
    {
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "This retained instrument implements contractual present value only.");
        var (basis, scenario) = context.RetainedProgramMarkets(Declare(context).Dependencies);
        var sources = Sources(basis);
        return [..Members.Select(member => sources.Evaluate(member.Program, member.SideId, scenario, context.DiscountProvider, context.Cancellation))];
    }
    protected static ExactMoney Signed(TMember member, ExactMoney value) =>
        (member.ValuationRatio is {} ratio ? value.Scale(ratio) : value).Scale(member.ValuationCoefficient);

    // The started-delivery lifecycle has one supported consumer; other commodities name the missing policy.
    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if (state is PhysicalCashState)
            throw new MarketDataException("physical_lifecycle_unsupported", "This retained instrument has no started-delivery lifecycle policy.");
        base.ValidateValuationState(state);
    }
}
