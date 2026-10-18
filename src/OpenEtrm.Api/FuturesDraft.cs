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
    private static (FuturesMarketBinding Binding,FuturesTrade Trade) Draft(PreviewData data,InputFrame frame,FuturesDraftRequest body)
    {
        var binding=Binding(data,body.Curve,body.ContractId,body.ContractId,body.Context.Convention!);
        var position=new FuturesPosition(Guid.NewGuid(),binding,ExactDecimal.Parse(body.Lots??""),new(ExactDecimal.Parse(body.ExecutionPrice??""),binding.Quote),frame.At,FuturesSettlementState.Unprocessed);
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
        // Qualify live eligibility against the actual base. Scenario marks are
        // synthetic and use the same domain execution-P&L pricer directly.
        _=FuturesHoldings.Price([draft.Trade],snapshot,data.Model.Build(snapshot,draft.Binding.Dependencies),body.Draft.Context.Convention!);
        var position=draft.Trade.Instruments[0];
        var risk=DealRisk.Calculate(data.Model,snapshot,draft.Binding.Dependencies,
            market=>ExactMoney.FromMoney(draft.Binding.ExecutionPnl(market.Values.Single(v=>v.Request==draft.Binding.Market).Price,position.ExecutionPrice,position.Lots)),body.BumpPolicy,body.NumericalPolicy,ct);
        return Task.FromResult(DraftRisk.Result(data,risk,"execution_pnl"));
    }
}
