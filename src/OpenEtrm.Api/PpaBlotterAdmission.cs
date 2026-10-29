using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record PpaPriceDemand(Guid TradeId,Guid BindingId,DeliveryCoverage Coverage);
// Report-only admission. Strict PPA and risk consumers never construct this collector.
// It carries reasons, not prices or quantities, and never crosses an operation.
internal sealed class PpaBlotterAdmission
{
    private static readonly FrozenSet<string> MarketRefusals=new[]{"interval_block_coverage","interval_curve_coverage","interval_actual_missing"}.ToFrozenSet();
    private readonly Dictionary<Guid,HashSet<string>> refusals=[];
    internal IReadOnlyDictionary<Guid,HashSet<string>> Refusals=>refusals;
    internal bool Includes(Guid trade)=>!refusals.ContainsKey(trade);
    internal static bool NeedsRebooking(string code)=>code is "side_schedule_missing" or "side_schedule_unsupported";
    internal void Refuse(Guid trade,string code)
    {
        if(!NeedsRebooking(code)&&!MarketRefusals.Contains(code)&&code!="missing_curve_view")
            throw new MarketDataException("ppa_admission","Only explicitly supported report refusals may exclude a whole trade.");
        if(!refusals.TryGetValue(trade,out var reasons))refusals.Add(trade,reasons=[]);
        reasons.Add(code);
    }
    internal async Task Definitions(ImmutableArray<JsonElement> bindings,Func<Guid,Task> read)
    {
        foreach(var group in bindings.GroupBy(b=>b.Id("instrument_id")))
            try{await read(group.Key);}
            catch(MarketDataException error)when(NeedsRebooking(error.Code))
            {foreach(var trade in group.Select(b=>b.Id("trade_id")).Distinct())Refuse(trade,error.Code);}
    }
    // A failed union is attributed using only price-source admission, once per distinct
    // declared window set. No quantities, schedule graph or PV are prepared in a probe.
    // A failure which no individual declaration explains remains a hard refusal.
    internal async Task<PreparedCurve?> Price(ImmutableArray<PpaPriceDemand> demands,
        Func<ImmutableArray<DeliveryCoverage>,Task<PreparedCurve>> read)
    {
        demands=[..demands.Where(d=>Includes(d.TradeId))];
        if(demands.IsEmpty)return null;
        try{return await read(PpaReads.Union(demands.Select(d=>d.Coverage)));}
        catch(MarketDataException error)when(MarketRefusals.Contains(error.Code))
        {
            var explained=false;
            var scopes=demands.GroupBy(d=>d.BindingId).Select(g=>new{Demands=g.ToArray(),Spans=PpaReads.Union(g.Select(d=>d.Coverage))});
            foreach(var equivalent in scopes.GroupBy(s=>string.Join(';',s.Spans.Select(w=>$"{w.From.UtcTicks}:{w.To.UtcTicks}"))))
                try{await read(equivalent.First().Spans);}
                catch(MarketDataException alone)when(MarketRefusals.Contains(alone.Code))
                {
                    explained=true;
                    foreach(var trade in equivalent.SelectMany(s=>s.Demands).Select(d=>d.TradeId).Distinct())Refuse(trade,alone.Code);
                }
            if(!explained)throw;
            var remaining=PpaReads.Union(demands.Where(d=>Includes(d.TradeId)).Select(d=>d.Coverage));
            return remaining.IsEmpty?null:await read(remaining);
        }
    }
}
