using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

public sealed partial class SwapStore
{
    internal sealed record ResolvedSwapHolding(JsonElement Row,FinancialSwapInstrument Instrument,Quantity Quantity,ISideDiscounting Discounting);

    internal async Task<ImmutableArray<ResolvedSwapHolding>> ReadRiskHoldings(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        ImmutableArray<Guid> bookIds,CancellationToken ct,MarketSnapshot? snapshot=null)
    {
        var books=await new FuturesStore(manifest).Books(connection,ct);
        FuturesStore.ValidateScope(books,bookIds,[]);
        var rows=(await Rows(connection,null,ct)).GroupBy(row=>row.Id("trade_id"))
            .Select(group=>group.MaxBy(row=>row.Number("revision"))!)
            .Where(row=>bookIds.IsEmpty||bookIds.Contains(row.Id("book_id"))).ToImmutableArray();
        snapshot??=data.Snapshot(frame);
        if(snapshot.Id!=frame.Id||snapshot.Definitions!=data.DefinitionRevision||snapshot.ValuationAt!=frame.At)
            throw Error("swap_context","Swap risk requires the exact selected model and snapshot context.");
        var identities=new InstrumentIdentityMap(manifest.CatalogId);
        var providers=new Dictionary<Guid,ISideDiscounting>();
        var result=ImmutableArray.CreateBuilder<ResolvedSwapHolding>(rows.Length);
        foreach(var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if(row.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId)
                throw Error("swap_risk_model","A held swap requires a different exact model revision. Supplemental markets are unsupported by this risk operation.");
            var terms=Terms(data,row,frame.Id);
            var resolved=Resolve(data,terms,row.Id("instrument_id"),row.Id("fixed_side_id"),row.Id("floating_side_id"));
            var instrument=identities.Retain(resolved.Instrument);
            if(instrument.Id!=row.Id("instrument_id"))
                throw Error("instrument_identity","Equivalent retained terms cannot replace an exact published instrument identity.");
            ValidateProjections(instrument.Floating.GenerateSchedule().Payments.SelectMany(payment=>payment.Prices).Select(price=>price.Lookup!.Contract.Key.Id),row);
            if(!providers.TryGetValue(instrument.Id,out var provider))
            {
                var mapping=instrument.Discounting?await Discount(connection,data,frame,instrument.Currency,ct):null;
                provider=FinancialSwapValuation.CreateDiscounting(instrument,snapshot,mapping);
                providers.Add(instrument.Id,provider);
            }
            result.Add(new(row,instrument,resolved.Quantity,provider));
        }
        return result.MoveToImmutable();
    }
}
