using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// These are authorized valuation/ledger projections, never another persisted trade model.
// AllocationId is the stable native allocation identity, not a payment header or a report-generated key.
// Flows retain all known allocations; recognition by effective time happens only in Compare.
public sealed record PnlPositionValue(AccountingPositionKey Position,ExactMoney RemainingValue);
public enum PnlAccountingCoverage { RemainingValueOnly=1,Complete }
public sealed record PnlAccountingPoint(Guid ContextId,Guid SnapshotId,DateTimeOffset At,DateTimeOffset KnownAt,
    ImmutableArray<PnlPositionValue> Positions,ImmutableArray<AccountingFlow> Flows,PnlAccountingCoverage Coverage)
{
    // Operation-local provenance: equal numbers alone cannot attest matching economic terms, quantity or lifecycle state.
    internal ImmutableArray<PreparedConnectedPortfolioRisk> ValueSources{get;init;}=[];
    public PnlAccountingPoint WithLedger(ImmutableArray<AccountingFlow> ledger)
    {
        if(ledger.IsDefault||ledger.Any(f=>f is null||f.KnownAt>KnownAt))throw new MarketDataException("pnl_cash_context","A complete known ledger requires its exact certified cutoff.");
        var rows=Flows.Concat(ledger).GroupBy(f=>f.AllocationId).ToArray();
        if(rows.Any(g=>g.Distinct().Count()!=1))throw new MarketDataException("pnl_cash_revision","Valuation and ledger projections disagree on an exact allocation revision.");
        return this with{Flows=[..rows.Select(g=>g.First()).OrderBy(f=>f.AllocationId)]};
    }
    public static PnlAccountingPoint Combine(ImmutableArray<PnlAccountingPoint> points)
    {
        if(points.IsDefaultOrEmpty||points.Any(p=>p is null))throw new MarketDataException("pnl_context","Supply explicit valued accounting partitions.");
        var first=points[0];
        if(points.Any(p=>(p.ContextId,p.SnapshotId,p.At,p.KnownAt)!=(first.ContextId,first.SnapshotId,first.At,first.KnownAt)))
            throw new MarketDataException("pnl_context","Accounting partitions must share one exact retained endpoint.");
        var values=points.SelectMany(p=>p.Positions).ToImmutableArray();var flows=points.SelectMany(p=>p.Flows).ToImmutableArray();
        if(values.Select(p=>p.Position).Distinct().Count()!=values.Length||flows.Select(f=>f.AllocationId).Distinct().Count()!=flows.Length)
            throw new MarketDataException("pnl_position","Accounting partitions cannot duplicate holdings or native allocations.");
        return first with{Positions=values,Flows=flows,Coverage=points.All(p=>p.Coverage==PnlAccountingCoverage.Complete)?PnlAccountingCoverage.Complete:PnlAccountingCoverage.RemainingValueOnly,
            ValueSources=[..points.SelectMany(p=>p.ValueSources).Distinct()]};
    }
}
public sealed record PnlAccountingPosition(AccountingPositionKey Position,ReferenceKey Currency,bool HeldAtOpening,bool HeldAtClosing,
    ExactMoney OpeningValue,ExactMoney ClosingValue,ExactMoney ValueMovement,ExactMoney PeriodCash,
    ExactMoney PriorCashKnowledgeAdjustment,ExactMoney NonEarningTransfers,ExactMoney EconomicPnl);
public sealed record PnlAccountingReport(PnlAccountingPoint Opening,PnlAccountingPoint Closing,string Policy,
    ImmutableArray<PnlAccountingPosition> Positions);

public static class TradingPnlAccounting
{
    public const string Version1="remaining_value_and_signed_cash_pnl_v1";
    // The caller supplies complete, authorized remaining-value and cash/transfer projections at both cutoffs.
    // A missing holding in a complete projection means no holding; missing accounting coverage never means zero cash.
    public static PnlAccountingReport Compare(PnlAccountingPoint opening,PnlAccountingPoint closing,string policy)
    {
        if(policy!=Version1)throw Error("pnl_policy","Select the explicit remaining-value and signed-cash reconciliation policy.");
        Require(opening);Require(closing);
        if(opening.At>=closing.At||opening.KnownAt>closing.KnownAt)
            throw Error("pnl_context","The closing instant must follow the opening instant, with a nondecreasing explicit knowledge cutoff.");
        var oldFlows=opening.Flows.ToDictionary(f=>f.AllocationId);var newFlows=closing.Flows.ToDictionary(f=>f.AllocationId);
        foreach(var (id,prior) in oldFlows)
        {
            if(!newFlows.TryGetValue(id,out var now)||prior.Position!=now.Position||prior.Kind!=now.Kind||prior.Amount.Currency!=now.Amount.Currency||
                now.Revision<prior.Revision||now.Revision==prior.Revision&&now!=prior||
                now.Revision>prior.Revision&&(now.KnownAt<=prior.KnownAt||now.KnownAt<=opening.KnownAt))
                throw Error("pnl_cash_revision","A known allocation needs its exact retained revision or a later-known correction; it cannot disappear or change ownership.");
        }
        foreach(var flow in closing.Flows)
            if(!oldFlows.ContainsKey(flow.AllocationId)&&flow.KnownAt<=opening.KnownAt)
                throw Error("pnl_cash_revision","A flow already effective and known at opening is missing from that complete projection.");
        var oldValues=opening.Positions.ToDictionary(p=>p.Position);var newValues=closing.Positions.ToDictionary(p=>p.Position);
        var currencies=new Dictionary<AccountingPositionKey,ReferenceKey>();
        void Currency(AccountingPositionKey position,ReferenceKey currency)
        {
            if(currencies.TryGetValue(position,out var previous)&&previous!=currency)
                throw Error("pnl_currency_transition","A position's cash and remaining value must retain one exact currency; translation requires an explicit separate policy.");
            currencies[position]=currency;
        }
        foreach(var position in opening.Positions.Concat(closing.Positions))Currency(position.Position,position.RemainingValue.Currency);
        foreach(var flow in opening.Flows.Concat(closing.Flows))Currency(flow.Position,flow.Amount.Currency);
        var flowsAtOpening=opening.Flows.Where(f=>f.EffectiveAt<=opening.At).ToLookup(f=>f.Position);
        var flowsAtClosing=closing.Flows.Where(f=>f.EffectiveAt<=closing.At).ToLookup(f=>f.Position);
        var rows=ImmutableArray.CreateBuilder<PnlAccountingPosition>(currencies.Count);
        foreach(var (position,currency) in currencies.OrderBy(p=>p.Key.BookId).ThenBy(p=>p.Key.TradeId).ThenBy(p=>p.Key.BindingId))
        {
            var zero=ExactMoney.Parse("0",currency);
            ExactMoney Sum(IEnumerable<AccountingFlow> flows,AccountingFlowKind kind)=>flows.Where(f=>f.Kind==kind).Select(f=>f.Amount).Aggregate(zero,(a,b)=>a.Add(b));
            var old=oldValues.TryGetValue(position,out var prior)?prior.RemainingValue:zero;
            var now=newValues.TryGetValue(position,out var current)?current.RemainingValue:zero;
            var movement=now.Subtract(old);
            var period=Sum(flowsAtClosing[position].Where(f=>f.EffectiveAt>opening.At),AccountingFlowKind.EconomicCash);
            var restated=Sum(flowsAtClosing[position].Where(f=>f.EffectiveAt<=opening.At),AccountingFlowKind.EconomicCash)
                .Subtract(Sum(flowsAtOpening[position],AccountingFlowKind.EconomicCash));
            var transfers=Sum(flowsAtClosing[position],AccountingFlowKind.NonEarningTransfer).Subtract(Sum(flowsAtOpening[position],AccountingFlowKind.NonEarningTransfer));
            rows.Add(new(position,currency,prior is not null,current is not null,old,now,movement,period,restated,transfers,
                movement.Add(period).Add(restated).Subtract(transfers)));
        }
        return new(opening,closing,policy,rows.ToImmutable());
    }
    private static void Require(PnlAccountingPoint point)
    {
        if(point is null||point.ContextId==Guid.Empty||point.SnapshotId==Guid.Empty||!TradingInstant.IsSupported(point.At)||!TradingInstant.IsSupported(point.KnownAt)||
            point.Positions.IsDefault||point.Flows.IsDefault)
            throw Error("pnl_context","Supply both exact retained contexts and complete known position and flow projections.");
        if(point.Coverage!=PnlAccountingCoverage.Complete)
            throw Error("pnl_accounting_coverage","Full trading P&L requires complete remaining value, signed cash and non-earning transfer coverage.");
        static bool Key(AccountingPositionKey key)=>key.TradeId!=Guid.Empty&&key.BindingId!=Guid.Empty&&key.BookId!=Guid.Empty;
        if(point.Positions.Any(p=>p is null||!Key(p.Position)||p.RemainingValue is null)||point.Positions.Select(p=>p.Position).Distinct().Count()!=point.Positions.Length)
            throw Error("pnl_position","Each retained holding requires one distinct exact trade/binding/book value.");
        if(point.Flows.Any(f=>f is null||f.AllocationId==Guid.Empty||f.Revision<=0||!Key(f.Position)||f.Amount is null||!Enum.IsDefined(f.Kind))||
            point.Flows.Select(f=>f.AllocationId).Distinct().Count()!=point.Flows.Length)
            throw Error("pnl_cash_revision","Each flow requires one explicitly resolved cash-allocation revision and exact owned amount.");
        if(point.Flows.Any(f=>!TradingInstant.IsSupported(f.EffectiveAt)||!TradingInstant.IsSupported(f.KnownAt)||f.KnownAt>point.KnownAt))
            throw Error("pnl_cash_context","A flow must be known at its selected endpoint and retain exact UTC effective and knowledge instants.");
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
