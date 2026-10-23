using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record PpaPortfolioInput(ValuationPortfolio Portfolio,ImmutableArray<PortfolioPositionEvidence> Evidence,
    ValuationReceipt Receipt,long Rows);

// Adapt persisted coverage bindings to the shared unit valuation and risk engines.
internal static class PpaPortfolio
{
    internal static async Task<PpaPortfolioInput?> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        ScheduleContext context,ImmutableArray<Guid> books,CloseBookRiskDefinition policy,CancellationToken ct)
    {
        await using var scopeCommand=ScheduleStore.Command(connection,"SELECT security.ppa_read_scope($1,'books',$2::uuid[],$3,$4,false)::text",
            data.DefinitionRevision.CatalogId,books.ToArray(),context.ContextId!.Value,frame.At);
        var scope=JsonSerializer.Deserialize<JsonElement>((string)(await scopeCommand.ExecuteScalarAsync(ct))!);
        var heads=scope.GetProperty("heads").EnumerateArray().ToDictionary(r=>r.Id("trade_id"));
        var rows=scope.GetProperty("bindings").EnumerateArray().Select(r=>r.Clone()).ToArray();
        if(rows.Length==0)return null;
        var incompatible=rows.Where(r=>r.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId ||
            r.Id("reference_catalog_id")!=data.ReferenceRevision.CatalogId||r.Id("reference_revision_id")!=data.ReferenceRevision.RevisionId).ToArray();
        if(incompatible.Length>0)throw CloseRiskInputsException.For(incompatible,"exact_bundle_whole_operation_v1");
        var envelopes=new Dictionary<Guid,JsonElement>();
        foreach(var id in rows.Select(r=>r.Id("instrument_id")).Distinct())
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.read_ppa_terms($1,$2)::text",data.DefinitionRevision.CatalogId,id);
            envelopes.Add(id,JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!));
        }
        var terms=envelopes.ToDictionary(p=>p.Key,p=>p.Value.GetProperty("terms").Deserialize<PpaTermsInput>(PpaStore.Json)!);
        PpaCompiler.PreflightWork(rows.Select(r=>(terms[r.Id("instrument_id")],r.Text("coverage_from"),r.Text("coverage_to"),r.Id("instrument_id").ToString())));
        var tou=await PpaReads.Tou(connection,data,frame,context.ContextId.Value,terms.Values,ct);
        var instruments=new Dictionary<Guid,PpaInstrument>();
        foreach(var pair in envelopes)
        {
            try
            {
                var instrument=PpaCompiler.Compile(data,terms[pair.Key],tou,pair.Key,
                    pair.Value.GetProperty("sideIds").EnumerateArray().ToDictionary(s=>new SideSlot(PpaCompiler.Role(s.Text("role")),s.GetProperty("ordinal").GetInt32()),s=>s.Id("sideId")));
                await PpaCompiler.ValidateDefinitions(connection,data,instrument,ct);instruments.Add(pair.Key,instrument);
            }
            catch(MarketDataException error){throw CloseRiskInputsException.For(rows.Where(r=>r.Id("instrument_id")==pair.Key),error.Code);}
        }
        var bindings=rows.Select(r=>new PpaBinding(r.Id("trade_id"),r.GetProperty("revision").GetInt32(),r.Id("binding_id"),instruments[r.Id("instrument_id")],
            new(r.Instant("coverage_from"),r.Instant("coverage_to")),r.Number("quantity"))).ToImmutableArray();
        var work=PpaCompiler.Work(bindings.Select(b=>(b.Instrument,b.Coverage)),[],i=>PpaQuantitiesStore.Clock(data,i.Delivery.Clock.Code));
        var prepared=await new PpaReads().Prepare(connection,data,frame,bindings,frame.At,context.ContextId.Value,PpaQuantitiesStore.Policy(policy.PendingActuals),[],ct,closeRiskInputs:true);
        var positions=bindings.Select(b=>new RiskPosition(b.TradeId!.Value,b.BindingId,heads[b.TradeId.Value].Id("book_id"),b.Instrument.Id,
            new(b.Mw,b.Instrument.UnitQuantity.Specification),new(new Money(default,b.Instrument.Currency),b.Instrument.UnitQuantity.Specification),new(default,b.Instrument.Currency)){Coverage=b.Coverage}).ToImmutableArray();
        var portfolio=new ValuationPortfolio([..instruments.Values],positions,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
            {Interval=prepared.Inputs,InstrumentDiscounting=prepared.Discounting});
        var byBinding=rows.ToDictionary(r=>r.Id("binding_id"));
        var evidence=positions.Select(p=>{var row=byBinding[p.BindingId];return new PortfolioPositionEvidence(p.TradeId,row.GetProperty("revision").GetInt32(),p.BindingId,p.ValuationId,p.ValuationId,
            p.BookId,PortfolioValuation.PpaMeasure,data.DefinitionRevision.RevisionId,row.GetProperty("publication_order").GetInt32(),p.Quantity){Coverage=p.Coverage};}).ToImmutableArray();
        return new(portfolio,evidence,prepared.Inputs.Receipt,work);
    }
}
