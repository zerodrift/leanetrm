using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

// One product's part of a Blotter valuation: how many positions it holds, known before any is valued, and its rows in units
// of completed work. A unit holds whole trades. Units is enumerated once.
internal interface IBlotterProductRun
{
    int Positions{get;}
    IAsyncEnumerable<ImmutableArray<JsonObject>> Units(CancellationToken ct);
}

// One Blotter valuation over one bound read: its products valued in the Blotter's order (futures, swaps and heat-rate swaps,
// then PPAs, gas and crude), and the report of all their rows. trading/workspace collects its units; the stream sends each as it completes.
internal sealed class BlotterRun(FuturesBlotterView view,ImmutableArray<FuturesBlotterView> summaries,FuturesStore.FuturesBlotterRun? futures,
    SwapStore.SwapBlotterRun? swaps,PpaBlotter.PpaBlotterRun? ppas,Action<JsonObject> futureTerms,
    Dictionary<Guid,Dictionary<string,DealAmountFormats.Selection>> formats,IBlotterProductRun? gas=null,IBlotterProductRun? crude=null)
{
    // A run of an empty directory has no products (D160).
    internal static BlotterRun Empty(FuturesBlotterView view,ImmutableArray<FuturesBlotterView> summaries)=>new(view,summaries,null,null,null,_=>{},[]);

    internal int Positions{get;}=(futures?.Positions??0)+(swaps?.Positions??0)+(ppas?.Positions??0)+(gas?.Positions??0)+(crude?.Positions??0);

    // Each unit's rows, decorated as the Blotter lists them.
    internal async IAsyncEnumerable<ImmutableArray<JsonObject>> Units([EnumeratorCancellation]CancellationToken ct)
    {
        foreach(var (product,future) in new (IBlotterProductRun? Product,bool Future)[]{(futures,true),(swaps,false),(ppas,false),(gas,false),(crude,false)})
        {
            if(product is null)continue;
            await foreach(var unit in product.Units(ct).WithCancellation(ct))
            {
                foreach(var row in unit)
                {
                    if(future)futureTerms(row);
                    TradingBlotter.AmountFormats(row,formats.GetValueOrDefault(Guid.Parse(row["tradeId"]!.GetValue<string>())));
                }
                yield return unit;
            }
        }
    }

    // The report and summaries of the complete rows, which Units gave in order.
    internal TradingBlotter.BlotterRead Finish(IReadOnlyList<JsonObject> rows)
    {
        if(rows.Count!=Positions)throw new InvalidOperationException($"The run valued {rows.Count} positions of the {Positions} it holds.");
        if(rows.Select(r=>r["tradeId"]!.GetValue<string>()+r["bindingId"]!.GetValue<string>()).Distinct().Count()!=rows.Count)throw TradingBlotter.Invalid();
        List<JsonObject> all=[..rows];
        return new(TradingBlotter.Project(view,all,futures?.EvaluatedContracts??0,futures?.Totals??new JsonArray(),
            new(swaps?.Positions??0,swaps?.Valuations??0,swaps?.SupplementalMarketLoads??0)),TradingBlotter.Summarize(summaries,all));
    }

    // The whole run, collected.
    internal async Task<TradingBlotter.BlotterRead> Collect(CancellationToken ct)
    {
        var rows=new List<JsonObject>();
        await foreach(var unit in Units(ct))rows.AddRange(unit);
        return Finish(rows);
    }
}
