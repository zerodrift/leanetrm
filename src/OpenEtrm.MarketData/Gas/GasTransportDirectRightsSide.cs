using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

// A signed current whole-rights claim owns one admitted mark, not delivery or
// payment rows. Fees and owned gas remain independently valued instruments.
[EconomicDefinition("gas_transport_direct_rights_side_v1")]
public sealed record GasTransportDirectRightsSide:Side
{
    [IdentityField]public GasTransportDirectRightsTerms RightsTerms{get;}
    [IdentityField]public GasTransportDirectRightsState State{get;}
    [IdentityField]public DateTimeOffset Through{get;}
    [IdentityField]public string ExpiryPolicy{get;}
    [IdentityField]public GasTransportDirectRightsExpiry? Expiry{get;}
    public CapturedPriceSource? Source{get;}
    public GasTransportDirectRightsMark? Mark{get;}
    public GasTransportDirectRightsEvaluation? Evaluation{get;}
    public ReferenceKey Currency=>RightsTerms.Currency;
    public Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),RightsTerms.PackageQuantity);
    public override ImmutableArray<CurveRequest> Dependencies=>Source is {} source?[source.Market]:[];
    public override DefinitionIdentity PricingBindingIdentity=>DefinitionIdentity.For(new PriceBinding(Source?.Market,
        Source?.Snapshot.Id,Evaluation?.BaseScenarioId,Evaluation?.Context.ManifestId,Mark?.Observation.Id));
    private readonly GasTransportDirectRightsSchedule schedule;

    internal GasTransportDirectRightsSide(Guid id,GasTransportDirectRightsTerms terms,GasTransportDirectRightsState state,
        DateTimeOffset through,string expiryPolicy,GasTransportDirectRightsExpiry? expiry,CapturedPriceSource? source,
        GasTransportDirectRightsMark? mark,GasTransportDirectRightsEvaluation? evaluation):base(id,
            new(CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),SideCalendarPolicy.Implementation))
    {
        GasTransportDirectRightsValuation.RequireContract(terms,state);
        if(expiryPolicy!=GasTransportDirectRightsPolicy.ExpiryAtThrough||!TradingInstant.IsSupported(through))
            throw Error("expiry","Select the exact installed fixed-window expiry law and contractual rights horizon.");
        if(expiry is not null)
        {
            if(expiry.Id==Guid.Empty||expiry.PreviousLiveStateId==Guid.Empty||expiry.Id==expiry.PreviousLiveStateId||expiry.State!=state||
                expiry.Through!=through||expiry.EffectiveAt!=through||!TradingInstant.IsSupported(expiry.KnownAt)||expiry.KnownAt<through||
                source is not null||mark is not null||evaluation is not null)
                throw Error("expiry","A closed whole position requires its exact accepted expiry, state, prior live lineage and horizon, without a live mark.");
        }
        else
        {
            if(source is null||mark is null||evaluation is null||source.Point!=mark.Point||
                !source.Valuation.Scenario.Plan.Points.TryGetValue(source.Point,out var point)||
                point.Expression is not ConnectedSource direct||direct.Request!=source.Market||point.Contract?.Key!=source.Market.Contract)
                throw Error("mark","The live whole position requires its exact captured current rights mark.");
            _=GasTransportDirectRightsValuation.PV(terms,state,mark,evaluation,source.Valuation.Scenario);
            if(evaluation.Context.ValuationAt>=through)
                throw Error("expiry_required","The live rights window has ended. An explicit accepted expiry is required; the date alone supplies no zero value.");
        }
        RightsTerms=terms;State=state;Through=through;ExpiryPolicy=expiryPolicy;Expiry=expiry;Source=source;Mark=mark;Evaluation=evaluation;
        schedule=new(this);
    }

    public override GasTransportDirectRightsSchedule GenerateSchedule()=>schedule;
    public override GasTransportDirectRightsSideValuation PV(SideValuationContext context)=>schedule.Evaluate(context);
    internal void RequireSnapshot(MarketSnapshot snapshot)
    {
        if(Expiry is {} expiry)
        {
            if(snapshot.Knowledge is not {} known||snapshot.ValuationAt<expiry.EffectiveAt||known.KnownAt<expiry.KnownAt)
                throw Error("expiry","This read does not cover the exact accepted expiry fact.");
        }
        else if(snapshot.ValuationAt>=Through)
            throw Error("expiry_required","A live rights position at its horizon requires explicit accepted expiry.");
    }
    internal static MarketDataException Error(string suffix,string message)=>new("gas_transport_rights_"+suffix,message);
    [EconomicDefinition("gas_transport_direct_rights_price_binding_v1")]
    sealed record PriceBinding([property:IdentityField]CurveRequest? Market,[property:IdentityField]Guid? Snapshot,
        [property:IdentityField]string? BaseScenario,[property:IdentityField]string? Manifest,[property:IdentityField]Guid? Observation);
}

public sealed record GasTransportDirectRightsSchedule:SideSchedule
{
    public GasTransportDirectRightsSide Side{get;}
    public DateTimeOffset Through=>Side.Through;
    public override ImmutableArray<CurveRequest> Dependencies=>Side.Dependencies;
    public override ImmutableArray<SchedulePriceTerm> PriceTerms=>[];
    public override IReadOnlyList<ScheduleRow> Rows=>throw NoPaymentRows();
    internal GasTransportDirectRightsSchedule(GasTransportDirectRightsSide side)
        :base(side.Id,side.Currency,side.UnitQuantity,GasTransportDirectRightsInstrument.ModelVersion){Side=side;}
    public override GasTransportDirectRightsSideValuation Evaluate(SideValuationContext context)
    {
        if(context is null)throw new MarketDataException("side_context","Supply the exact whole-rights valuation context.");
        if(context.BindingState is not null)
            throw new MarketDataException("settlement_scope","A whole-rights claim has no payment lifecycle valuation.");
        Side.RequireSnapshot(context.Snapshot);
        var inputs=context.Inputs;
        if(inputs.ValuationAt!=context.Snapshot.ValuationAt)
            throw GasTransportDirectRightsSide.Error("time","The current rights profile has no projected or shifted-clock valuation.");
        if(Side.Expiry is not null)return new(this,context,ExactMoney.Parse("0",Currency));
        inputs.RequireSource(Side.Source!);
        return new(this,context,inputs.ExactPrice(Side.Source!.Market).ExactTimes(UnitQuantity));
    }
    internal static MarketDataException NoPaymentRows()=>GasTransportDirectRightsSide.Error("schedule",
        "A whole-rights mark retains its exact contractual horizon and expiry; it has no delivery or payment-row schedule.");
}

public sealed record GasTransportDirectRightsSideValuation:SideValuation
{
    internal GasTransportDirectRightsSideValuation(GasTransportDirectRightsSchedule schedule,SideValuationContext context,ExactMoney value)
        :base(schedule,context,value){}
    public override SideScheduleSnapshot InspectSchedule(string manifest,ExactDecimal scale,SignedRatio coefficient,Func<DateOnly,DateTimeOffset> boundary)=>
        throw GasTransportDirectRightsSchedule.NoPaymentRows();
}
