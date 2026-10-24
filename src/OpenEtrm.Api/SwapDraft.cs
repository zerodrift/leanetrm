using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
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
public sealed record ParRateInput(string Implementation,int DecimalPlaces,string TieRule);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapDraftRiskRequest(SwapDraftRequest Draft,string BumpPolicy,string NumericalPolicy);

public sealed partial class SwapStore
{
    private static object[] DraftQuantities(PreviewData data,ReferenceKey target)
    {
        var source=data.References["quantity_specifications"].Single(q=>q.Text("code")==target.Code);
        return data.References["quantity_specifications"].Where(q=>q.Text("commodity_code")==source.Text("commodity_code")&&q.Text("basis_code")==source.Text("basis_code")&&q.Text("dimension_code")==source.Text("dimension_code"))
            .OrderBy(q=>q.Text("code")==target.Code?0:1).ThenBy(q=>q.Text("code"),StringComparer.Ordinal)
            .Select(q=>(object)new{specification=new ReferenceKey(data.ReferenceRevision,q.Text("code")),unit=data.References["units"].Single(u=>u.Text("code")==q.Text("unit_code")).Text("symbol")}).ToArray();
    }
    private sealed record PreparedDraft(PreviewData Data,InputFrame Frame,FinancialSwapInstrument Instrument,Quantity Entered,Quantity Quantity,MarketSnapshot Snapshot,CurrencyDiscountCurve? Discount,FinancialSwapValuation.ParSwapRate? ParRate);
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
            throw Error("swap_par_rate","Enter an agreed fixed price, or explicitly request par pricing with precision and ties to even. Risk requires the reviewed fixed rate.");
        var terms=new SwapWriteRequest(body.Context.SnapshotId,Convention,body.Context.KnowledgeMode,Guid.Empty,Guid.Empty,null,
            quantity.Amount.ToString(),body.FixedCoefficient,body.Discounting,missingPrice?body.Fixed! with{Price="0"}:body.Fixed,body.Floating,DefinitionRevisionId:body.DefinitionRevisionId,Commodity:body.Commodity,
            FirstFloating:body.FirstFloating,SwapType:body.SwapType,FirstCoefficient:body.FirstCoefficient);
        RequireWriteRules(data,terms);var instrument=Resolve(data,terms,null,null,null).Instrument;
        var snapshot=await SwapSnapshot(connection,data,frame,instrument,ct);
        var discount=body.Discounting?await Discount(connection,data,frame,instrument.Currency,ct):null;
        FinancialSwapValuation.ParSwapRate? par=null;
        if(missingPrice)
        {
            par=FinancialSwapValuation.ParRate(instrument,data.Model.Build(snapshot,instrument.DependenciesAt(snapshot)),discount,new AmountFormat(body.ParRatePolicy!.DecimalPlaces,CashTieRule.ToEven,false,true));
            instrument=Resolve(data,terms with{Fixed=terms.Fixed! with{Price=par.Price.Value.ToString()}}).Instrument;
        }
        return new(data,frame,instrument,entered,quantity,snapshot,discount,par);
    }
    public async Task<object> PriceDraft(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapDraftRequest body,CancellationToken ct)
    {
        var draft=await PrepareDraft(connection,data,frame,body,ct,resolvePar:true);var snapshot=draft.Snapshot;
        var market=draft.Data.Model.Build(snapshot,draft.Instrument.DependenciesAt(snapshot));
        var value=FinancialSwapValuation.Evaluate(draft.Instrument,market,draft.Instrument.Discounting,draft.Discount);
        var valuation=DraftValue(value,draft.Quantity);
        var receipt=await RetainValuation(connection,value.Instrument,draft.Quantity,body,valuation,ct);
        return new{unbooked=true,measure="present_value",enteredQuantity=draft.Entered,quantity=draft.Quantity,parRate=draft.ParRate,resolvedFixedPrice=(draft.Instrument.First as FixedSide)?.Price.Value.ToString(),
            currency=draft.Data.References["currencies"].Single(c=>c.Text("code")==draft.Instrument.Currency.Code).Text("display_code"),conversion=UnitConverter.Implementation,valuationReceiptId=receipt,valuation,numericalPolicy=DealRisk.NumericalPolicy,bumpPolicy=DealRisk.BumpPolicy};
    }
    public async Task<object> RiskDraft(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapDraftRiskRequest body,CancellationToken ct)
    {
        var draft=await PrepareDraft(connection,data,frame,body.Draft,ct);var snapshot=draft.Snapshot;
        var dependencies=draft.Instrument.DependenciesAt(snapshot);
        FinancialSwapEvaluation? baseValuation=null;
        ExactMoney Price(CurveBuild market)
        {
            var value=FinancialSwapValuation.Evaluate(draft.Instrument,market,draft.Instrument.Discounting,draft.Discount);
            baseValuation??=value;
            return value.Instrument.PresentValue.Scale(draft.Quantity.Amount);
        }
        DealRiskRun run;
        if(dependencies.IsEmpty&&snapshot.FixingContextId is not null)
        {
            if(body.BumpPolicy!=DealRisk.BumpPolicy||body.NumericalPolicy!=DealRisk.NumericalPolicy)
                throw Error("risk_policy","Choose the explicit supported quote-tick risk policy even when every commodity fixing is actual.");
            run=new(Price(draft.Data.Model.Build(snapshot,dependencies)),[],1);
        }
        else run=DealRisk.Calculate(draft.Data.Model,snapshot,dependencies,Price,body.BumpPolicy,body.NumericalPolicy,ct);
        var result=DraftRisk.Result(draft.Data,run,"present_value");
        if(snapshot.FixingContextId is null)return result;
        var valued=baseValuation??throw Error("index_fixing_receipt","Risk requires its completed base valuation.");
        var receipt=await RetainValuation(connection,valued.Instrument,draft.Quantity,body.Draft,new{valuation=DraftValue(valued,draft.Quantity),risk=result},ct,"fixing_aware_swap_risk_v1");
        var response=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!.AsObject();
        response["valuationReceiptId"]=System.Text.Json.JsonSerializer.SerializeToNode(receipt);return response;

    }
}
