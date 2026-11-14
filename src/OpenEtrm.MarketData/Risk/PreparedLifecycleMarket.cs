using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Risk;

// A retained native market graph, independent of which economic owner may use
// it after an event. Path quotes still run through the ordinary scenario engine.
internal sealed class PreparedLifecycleMarket
{
    internal MarketSnapshot SourceSnapshot=>valuation.Snapshot;
    internal PreparedValuationEnvironment Basis=>basis;
    internal ImmutableArray<QuoteFactor> Factors{get;}
    internal ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> KnownFixings{get;}
    internal ImmutableDictionary<ReferenceKey,ISideDiscounting> CashCurves{get;}
    internal RiskHistoryMarket Market=>new(SourceSnapshot.Definitions,SourceSnapshot.Id,valuation.Scenario.Plan.ManifestId);
    private readonly PreparedValuationEnvironment basis;
    private readonly ConnectedValuation valuation;
    private readonly ImmutableDictionary<CurveRequest,CurvePointKey> bindings;
    private readonly CurveBuild authority;
    private readonly ImmutableDictionary<InputKey,CurvePointKey> primitives;
    internal ImmutableDictionary<InputKey,DateTimeOffset> FinalInstants{get;}
    internal ImmutableDictionary<InputKey,(DateTimeOffset At,ExactDecimal Shift)> ActualFinals{get;}
    internal bool Admits(CurveRequest dependency,PreparedLifecycleMarket selected,CancellationToken ct)=>
        ReferenceEquals(this,selected)||valuation.AdmitsInputs(selected.SourceSnapshot)&&
        bindings.TryGetValue(dependency,out var point)&&selected.bindings.TryGetValue(dependency,out var original)&&
        valuation.Scenario.Plan.HasSameCalculation(point,selected.valuation.Scenario.Plan,original,ct);
    private readonly LifecycleProjectionPolicy Policy;
    private readonly DateTimeOffset Horizon;
    internal PreparedLifecycleMarket(LifecycleProjectionMarket selectedMarket,DateTimeOffset horizon,LifecycleProjectionPolicy policy,
        ImmutableDictionary<CurveRequest,DateTimeOffset> uses,ImmutableArray<LifecycleInputFinal> selected,
        ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> known,CancellationToken ct)
    {
        valuation=selectedMarket.Valuation;bindings=selectedMarket.Bindings;authority=selectedMarket.Authority;
        var cashCurves=selectedMarket.CashCurves;CashCurves=cashCurves;Horizon=horizon;Policy=policy;
        if(cashCurves.Any(p=>p.Value is not IProjectionCurrencyCurve curve||curve.Currency!=p.Key||
            !curve.SourceSnapshot.HasSameInputs(SourceSnapshot)||curve.TimingPolicy!=RateDiscountTiming.Version))
            throw LifecycleProjection.Error("projection_cash_policy","Select captured native currency curves with explicit exact-instant timing; scalar factors cannot define a path cash policy.");
        if(valuation.Scenario.CalculationAt!=SourceSnapshot.ValuationAt||!authority.Snapshot.HasSameInputs(SourceSnapshot))
            throw LifecycleProjection.Error("projection_source","Projection requires the original certified source clock and compatible retained market authority.");
        basis=valuation.Market(bindings,authority,ct,authority.BindConnected(valuation,bindings,ct));
        primitives=valuation.Scenario.Plan.Points.Values.Where(p=>p.Expression is ConnectedSource {Input.Use:InputUse.Variable})
            .GroupBy(p=>((ConnectedSource)p.Expression).Input).ToImmutableDictionary(g=>g.Key,g=>g.First().Key);
        Factors=basis.Factors([..uses.Keys],ct);
        if(selected.Any(f=>f is null||!valuation.Scenario.Plan.Points.Values.Any(p=>p.Expression is ConnectedSource c&&c.Request==f.Market)||
            !TradingInstant.IsSupported(f.At)||f.At<authority.Model.ContractDefinition(f.Market.Contract).Expiry||
            authority.Model.AssetDefinition(f.Market.Curve).Rule!=CurveRule.Direct||authority.Model.ContractDefinition(f.Market.Contract).Kind!=ContractKind.Future)||
            selected.GroupBy(f=>f.Market).Any(g=>g.Select(f=>f.At).Distinct().Count()!=1||g.Select(f=>f.Actual).Where(a=>a is not null).Distinct().Count()>1))
            throw LifecycleProjection.Error("projection_input_expiry","Retain consistent exact native input-final declarations under the selected contract policy.");
        var configured=authority.Model.LifecycleFinals(uses,SourceSnapshot.ValuationAt,
            selected.DistinctBy(f=>f.Market).ToImmutableDictionary(f=>f.Market,f=>f.At),LifecycleProjection.ExpiryTreatment(policy));
        var finalTimes=ImmutableDictionary.CreateBuilder<InputKey,DateTimeOffset>();
        var absolute=ImmutableDictionary.CreateBuilder<InputKey,(DateTimeOffset,ExactDecimal)>();var actualFixings=known.ToBuilder();
        foreach(var (request,at) in configured)
        {
            if(!bindings.TryGetValue(request,out var point))
                point=valuation.Scenario.Plan.Points.Values.Single(p=>p.Expression is ConnectedSource c&&c.Request==request&&c.Input.Use==InputUse.Variable).Key;
            if(valuation.Scenario.Plan.Points[point].Expression is not ConnectedSource {Input.Use:InputUse.Variable} primitive)
                throw LifecycleProjection.Error("projection_input_expiry","The selected final transition requires an admitted native primitive source.");
            if(finalTimes.TryGetValue(primitive.Input,out var previous)&&previous!=at)
                throw LifecycleProjection.Error("projection_input_expiry","One native input cannot have conflicting final-settlement instants.");
            finalTimes[primitive.Input]=at;
            var declaration=selected.FirstOrDefault(f=>f.Market==request&&f.Actual is not null);
            if(declaration?.Actual is not {} actual)continue;
            var official=authority.Model.AssetDefinition(actual.OfficialMarket.Curve);var observed=actual.Observation;
            var quote=authority.Model.QuoteFor(actual.OfficialMarket);
            if(actual.OfficialMarket.Contract!=request.Contract||official.Rule!=CurveRule.Direct||observed is null||
                observed.Key.CatalogId!=SourceSnapshot.Definitions.CatalogId||observed.Key.Id==Guid.Empty||
                observed.Series!=primitive.Input.Series||observed.Source!=official.DirectSource||observed.Role!=ObservationRole.FinalContractSettlement||
                observed.State!=PublicationState.Final||observed.ObservedAt<at||observed.ObservedAt!=declaration.At||actual.SourceContextId!=SourceSnapshot.Knowledge!.ContextId||
                !TradingInstant.IsSupported(actual.KnownAt)||actual.KnownAt<observed.ObservedAt||actual.KnownAt>SourceSnapshot.Knowledge.KnownAt||
                observed.Value.CompareTo(quote.Minimum)<0||observed.Value.CompareTo(quote.Maximum)>0||!observed.Value.IsMultipleOf(quote.ClearingIncrement)||
                PriceUnit.FromQuote(quote)!=PriceUnit.FromQuote(primitive.Quote))
                throw LifecycleProjection.Error("projection_input_expiry","A known final requires exact official source, series, native quotation, occurrence and certified knowledge evidence.");
            var value=(observed.ObservedAt,observed.Value-primitive.Observation.Value);
            if(absolute.TryGetValue(primitive.Input,out var old)&&old!=value)
                throw LifecycleProjection.Error("projection_input_expiry","One native input cannot have conflicting certified final observations.");
            absolute[primitive.Input]=value;
            actualFixings[new(request,ObservationRole.FinalContractSettlement,observed.ObservedAt)]=ExactUnitPrice.FromPrice(new(observed.Value,primitive.Quote));
        }
        KnownFixings=actualFixings.ToImmutable();
        FinalInstants=finalTimes.ToImmutable();ActualFinals=absolute.ToImmutable();
    }
    internal Path OpenPath(ImmutableArray<QuoteShift> terminalChanges,ImmutableDictionary<InputKey,DateTimeOffset> finalInstants,
        ImmutableDictionary<InputKey,(DateTimeOffset At,ExactDecimal Shift)> actualFinals,CancellationToken ct)
    {
        var markets=new Dictionary<DateTimeOffset,PreparedValuationEnvironment>();
        PreparedValuationEnvironment At(DateTimeOffset at)
        {
            if(at<SourceSnapshot.ValuationAt||at>Horizon)throw LifecycleProjection.Error("projection_clock","The path read is outside its common horizon.");
            if(markets.TryGetValue(at,out var cached))return cached;
            var sizes=terminalChanges.Where(s=>primitives.ContainsKey(s.Input)).ToDictionary(s=>s.Input,s=>Policy.PricePath==LifecycleProjectionPolicy.Hold?s.Size:
                Scale(s.Size,(finalInstants.TryGetValue(s.Input,out var final)&&final<at?final:at).Ticks-SourceSnapshot.ValuationAt.Ticks,Horizon.Ticks-SourceSnapshot.ValuationAt.Ticks));
            foreach(var (input,actual) in actualFinals)if(primitives.ContainsKey(input)&&at>=actual.At)sizes[input]=actual.Shift;
            var shifts=sizes.Where(s=>s.Value!=default).Select(s=>
            {
                var native=(ConnectedSource)valuation.Scenario.Plan.Points[primitives[s.Key]].Expression;
                return new QuoteShift(s.Key,native.Quote.Key,PriceUnit.FromQuote(native.Quote),s.Value,QuoteShift.Convention);
            }).ToImmutableArray();
            var changes=shifts.Select(s=>
            {
                if(!primitives.TryGetValue(s.Input,out var point))throw LifecycleProjection.Error("projection_scenario","The declared factor has no admitted native primitive node.");
                return new CurveShift(CurveShockCoordinate.MarketQuote,new PointCurveTarget(point.Curve,[point.PointId]),s.Unit,s.Size,CurveShift.ParentAdditive);
            }).ToImmutableArray();
            var child=changes.IsEmpty?valuation:valuation.WithScenario(valuation.Scenario.WithShifts(changes,ct));
            var market=changes.IsEmpty?basis:child.Market(bindings,authority,ct,authority.BindConnected(child,bindings,ct));
            markets.Add(at,market);return market;
        }
        void Freeze(ProjectedFixing fact)
        {
            if(!bindings.TryGetValue(fact.Key.Market,out var point)||valuation.Scenario.Plan.Points[point].Expression is not ConnectedSource {Input.Use:InputUse.Variable} primitive)
                throw LifecycleProjection.Error("projection_fixing","A projected final contract settlement requires its explicitly selected native primitive market.");
            if(!finalInstants.TryGetValue(primitive.Input,out var existing)||existing>fact.Key.At)
                throw LifecycleProjection.Error("projection_fixing","A final fixing requires its declared native-expiry input transition before the selected final event.");
            foreach(var time in markets.Keys.Where(time=>time>fact.Key.At).ToArray())markets.Remove(time);
        }
        return new(At,Freeze);
    }
    internal sealed record Path(Func<DateTimeOffset,PreparedValuationEnvironment> At,Action<ProjectedFixing> Freeze);
    private static ExactDecimal Scale(ExactDecimal value,long elapsed,long horizon)=>ExactDecimal.FromScaled(
        IntegerRounding.RoundQuotient(value.ScaledValue*elapsed,new BigInteger(horizon),MidpointRounding.ToEven));
}
