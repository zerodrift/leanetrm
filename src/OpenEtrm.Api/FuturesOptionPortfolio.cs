using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

// The futures-option valuation a calculation selects: the observation role options are calibrated and valued at, the
// tree and solver's numerical policy, the scenario policy that holds each series' calibration under futures shocks, and
// whether the intrinsic fallback is declared ("none" makes those series structured errors).
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionValuationPolicy(string ObservationRole,string NumericalPolicy,string ScenarioPolicy,string CalibrationFallback)
{
    public const string NoFallback="none";
    // The policy the Blotter's trading_workspace_v1 convention and the portfolio risk catalog state.
    public static FuturesOptionValuationPolicy Release1=>new(FuturesOptionPricing.DailySettlement,AmericanFuturesOptionModel.Id,
        FuturesOptionPricing.ScenarioPolicy,ImpliedVolatility.FallbackMode);
    internal bool FallbackDeclared=>CalibrationFallback==ImpliedVolatility.FallbackMode;
    internal OptionNumericalPolicy Validate()
    {
        if(ObservationRole!=FuturesOptionPricing.DailySettlement||ScenarioPolicy!=FuturesOptionPricing.ScenarioPolicy||
            CalibrationFallback is not(ImpliedVolatility.FallbackMode or NoFallback))
            throw new MarketDataException("option_policy",$"Select the {FuturesOptionPricing.DailySettlement} role, the {FuturesOptionPricing.ScenarioPolicy} "+
                $"scenario policy and an explicit calibration fallback ({ImpliedVolatility.FallbackMode} or {NoFallback}).");
        return OptionNumericalPolicies.Require(NumericalPolicy);
    }
}

// The option work of one valuation: held option positions, distinct series, the tree prices their calibration evaluated and
// the tree prices the scenario valuations evaluated at the held calibrations.
public sealed record FuturesOptionWork(int Positions,int UniqueSeries,int SolvedSeries,int FallbackSeries,int CalibrationTreePrices)
{
    public int ScenarioTreePrices{get;init;}
}

// One market's option and premium valuation inputs for a set of held positions: each distinct series calibrated once from
// the unshocked base, and the context's payment-discount selection for each currency. A position-specific failure is kept
// against its binding with its structured code; any other failure fails the preparation.
internal sealed class FuturesOptionMarket
{
    // The codes that concern one option series or one premium; a read lists the position and values the rest.
    internal static bool Excludable(string? code)=>ValuationStatus.PositionSpecific(code)||code is "option_settlement_missing" or "option_underlying_missing" or
        "option_settlement_below_intrinsic" or "option_settlement_above_bound" or "option_calibration_nonconvergent" or
        "option_calibration_fallback_undeclared" or "option_input" or "option_expired" or "discount_coverage";

    internal required ImmutableDictionary<Guid,FuturesOptionMarketBinding> Bindings{get;init;}
    internal required ImmutableDictionary<Guid,ExactDecimal> Settlements{get;init;}
    internal required ImmutableDictionary<Guid,string> OptionFailures{get;init;}
    internal required ImmutableDictionary<Guid,CashPayment> Premiums{get;init;}
    internal required ImmutableDictionary<Guid,string> PremiumFailures{get;init;}
    internal required ImmutableDictionary<ReferenceKey,CurrencyDefaultDiscounting> Discounting{get;init;}
    // The snapshot's valuation instant, at which premium cash is pending or settled for both its value and its state.
    internal required DateTimeOffset ValuationAt{get;init;}

    internal string PremiumState(Guid premium)=>Premiums[premium].Settled(ValuationAt)?"settled":"pending";

    internal ImmutableArray<FuturesOptionCalibrationReceipt> Receipts=>[..Bindings.Values.OrderBy(b=>b.Instrument.Id).Select(b=>b.Receipt)];
    internal ImmutableArray<PortfolioDiscountEvidence> DiscountInputs=>[..Discounting.Values.SelectMany(d=>d.Inputs)
        .OrderBy(p=>p.Key.SideId).Select(p=>new PortfolioDiscountEvidence(p.Key.SideId,p.Key,p.Value))];
    internal string? Mode(Guid option)=>Bindings.TryGetValue(option,out var binding)?binding.Receipt.Mode:null;

    // The tree prices every binding's valuations have evaluated so far.
    internal int TreePrices=>Bindings.Values.Sum(b=>b.TreePrices);

    // The structured reason an option binding has no value, or null when it is valued. A closed option binding has no value
    // to give.
    internal string? Reason(FuturesOptionPosition position)=>position.OpenLots==default?null:OptionFailures.GetValueOrDefault(position.OptionBinding);
    // Premium cash is valued unless its pending payment date lies outside the selected curve (discount_coverage).
    internal string? PremiumReason(FuturesOptionPosition position)=>PremiumFailures.GetValueOrDefault(position.PremiumBinding);

    internal ImmutableArray<ExcludedPosition> Unvalued(ImmutableArray<FuturesOptionPosition> positions)=>[..positions.SelectMany(p=>
        new[]{(Binding:p.OptionBinding,Reason:Reason(p)),(Binding:p.PremiumBinding,Reason:PremiumReason(p))}.Where(b=>b.Reason is not null)
            .Select(b=>new ExcludedPosition(p.Trade,b.Binding,p.Book.Id("id"),TradingReference.Trade(p.Head),FuturesOptionPositions.Type,b.Reason!)))];

    // The valued bindings: each option for its open lots and each trade's premium cash.
    internal ValuationPortfolio? Portfolio(ImmutableArray<FuturesOptionPosition> positions)
    {
        var trades=positions.Select(p=>
        {
            var held=ImmutableArray.CreateBuilder<TradeInstrument>();
            if(p.OpenLots!=default&&Reason(p) is null)held.Add(new(p.OptionBinding,Bindings[p.OptionBinding].Instrument,new(p.OpenLots,p.LotQuantity)));
            var premium=Premiums[p.PremiumBinding];
            if(PremiumReason(p) is null)held.Add(new(p.PremiumBinding,premium,new(p.PremiumQuantity,premium.Currency)));
            return new Trade(p.Trade,p.Book.Id("id"),held.ToImmutable());
        }).Where(t=>!t.Instruments.IsEmpty).ToImmutableArray();
        if(trades.IsEmpty)return null;
        var instruments=trades.SelectMany(t=>t.Instruments).Select(i=>i.Instrument).DistinctBy(i=>i.Id).ToArray();
        return ValuationPortfolio.FromTrades(trades,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
        {
            OptionBindings=instruments.OfType<FuturesOptionInstrument>().ToImmutableDictionary(i=>i.Id,i=>Bindings[i.Id]),
            // Settled cash is worth nothing further and selects no discounting.
            InstrumentDiscounting=instruments.OfType<CashPayment>().Where(i=>Discounting.TryGetValue(i.Currency,out var d)&&d.Inputs.ContainsKey(i.Payment))
                .ToImmutableDictionary(i=>i.Id,i=>(ISideDiscounting)Discounting[i.Currency])
        });
    }

    internal FuturesOptionWork Work(ImmutableArray<FuturesOptionPosition> positions)
    {
        var receipts=Receipts;
        return new(positions.Count(p=>p.OpenLots!=default&&Reason(p) is null),receipts.Length+OptionFailures.Count,
            receipts.Count(r=>r.Mode==FuturesOptionPricing.SolvedMode),receipts.Count(r=>r.Mode==ImpliedVolatility.FallbackMode),
            // A solved series prices the tree at both bracket ends and once per halving; the fallback once at the bracket's foot.
            receipts.Sum(r=>r.Mode==FuturesOptionPricing.SolvedMode?r.Iterations!.Value+2:1));
    }
}

internal static class FuturesOptionPortfolio
{
    internal static async Task<FuturesOptionMarket> Prepare(NpgsqlConnection connection,Guid catalog,PreviewData data,InputFrame frame,MarketSnapshot snapshot,
        ImmutableArray<FuturesOptionPosition> positions,FuturesOptionValuationPolicy policy,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var numerical=policy.Validate();
        if(positions.IsDefault||positions.Any(p=>p.Definition!=data.DefinitionRevision.RevisionId||p.Reference!=data.ReferenceRevision)||snapshot.Definitions!=data.DefinitionRevision)
            throw new MarketDataException("option_market_context","Option holdings require their exact market definition and reference revisions.");
        var options=positions.Select(p=>p.SeriesKey).Distinct().ToDictionary(key=>key,key=>new FuturesOptionInstrument(
            data.FuturesOptionSeries.SingleOrDefault(s=>s.Key==key)??throw new MarketDataException("option_definition",$"Option series {key.Code} is not in this market.")));
        if(positions.Any(p=>options[p.SeriesKey].Id!=p.OptionBinding))throw FuturesOptionPositions.Missing();
        var premiums=positions.Select(p=>p.Premium(options[p.SeriesKey].Series.Multiplier)).DistinctBy(p=>p.Id).ToImmutableDictionary(p=>p.Id);
        var unsettled=premiums.Values.Where(p=>!p.Settled(snapshot.ValuationAt)).ToArray();
        var failures=new Dictionary<Guid,string>();var premiumFailures=new Dictionary<Guid,string>();
        var held=positions.Where(p=>p.OpenLots!=default).Select(p=>options[p.SeriesKey]).DistinctBy(o=>o.Id).OrderBy(o=>o.Id).ToArray();
        foreach(var expired in held.Where(o=>snapshot.ValuationAt>=o.Series.Terms.ExpiryAt))failures[expired.Id]="option_expired";
        var live=held.Where(o=>!failures.ContainsKey(o.Id)).ToArray();
        var discounting=ImmutableDictionary.CreateBuilder<ReferenceKey,CurrencyDefaultDiscounting>();
        foreach(var currency in live.Select(o=>o.Currency).Concat(unsettled.Select(p=>p.Currency)).Distinct())
        {
            ImmutableArray<SidePaymentKey> keys=[..live.Where(o=>o.Currency==currency).Select(o=>new SidePaymentKey(o.Id,0,o.Series.Terms.ExpiryAt,currency)),
                ..unsettled.Where(p=>p.Currency==currency).Select(p=>p.Payment)];
            // A payment date the curve does not cover leaves out only the option or premium that owns it.
            discounting.Add(currency,CurrencyDefaultDiscounting.Covering(keys,snapshot,true,await SwapStore.CurrencyDefault(connection,catalog,data,frame,currency,ct),out var uncovered));
            foreach(var key in uncovered)(premiums.ContainsKey(key.SideId)?premiumFailures:failures)[key.SideId]="discount_coverage";
        }
        live=[..live.Where(o=>!failures.ContainsKey(o.Id))];
        var requests=new Dictionary<Guid,(CurveRequest Underlying,CurveRequest Settlement)>();
        foreach(var option in live)
        {
            try{requests[option.Id]=FuturesOptionPricing.Requests(data.Model,option.Series,policy.ObservationRole);}
            catch(MarketDataException error) when(error.Code is "option_settlement_missing" or "option_underlying_missing"){failures[option.Id]=error.Code;}
        }
        var (basis,unvalued)=ValuationStatus.BuildValued(selected=>data.Model.Build(snapshot,selected,ct),
            [..requests.Values.SelectMany(r=>new[]{r.Underlying,r.Settlement}).Distinct()]);
        var calibrated=new List<FuturesOptionInstrument>();
        foreach(var option in live.Where(o=>requests.ContainsKey(o.Id)))
        {
            var (underlying,settlement)=requests[option.Id];
            // A curve without its value at the close is the series' missing underlying or settlement.
            static string? Missing(ImmutableDictionary<CurveRequest,string> unvalued,CurveRequest request,string code)=>
                unvalued.TryGetValue(request,out var reason)?reason=="missing_observation"?code:reason:null;
            var reason=Missing(unvalued,underlying,"option_underlying_missing")??Missing(unvalued,settlement,"option_settlement_missing");
            if(reason is not null)failures[option.Id]=reason;else calibrated.Add(option);
        }
        // Each series calibrates independently from the same immutable base, so the series run in parallel. Every outcome,
        // failures included, is kept in series-identity order; the first failure that is not one series' own then fails the
        // preparation, exactly as a sequential pass in that order would, and never as a parallel AggregateException.
        var outcomes=calibrated.AsParallel().AsOrdered().Select(option=>
        {
            try{return(Option:option,Binding:FuturesOptionPricing.Prepare(option,basis!,snapshot,discounting[option.Currency],policy.ObservationRole,numerical,policy.FallbackDeclared,ct),Error:(Exception?)null);}
            catch(Exception error){return(Option:option,Binding:(FuturesOptionMarketBinding?)null,Error:error);}
        }).ToArray();
        if(outcomes.FirstOrDefault(o=>o.Error is {} error&&!(error is MarketDataException known&&FuturesOptionMarket.Excludable(known.Code))).Error is {} failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        var bindings=ImmutableDictionary.CreateBuilder<Guid,FuturesOptionMarketBinding>();
        var settlements=ImmutableDictionary.CreateBuilder<Guid,ExactDecimal>();
        foreach(var (option,binding,error) in outcomes)
        {
            if(error is MarketDataException excluded){failures[option.Id]=excluded.Code;continue;}
            bindings.Add(option.Id,binding!);
            settlements.Add(option.Id,basis!.Values.Single(v=>v.Request==requests[option.Id].Settlement).Price.Value);
        }
        return new(){Bindings=bindings.ToImmutable(),Settlements=settlements.ToImmutable(),OptionFailures=failures.ToImmutableDictionary(),
            Premiums=premiums,PremiumFailures=premiumFailures.ToImmutableDictionary(),Discounting=discounting.ToImmutable(),ValuationAt=snapshot.ValuationAt};
    }

    // The Blotter's base values of the held option and premium bindings, by trade and binding, through the same connected
    // valuation a portfolio risk run uses, under the release-1 option policy its convention states.
    internal static async Task<(FuturesOptionMarket Market,ImmutableDictionary<(Guid Trade,Guid Binding),ExactMoney> Values)> Values(NpgsqlConnection connection,
        PreviewDatabase database,PreviewData data,InputFrame frame,ImmutableArray<FuturesOptionPosition> positions,CancellationToken ct)
    {
        if(frame.ContextId is not {} contextId)throw new MarketDataException("option_market_context","Option values require the certified calculation context.");
        var context=PinnedContext.Exact((await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,contextId,ct)).Context);
        var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),contextId,SchedulePreview.ParseAt(context.KnownAt));
        var market=await Prepare(connection,database.Manifest.CatalogId,data,frame,snapshot,positions,FuturesOptionValuationPolicy.Release1,ct);
        if(market.Portfolio(positions) is not {} portfolio)return(market,ImmutableDictionary<(Guid,Guid),ExactMoney>.Empty);
        var dependencies=portfolio.Declare(snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
        var prepared=await CustomCurves.PrepareMarket(connection,database,data,frame,snapshot,context,portfolio.Instruments,dependencies,
            CustomCurves.MarketPreparation.ValuationDependencies,ct);
        var run=RiskEngine.CalculateExact(RiskEngine.PrepareConnected(prepared.Valuation,prepared.Bindings,[portfolio],prepared.Authority,ct),
            [],RiskEngine.ExactNumericalPolicy,ct,RiskDetail.Holdings);
        return(market,run.Groups.Single().BasePositions.ToImmutableDictionary(p=>(p.TradeId,p.BindingId),p=>p.Value));
    }
}
