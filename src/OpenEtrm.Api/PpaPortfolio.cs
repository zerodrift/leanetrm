using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record PpaPortfolioInput(ValuationPortfolio Portfolio,ImmutableArray<PortfolioPositionEvidence> Evidence,
    ValuationReceipt Receipt,long Rows,ImmutableArray<PpaBinding> Bindings,ImmutableDictionary<Guid,JsonElement> Heads,CurveBuild Market,ImmutableArray<Guid> ReadContexts);

internal sealed record PpaScope(ImmutableDictionary<Guid,JsonElement> Heads,ImmutableArray<JsonElement> Bindings)
{
    // The scope without the given trades: their heads and every binding of theirs.
    internal PpaScope Without(IReadOnlySet<Guid> trades)=>new(Heads.RemoveRange(trades),[..Bindings.Where(b=>!trades.Contains(b.Id("trade_id")))]);
}

// Adapt persisted coverage bindings to the shared unit valuation and risk engines.
internal static class PpaPortfolio
{
    internal static object Receipt(ValuationReceipt receipt)=>new{
        receipt.Definitions,receipt.Reference,receipt.Snapshot,receipt.ValuationAt,receipt.KnownAt,receipt.KnowledgeContext,
        pending=receipt.Pending==OpenEtrm.MarketData.Power.PendingActualsPolicy.EstimateUntilActual?"estimate_until_actual_v1":"require_actuals_v1",receipt.Coverage,
        quantities=receipt.QuantityRevisions.Select(q=>new{q.Series,q.Revision,q.KnownAt,q.Context}),receipt.Shapes,
        actuals=receipt.Actuals.Select(a=>new{a.Batch,a.Revision,a.KnownAt}),receipt.AssetVersions,receipt.Implementations};

    internal static async Task<PpaPortfolioInput?> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        ScheduleContext context,ImmutableArray<Guid> books,CloseBookRiskDefinition policy,CancellationToken ct)
        =>await Prepare(connection,data,frame,context,books,policy.PendingActuals,frame.At,ct);
    internal static async Task<PpaPortfolioInput?> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        ScheduleContext context,ImmutableArray<Guid> books,string? pending,DateTimeOffset known,CancellationToken ct)
        =>await Prepare(connection,data,frame,context,await Scope(connection,data,frame,context,books,ct),pending,known,ct);
    // The head revision and bindings of every PPA trade in the books, as the selected context sees them.
    internal static async Task<PpaScope> Scope(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        ScheduleContext context,ImmutableArray<Guid> books,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.ppa_read_scope($1,'books',$2::uuid[],$3,$4,false)::text",
            data.DefinitionRevision.CatalogId,books.ToArray(),context.ContextId!.Value,frame.At);
        var scope=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        return new(scope.GetProperty("heads").EnumerateArray().ToImmutableDictionary(r=>r.Id("trade_id"),r=>r.Clone()),
            [..scope.GetProperty("bindings").EnumerateArray().Select(r=>r.Clone())]);
    }
    // Whether a binding was booked under exactly the definition and reference versions the selected market data uses.
    internal static bool SameVersions(PreviewData data,JsonElement binding)=>binding.Id("definition_revision_id")==data.DefinitionRevision.RevisionId&&
        binding.Id("reference_catalog_id")==data.ReferenceRevision.CatalogId&&binding.Id("reference_revision_id")==data.ReferenceRevision.RevisionId;
    // Values every binding of the scope or none: one booked under other versions refuses the whole scope.
    internal static async Task<PpaPortfolioInput?> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        ScheduleContext context,PpaScope scope,string? pending,DateTimeOffset known,CancellationToken ct)
    {
        var heads=scope.Heads;var rows=scope.Bindings.ToArray();
        if(rows.Length==0)return null;
        var incompatible=rows.Where(r=>!SameVersions(data,r)).ToArray();
        if(incompatible.Length>0)throw CloseRiskInputsException.For(incompatible,"exact_bundle_whole_operation_v1");
        if(pending is not("estimate_until_actual_v1" or "require_actuals_v1"))
            throw new MarketDataException("ppa_policy","Select the PPA pending-actuals policy explicitly.");
        var envelopes=new Dictionary<Guid,JsonElement>();
        foreach(var id in rows.Select(r=>r.Id("instrument_id")).Distinct())
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.read_ppa_terms($1,$2)::text",data.DefinitionRevision.CatalogId,id);
            envelopes.Add(id,JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!));
        }
        var terms=envelopes.ToDictionary(p=>p.Key,p=>p.Value.GetProperty("terms").Deserialize<PpaTermsInput>(PpaStore.Json)!);
        PpaCompiler.PreflightWork(rows.Select(r=>(terms[r.Id("instrument_id")],r.Text("coverage_from"),r.Text("coverage_to"),r.Id("instrument_id").ToString())));
        var tou=await PpaReads.Tou(connection,data,frame,context.ContextId!.Value,terms.Values,ct);
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
        var prepared=await new PpaReads().Prepare(connection,data,frame,bindings,known,context.ContextId!.Value,PpaQuantitiesStore.Policy(pending),[],ct,closeRiskInputs:true);
        var trades=bindings.GroupBy(b=>b.TradeId!.Value).Select(g=>new Trade(g.Key,heads[g.Key].Id("book_id"),
            [..g.Select(b=>new TradeInstrument(b.BindingId,b.Instrument,new(b.Mw,b.Instrument.UnitQuantity.Specification),coverage:b.Coverage))])).ToImmutableArray();
        var portfolio=ValuationPortfolio.FromTrades(trades,new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
            {Interval=prepared.Inputs,InstrumentDiscounting=prepared.Discounting});
        var (tradeReferences,instrumentReferences)=await References(connection,data.DefinitionRevision.CatalogId,[..heads.Keys],[..instruments.Keys],ct);
        var byBinding=rows.ToDictionary(r=>r.Id("binding_id"));
        var evidence=portfolio.Positions.Select(p=>{var row=byBinding[p.BindingId];return new PortfolioPositionEvidence(p.TradeId,row.GetProperty("revision").GetInt32(),p.BindingId,p.ValuationId,p.ValuationId,
            p.BookId,PortfolioValuation.PpaMeasure,data.DefinitionRevision.RevisionId,row.GetProperty("publication_order").GetInt32(),p.Quantity){Coverage=p.Coverage,TradeReference=tradeReferences[p.TradeId],InstrumentReference=instrumentReferences[p.ValuationId],InstrumentType="ppa"};}).ToImmutableArray();
        return new(portfolio,evidence,prepared.Inputs.Receipt,work,bindings,heads,prepared.Market,prepared.ReadContexts);
    }
    // The trade and instrument reference numbers of PPA trades and instruments.
    internal static async Task<(Dictionary<Guid,string> Trades,Dictionary<Guid,string> Instruments)> References(NpgsqlConnection connection,
        Guid catalog,ImmutableArray<Guid> trades,ImmutableArray<Guid> instruments,CancellationToken ct)
    {
        var tradeRows=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(r)::text FROM(SELECT trade_id,reference_number AS trade_reference FROM trading.trade_references
            WHERE catalog_id=$1 AND trade_id=ANY($2::uuid[]))r
            """,[catalog,trades.ToArray()],ct);
        var instrumentRows=await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(r)::text FROM(SELECT instrument_id,reference_number AS instrument_reference FROM trading.instrument_references
            WHERE catalog_id=$1 AND instrument_id=ANY($2::uuid[]))r
            """,[catalog,instruments.ToArray()],ct);
        return(tradeRows.ToDictionary(r=>r.Id("trade_id"),TradingReference.Trade),instrumentRows.ToDictionary(r=>r.Id("instrument_id"),TradingReference.Instrument));
    }
}
