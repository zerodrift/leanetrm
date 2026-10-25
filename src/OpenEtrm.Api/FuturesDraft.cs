using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesDraftRequest(FuturesReadRequest Context,string Curve,Guid ContractId,string Lots,string ExecutionPrice);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesDraftRiskRequest(FuturesDraftRequest Draft,string BumpPolicy,string NumericalPolicy);

public sealed partial class FuturesStore
{
    private static (FuturesMarketBinding Binding,Trade Trade) Draft(PreviewData data,InputFrame frame,FuturesDraftRequest body)
    {
        var binding=Binding(data,body.Curve,body.ContractId,body.ContractId,body.Context.Convention!);
        var position=FuturesExecution.Bind(Guid.NewGuid(),binding,ExactDecimal.Parse(body.Lots??""),new(ExactDecimal.Parse(body.ExecutionPrice??""),binding.Quote),frame.At,FuturesSettlementState.Unprocessed);
        return(binding,new(Guid.NewGuid(),Guid.NewGuid(),[position]));
    }
    public Task<object> PriceDraft(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesDraftRequest body,CancellationToken ct)
    {
        var draft=Draft(data,frame,body);var snapshot=data.Snapshot(frame);var market=data.Model.Build(snapshot,draft.Binding.Dependencies);
        var holding=FuturesHoldings.Price([draft.Trade],snapshot,market,body.Context.Convention!)[0];
        return Task.FromResult<object>(new{unbooked=true,measure="execution_pnl",executionPnl=holding.ExecutionPnl.Amount.ToString(),
            quantity=holding.Quantity,currency=data.References["currencies"].Single(c=>c.Text("code")==holding.ExecutionPnl.Currency.Code).Text("display_code"),
            mark=holding.Mark.Value.ToString(),priceTick=draft.Binding.Quote.ClearingIncrement.ToString(),
            qualification="Unprocessed execution P&L and open exposure. Variation margin and settlement PV are not implemented.",numericalPolicy=DealRisk.NumericalPolicy,bumpPolicy=DealRisk.BumpPolicy});
    }
    public Task<object> RiskDraft(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesDraftRiskRequest body,CancellationToken ct)
    {
        var draft=Draft(data,frame,body.Draft);var snapshot=data.Snapshot(frame);
        if(body.BumpPolicy!=DealRisk.BumpPolicy||body.NumericalPolicy!=DealRisk.NumericalPolicy)
            throw Error("risk_policy","Select the explicit quote-tick symmetric exact deal repricing policy.");
        var (market,portfolio)=FuturesRisk.PreparePortfolio([draft.Trade],snapshot,data.Model,FuturesRisk.Implementation,ct);
        var prepared=RiskEngine.Prepare(market,[portfolio],ct);
        if(prepared.Factors.Length is <1 or >128)throw Error("risk_factor","A projected draft requires one to 128 live declared quote factors.");
        var result=RiskEngine.CalculateExact(prepared,prepared.Factors.Select(f=>new QuoteBump(f.Input,f.Quote,f.Unit,f.TradingTick,QuoteBump.Convention)).ToImmutableArray(),
            RiskEngine.ExactNumericalPolicy,ct);
        var group=result.Groups.Single();
        var risk=new DealRiskRun(group.BaseValues.Single().Value,group.Factors.Select((f,n)=>new DealFactorRisk(prepared.Factors[n],f.Bump,f.Totals.Single().Values)).ToImmutableArray(),result.Work.ScenarioCount);
        return Task.FromResult(DraftRisk.Result(data,risk,"execution_pnl"));
    }
}
