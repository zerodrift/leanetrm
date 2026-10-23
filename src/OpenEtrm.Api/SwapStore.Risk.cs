using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

public sealed partial class SwapStore
{
    internal sealed record ResolvedSwapHolding(JsonElement Row,FinancialSwapInstrument Instrument,Quantity Quantity,ISideDiscounting Discounting,
        PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot);

    // Resolves every held swap in scope except exactly the excluded ones (D213).
    internal async Task<ImmutableArray<ResolvedSwapHolding>> ReadRiskHoldings(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        ImmutableArray<Guid> bookIds,ImmutableArray<ExcludedPosition> excluded,CancellationToken ct,MarketSnapshot? snapshot=null,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplemental=default)
    {
        var books=await new FuturesStore(manifest).Books(connection,ct);
        FuturesStore.ValidateScope(books,bookIds,[]);
        var rows=ExcludedPosition.Keep((await Rows(connection,null,ct)).GroupBy(row=>row.Id("trade_id"))
            .Select(group=>group.MaxBy(row=>row.Number("revision"))!)
            .Where(row=>bookIds.IsEmpty||bookIds.Contains(row.Id("book_id"))).ToImmutableArray(),excluded);
        snapshot??=data.Snapshot(frame);
        if(snapshot.Id!=frame.Id||snapshot.Definitions!=data.DefinitionRevision||snapshot.ValuationAt!=frame.At)
            throw Error("swap_context","Swap risk requires the exact selected model and snapshot context.");
        if(supplemental.IsDefault)
        {
            var revisions=rows.Select(r=>r.Id("definition_revision_id")).Where(r=>r!=data.DefinitionRevision.RevisionId).ToHashSet();
            supplemental=revisions.Count==0?[]:await database.ReadSwapMarkets(connection,frame,ct,data,revisions);
        }
        var markets=new[]{(Data:data,Frame:frame,Snapshot:snapshot)}.Concat((supplemental.IsDefault?[]:supplemental)
            .Select(m=>(m.Data,m.Frame,Snapshot:m.Data.Snapshot(m.Frame)))).ToArray();
        if(markets.Select(m=>m.Data.DefinitionRevision).Distinct().Count()!=markets.Length||markets.Any(m=>
            m.Frame.Id!=frame.Id||m.Frame.At!=frame.At||m.Frame.ContextId!=frame.ContextId||m.Data.ReferenceRevision!=data.ReferenceRevision||
            m.Data.DefinitionRevision.CatalogId!=data.DefinitionRevision.CatalogId))
            throw Error("swap_context","Supplemental markets require unique exact revisions and the same retained context, instant and references.");
        var identities=new InstrumentIdentityMap(manifest.CatalogId);
        var providers=new Dictionary<Guid,ISideDiscounting>();
        var result=ImmutableArray.CreateBuilder<ResolvedSwapHolding>(rows.Length);
        foreach(var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var selected=markets.Where(m=>m.Data.DefinitionRevision.RevisionId==row.Id("definition_revision_id")).ToArray();
            if(selected.Length!=1)throw Error("swap_risk_model","Every held swap requires its exact explicitly retained market revision.");
            var market=selected[0];
            var terms=Terms(market.Data,row,market.Frame.Id);
            var resolved=Resolve(market.Data,terms,row.Id("instrument_id"),row.Id("fixed_side_id"),row.Id("floating_side_id"));
            var instrument=identities.Retain(resolved.Instrument);
            if(instrument.Id!=row.Id("instrument_id"))
                throw Error("instrument_identity","Equivalent retained terms cannot replace an exact published instrument identity.");
            ValidateProjections(instrument.Floating.GenerateSchedule().Payments.SelectMany(payment=>payment.Prices),row);
            if(row.OptionalId("first_floating_side_id") is not null)ValidateProjections(((SidePaymentSchedule)instrument.First.GenerateSchedule()).Payments.SelectMany(payment=>payment.Prices),row,"first_projection_contracts","first_projection_weights");
            if(!providers.TryGetValue(instrument.Id,out var provider))
            {
                var mapping=instrument.Discounting?await Discount(connection,market.Data,market.Frame,instrument.Currency,ct):null;
                provider=FinancialSwapValuation.CreateDiscounting(instrument,market.Snapshot,mapping);
                providers.Add(instrument.Id,provider);
            }
            result.Add(new(row,instrument,resolved.Quantity,provider,market.Data,market.Frame,market.Snapshot));
        }
        return result.MoveToImmutable();
    }
}
