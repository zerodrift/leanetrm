using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;
using Npgsql;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuantityInput(string Value,ReferenceKey Specification);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapDraftRequest(FuturesReadRequest Context,Guid DefinitionRevisionId,string Commodity,QuantityInput Quantity,
    string? FixedCoefficient,[property:JsonRequired]bool Discounting,SwapFixedInput? Fixed,SwapFloatingInput Floating,
    SwapFloatingInput? FirstFloating=null,string? SwapType=null,string? FirstCoefficient=null,ParRateInput? ParRatePolicy=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ParRateInput(string Implementation,[property:JsonRequired]int DecimalPlaces,string TieRule);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapDraftRiskRequest(SwapDraftRequest Draft,string BumpPolicy,string NumericalPolicy);

public sealed partial class SwapStore
{
    private static object[] DraftQuantities(PreviewData data,ReferenceKey target)
    {
        var converter=UnitOperations.Converter(new(data.ReferenceRevision,data.References));
        return data.References["quantity_specifications"].Where(q=>converter.CanConvert(new ReferenceKey(data.ReferenceRevision,q.Text("code")),target))
            .OrderBy(q=>q.Text("code")==target.Code?0:1).ThenBy(q=>q.Text("code"),StringComparer.Ordinal)
            .Select(q=>(object)new{specification=new ReferenceKey(data.ReferenceRevision,q.Text("code")),unit=data.References["units"].Single(u=>u.Text("code")==q.Text("unit_code")).Text("symbol")}).ToArray();
    }
    private sealed record PreparedDraft(PreviewData Data,InputFrame Frame,FinancialSwapInstrument Instrument,Quantity Entered,Quantity Quantity,CurrencyDiscountCurve? Discount,FinancialSwapValuation.ParSwapRate? ParRate);
    private async Task<PreparedDraft> PrepareDraft(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapDraftRequest body,CancellationToken ct,bool resolvePar=false)
    {
        if(body.Quantity is null||body.Floating is null)throw Error("swap_input","Supply explicit instrument quantity and both sides.");
        if(body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
        {
            var selected=One((await database.ReadSwapMarkets(connection,frame,ct,data)).Where(m=>m.Data.DefinitionRevision.RevisionId==body.DefinitionRevisionId));data=selected.Data;frame=selected.Frame;
        }
        var curve=One(data.Definitions["curves"].Where(c=>c.Text("code")==body.Floating.Curve));
        var quote=One(data.Quotes.Where(q=>q.Key.Code==curve.Text("quote_code")));
        var entered=new Quantity(ExactDecimal.Parse(body.Quantity.Value??""),body.Quantity.Specification);
        var converter=UnitOperations.Converter(new(data.ReferenceRevision,data.References));
        var quantity=entered.ConvertTo(quote.Quantity,converter);
        var missingPrice=body.Fixed is {} fixedTerms&&string.IsNullOrWhiteSpace(fixedTerms.Price);
        if(body.ParRatePolicy is not null&&!missingPrice||missingPrice&&(!resolvePar||body.ParRatePolicy is not {Implementation:FinancialSwapValuation.ParRatePolicy,DecimalPlaces:>=0 and <=18,TieRule:"to_even"}))
            throw Error("swap_par_rate","To price a blank fixed rate, supply explicit par-rate precision and ties to even. Risk requires the reviewed fixed rate.");
        var terms=new SwapWriteRequest(body.Context.SnapshotId,Convention,body.Context.KnowledgeMode,Guid.Empty,Guid.Empty,null,
            quantity.Amount.ToString(),body.FixedCoefficient,body.Discounting,missingPrice?body.Fixed! with{Price="0"}:body.Fixed,body.Floating,DefinitionRevisionId:body.DefinitionRevisionId,Commodity:body.Commodity,
            FirstFloating:body.FirstFloating,SwapType:body.SwapType,FirstCoefficient:body.FirstCoefficient);
        RequireWriteRules(data,terms);var instrument=Resolve(data,terms,null,null,null).Instrument;
        var discount=body.Discounting?await Discount(connection,data,frame,instrument.Currency,ct):null;
        FinancialSwapValuation.ParSwapRate? par=null;
        if(missingPrice)
        {
            var draft=new PreparedDraft(data,frame,instrument,entered,quantity,discount,null);
            var connected=await ConnectedDraft(connection,draft,data.Snapshot(frame),ct);
            par=FinancialSwapValuation.ParRate(instrument,connected.Valuation,connected.Bindings,connected.Authority,discount,new AmountFormat(body.ParRatePolicy!.DecimalPlaces,CashTieRule.ToEven,false,true),ct);
            instrument=Resolve(data,terms with{Fixed=terms.Fixed! with{Price=par.Price.Value.ToString()}}).Instrument;
        }
        return new(data,frame,instrument,entered,quantity,discount,par);
    }
    public async Task<object> PriceDraft(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapDraftRequest body,CancellationToken ct)
    {
        var draft=await PrepareDraft(connection,data,frame,body,ct,resolvePar:true);var snapshot=draft.Data.Snapshot(draft.Frame);
        var connected=await ConnectedDraft(connection,draft,snapshot,ct);
        var value=FinancialSwapValuation.Evaluate(draft.Instrument,connected.Valuation,connected.Bindings,connected.Authority,draft.Discount,ct);
        return new{unbooked=true,measure="present_value",enteredQuantity=draft.Entered,quantity=draft.Quantity,parRate=draft.ParRate,resolvedFixedPrice=draft.Instrument.First is FixedSide{Price:FlatPrice fixedPrice}?fixedPrice.Price.Value.ToString():null,
            currency=draft.Data.References["currencies"].Single(c=>c.Text("code")==draft.Instrument.Currency.Code).Text("display_code"),conversion=UnitConverter.Implementation,valuation=DraftValue(value,draft.Quantity),numericalPolicy=DealRisk.NumericalPolicy,bumpPolicy=DealRisk.BumpPolicy};
    }
    public async Task<object> RiskDraft(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapDraftRiskRequest body,CancellationToken ct)
    {
        var draft=await PrepareDraft(connection,data,frame,body.Draft,ct);var snapshot=draft.Data.Snapshot(draft.Frame);
        var connected=await ConnectedDraft(connection,draft,snapshot,ct);
        if(body.BumpPolicy!=DealRisk.BumpPolicy||body.NumericalPolicy!=DealRisk.NumericalPolicy)
            throw Error("risk_policy","Select the explicit quote-tick symmetric exact deal repricing policy.");
        var instrument=draft.Instrument;
        var connectedInputs=FinancialSwapValuation.CreateDiscounting(instrument,snapshot,draft.Discount);
        var portfolio=ValuationPortfolio.FromTrades([new Trade(instrument.Id,instrument.Id,[new TradeInstrument(instrument.Id,instrument,draft.Quantity)])],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,connectedInputs));
        var prepared=RiskEngine.PrepareConnected(connected.Valuation,connected.Bindings,[portfolio],connected.Authority,ct);
        if(prepared.Factors.Length>128)throw Error("risk_factor","A draft supports at most 128 explicit live quote factors.");
        var targets=PortfolioValuation.QuoteTargets(connected.Valuation.Scenario);
        var result=RiskEngine.CalculateExact(prepared,prepared.Factors.Select(f=>new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,
            targets[f.Input],f.Unit,f.TradingTick,ConnectedRiskBump.Convention)).ToImmutableArray(),RiskEngine.ExactNumericalPolicy,ct);
        var group=result.Groups.Single();
        var connectedRun=new DealRiskRun(group.BaseValues.Single().Value,group.Factors.Select((f,n)=>new DealFactorRisk(prepared.Factors[n],
            new(prepared.Factors[n].Input,prepared.Factors[n].Quote,f.Bump.Unit,f.Bump.Size,QuoteBump.Convention),f.Totals.Single().Values)).ToImmutableArray(),result.Work.ScenarioCount);
        return DraftRisk.Result(draft.Data,connectedRun,"present_value",ProjectionPolicyReceipts.For(draft.Instrument));
    }
    private async Task<CustomCurves.PreparedMarket> ConnectedDraft(NpgsqlConnection connection,PreparedDraft draft,MarketSnapshot snapshot,CancellationToken ct)
    {
        var dependencies=draft.Instrument.DependenciesAt(snapshot);
        if(draft.Frame.ContextId is not {} contextId)throw Error("swap_context","Connected draft pricing requires a retained financial context.");
        var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,contextId,ct)).Context;
        return await CustomCurves.PrepareMarket(connection,database,draft.Data,draft.Frame,snapshot,context,[draft.Instrument],dependencies,ct);
    }
}
