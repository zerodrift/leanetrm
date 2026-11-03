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
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "This retained instrument implements contractual present value only.");
        var (basis, scenario) = context.RetainedProgramMarkets(Declare(context).Dependencies);
        var sources = Sources(basis);
        var total = ExactMoney.FromMoney(new(default, Currency));
        foreach (var member in Members)
        {
            var value = sources.Evaluate(member.Program, member.SideId, scenario, context.DiscountProvider, context.Cancellation).PresentValue;
            if (member.ValuationRatio is {} ratio) value = value.Scale(ratio);
            total = total.Add(value.Scale(member.ValuationCoefficient));
        }
        return total;
    }
}
