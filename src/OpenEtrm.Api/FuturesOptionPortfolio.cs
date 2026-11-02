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

// The option work of one valuation: held option positions, distinct series and the tree prices their calibration evaluated.
public sealed record FuturesOptionWork(int Positions,int UniqueSeries,int SolvedSeries,int FallbackSeries,int CalibrationTreePrices);

// One market's option and premium valuation inputs for a set of held positions: each distinct series calibrated once from
// the unshocked base, and the context's payment-discount selection for each currency. A position-specific failure is kept
// against its binding with its structured code; any other failure fails the preparation.
internal sealed class FuturesOptionMarket
{
    // The codes that concern one option series or one premium; a read lists the position and values the rest.
    internal static bool Excludable(string? code)=>ValuationStatus.PositionSpecific(code)||code is "option_settlement_missing" or "option_underlying_missing" or
        "option_settlement_below_intrinsic" or "option_settlement_above_bound" or "option_calibration_nonconvergent" or
        "option_calibration_fallback_undeclared" or "option_input" or "option_expired" or "option_premium_due";

    internal required ImmutableDictionary<Guid,FuturesOptionMarketBinding> Bindings{get;init;}
    internal required ImmutableDictionary<Guid,ExactDecimal> Settlements{get;init;}
    internal required ImmutableDictionary<Guid,string> OptionFailures{get;init;}
    internal required ImmutableDictionary<Guid,FixedCashPayment> Premiums{get;init;}
    internal required ImmutableDictionary<Guid,string> PremiumFailures{get;init;}
    internal required ImmutableDictionary<ReferenceKey,CurrencyDefaultDiscounting> Discounting{get;init;}

    internal ImmutableArray<FuturesOptionCalibrationReceipt> Receipts=>[..Bindings.Values.OrderBy(b=>b.Instrument.Id).Select(b=>b.Receipt)];
    internal ImmutableArray<PortfolioDiscountEvidence> DiscountInputs=>[..Discounting.Values.SelectMany(d=>d.Inputs)
        .OrderBy(p=>p.Key.SideId).Select(p=>new PortfolioDiscountEvidence(p.Key.SideId,p.Key,p.Value))];
    internal string? Mode(Guid option)=>Bindings.TryGetValue(option,out var binding)?binding.Receipt.Mode:null;

    // The structured reason a held binding has no value, or null when it is valued. A closed option binding has no value to give.
    internal string? Reason(FuturesOptionPosition position,bool option)=>option
        ?position.OpenLots==default?null:OptionFailures.GetValueOrDefault(position.OptionBinding)
        :PremiumFailures.GetValueOrDefault(position.PremiumBinding);

    internal ImmutableArray<ExcludedPosition> Unvalued(ImmutableArray<FuturesOptionPosition> positions)=>[..positions.SelectMany(p=>new[]{
            (Binding:p.OptionBinding,Reason:Reason(p,true)),(Binding:p.PremiumBinding,Reason:Reason(p,false))}
        .Where(b=>b.Reason is not null).Select(b=>new ExcludedPosition(p.Trade,b.Binding,p.Book.Id("id"),TradingReference.Trade(p.Head),FuturesOptionPositions.Type,b.Reason!)))];

    // The valued bindings: each option for its open lots and each premium for its booked lots, or none when nothing is valued.
    internal ValuationPortfolio? Portfolio(ImmutableArray<FuturesOptionPosition> positions)
    {
        var trades=positions.Select(p=>
        {
            var held=ImmutableArray.CreateBuilder<TradeInstrument>();
            if(p.OpenLots!=default&&Reason(p,true) is null)held.Add(new(p.OptionBinding,Bindings[p.OptionBinding].Instrument,new(p.OpenLots,p.LotQuantity)));
            if(Reason(p,false) is null)held.Add(new(p.PremiumBinding,Premiums[p.PremiumBinding],new(p.Long?p.BookedLots:ExactDecimal.Parse("0")-p.BookedLots,p.LotQuantity)));
            return held.Count==0?null:new Trade(p.Trade,p.Book.Id("id"),held.ToImmutable());
        }).OfType<Trade>().ToImmutableArray();
        if(trades.IsEmpty)return null;
        var instruments=trades.SelectMany(t=>t.Instruments).Select(i=>i.Instrument).DistinctBy(i=>i.Id).ToArray();
        return ValuationPortfolio.FromTrades(trades,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
        {
            OptionBindings=instruments.OfType<FuturesOptionInstrument>().ToImmutableDictionary(i=>i.Id,i=>Bindings[i.Id]),
            InstrumentDiscounting=instruments.OfType<FixedCashPayment>().ToImmutableDictionary(i=>i.Id,i=>(ISideDiscounting)Discounting[i.Currency])
        });
    }

    internal FuturesOptionWork Work(ImmutableArray<FuturesOptionPosition> positions)
    {
        var receipts=Receipts;
        return new(positions.Count(p=>p.OpenLots!=default&&Reason(p,true) is null),receipts.Length+OptionFailures.Count,
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
        var premiums=positions.ToImmutableDictionary(p=>p.PremiumBinding,p=>p.Premium(options[p.SeriesKey].Series.Multiplier));
        var valuationDate=DateOnly.FromDateTime(snapshot.ValuationAt.UtcDateTime);
        // A premium is due on its payment date; its cash is then outside the instrument's value and needs its own lifecycle.
        var premiumFailures=premiums.Values.Where(p=>p.PaymentDate<=valuationDate).ToImmutableDictionary(p=>p.Id,_=>"option_premium_due");
        var failures=new Dictionary<Guid,string>();
        var held=positions.Where(p=>p.OpenLots!=default).Select(p=>options[p.SeriesKey]).DistinctBy(o=>o.Id).OrderBy(o=>o.Id).ToArray();
        foreach(var expired in held.Where(o=>snapshot.ValuationAt>=o.Series.Terms.ExpiryAt))failures[expired.Id]="option_expired";
        var live=held.Where(o=>!failures.ContainsKey(o.Id)).ToArray();
        var discounting=ImmutableDictionary.CreateBuilder<ReferenceKey,CurrencyDefaultDiscounting>();
        foreach(var currency in live.Select(o=>o.Currency).Concat(premiums.Values.Where(p=>!premiumFailures.ContainsKey(p.Id)).Select(p=>p.Currency)).Distinct())
        {
            ImmutableArray<SidePaymentKey> keys=[..live.Where(o=>o.Currency==currency).Select(o=>new SidePaymentKey(o.Id,0,o.Series.Terms.ExpiryAt,currency)),
                ..premiums.Values.Where(p=>p.Currency==currency&&!premiumFailures.ContainsKey(p.Id)).Select(p=>p.Payment)];
            discounting.Add(currency,new(keys,snapshot,true,await SwapStore.CurrencyDefault(connection,catalog,data,frame,currency,ct)));
        }
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
            // A settlement curve without its value at the close is the series' missing settlement.
            var reason=unvalued.GetValueOrDefault(underlying)??(unvalued.TryGetValue(settlement,out var missing)?
                missing=="missing_observation"?"option_settlement_missing":missing:null);
            if(reason is not null)failures[option.Id]=reason;else calibrated.Add(option);
        }
        // Each series calibrates independently from the same immutable base, so the series run in parallel; the outcomes
        // are taken in series order, and the first failure that is not one series' own fails the preparation.
        var outcomes=calibrated.AsParallel().AsOrdered().WithCancellation(ct).Select(option=>
        {
            try{return(Option:option,Binding:FuturesOptionPricing.Prepare(option,basis!,snapshot,discounting[option.Currency],policy.ObservationRole,numerical,policy.FallbackDeclared,ct),Error:(MarketDataException?)null);}
            catch(MarketDataException error){return(Option:option,Binding:(FuturesOptionMarketBinding?)null,Error:error);}
        }).ToArray();
        if(outcomes.FirstOrDefault(o=>o.Error is {} error&&!FuturesOptionMarket.Excludable(error.Code)).Error is {} failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        var bindings=ImmutableDictionary.CreateBuilder<Guid,FuturesOptionMarketBinding>();
        var settlements=ImmutableDictionary.CreateBuilder<Guid,ExactDecimal>();
        foreach(var (option,binding,error) in outcomes)
        {
            if(error is not null){failures[option.Id]=error.Code;continue;}
            bindings.Add(option.Id,binding!);
            settlements.Add(option.Id,basis!.Values.Single(v=>v.Request==requests[option.Id].Settlement).Price.Value);
        }
        return new(){Bindings=bindings.ToImmutable(),Settlements=settlements.ToImmutable(),OptionFailures=failures.ToImmutableDictionary(),
            Premiums=premiums,PremiumFailures=premiumFailures,Discounting=discounting.ToImmutable()};
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
