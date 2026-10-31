using System.Collections.Immutable;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Trade reads callable without an HTTP request or workspace session, on the same terms as
// PartyReads. Each binds its view's cutoff and the trading context at the view's frame, and
// returns only while the reader still has trade access. Results are the pages' reads without
// the context, As of and rights the pages add.
public sealed partial class TradeReads
{
    readonly PreviewDatabase database;
    readonly FuturesStore futures;
    readonly SwapStore swaps;
    readonly TradingBlotter blotter;

    public TradeReads(PreviewDatabase database)
    {
        this.database=database;futures=new(database.Manifest);swaps=new(database);blotter=new(database,futures,swaps);
    }

    // The Blotter with the booking catalog, or with only the catalog's directory of books and parties, and the groupings
    // the caller asks for of the Blotter's own valuation.
    public Task<object> Workspace(NpgsqlConnection connection,TradingView view,FuturesBlotterView report,ImmutableArray<FuturesBlotterView> summaries,bool directory,CancellationToken ct)
    {
        // Without any visible trade revision, the directory's empty Blotter needs no valuation (D160).
        async Task<object?> EmptyDirectory()
        {
            if(await blotter.ReadEmptyDirectory(connection,view.Frame,report,summaries,ct) is not {} empty)return null;
            var catalog=await futures.Directory(connection,ct);
            await MarketBundle.RequireReferenceVisibility(connection,database.Manifest,ct);
            return new{catalog,blotter=empty.Blotter,summaries=empty.Summaries};
        }
        return Read(connection,view,async data=>
        {
            var catalog=directory?await futures.Directory(connection,ct):await futures.Catalog(connection,data,view.CalculationFrame,ct);
            var read=await blotter.Read(connection,data,view.CalculationFrame,report,summaries,ct);
            return new{catalog,blotter=read.Blotter,summaries=read.Summaries};
        },ct,directory?EmptyDirectory:null,
            referenceScope:directory?ReferenceReadScope.DefinitionClosure:ReferenceReadScope.TradeValuation,
            observationScope:directory?ObservationReadScope.FactHistory:ObservationReadScope.ExactPins);
    }
    public Task<object> Blotter(NpgsqlConnection connection,TradingView view,FuturesBlotterView report,CancellationToken ct)=>
        Read(connection,view,async data=>(await blotter.Read(connection,data,view.CalculationFrame,report,[],ct)).Blotter,ct);
    // Booking and scope choices do not depend on observations or held-position valuation.
    public Task<object> Directory(NpgsqlConnection connection,TradingView view,CancellationToken ct)=>
        ReadBound(connection,view,async()=>
        {
            var directory=await futures.Directory(connection,ct);
            await MarketBundle.RequireReferenceVisibility(connection,database.Manifest,ct);
            return directory;
        },ct);
    public Task<object> Positions(NpgsqlConnection connection,TradingView view,ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,
        int offset,int limit,CancellationToken ct)=>ReadBound(connection,view,
            async()=>await new TradePositionFacts(database.Manifest).Read(connection,view,bookIds,portfolioIds,offset,limit,ct),ct);
    // A trade's selected revision, or its latest under the view, with every revision the view knows.
    public Task<object> FuturesTrade(NpgsqlConnection connection,TradingView view,Guid tradeId,int? revision,CancellationToken ct)=>
        Read(connection,view,data=>futures.Editor(connection,data,view.CalculationFrame,tradeId,revision,ct),ct);
    public Task<object> SwapTrade(NpgsqlConnection connection,TradingView view,Guid tradeId,int? revision,CancellationToken ct)=>
        Read(connection,view,data=>swaps.Editor(connection,data,view.CalculationFrame,tradeId,revision,ct),ct);

    public Task<object> PpaTrade(NpgsqlConnection connection,TradingView view,Guid tradeId,int? revision,CancellationToken ct)=>
        Read(connection,view,async data=>
        {
            var manifest=database.Manifest;
            var editor=JsonSerializer.SerializeToNode(await Ppa().Editor(connection,view.CalculationFrame,new(view.Frame.Id,tradeId,PpaCutoff(view)),ct,Bound(view)),PpaStore.Json)!.AsObject();
            var head=editor["head"]!.AsObject();var selected=head["revision"]!.GetValue<int>();
            if(revision is {} requested&&requested!=selected)
                throw new MarketDataException("ppa_revision","Select the historical context of the requested PPA revision; this context retains its current head and complete visible history.");
            var references=await PreviewDatabase.Rows(connection,"SELECT json_build_object('trade_reference',reference_number)::text FROM trading.trade_references WHERE catalog_id=$1 AND trade_id=$2",[manifest.CatalogId,tradeId],ct);
            if(references.Length!=1)throw new MarketDataException("ppa_trade","The retained PPA trade reference is unavailable.");
            return new{state=new{tradeId,revision=selected,tradeReference=TradingReference.Trade(references[0]),head,
                bindings=editor["bindings"]!.DeepClone(),instruments=editor["instruments"]!.DeepClone()},history=editor["history"]!.DeepClone()};
        },ct);

    // The PPA trades booked in one book, as PPA bookings lists them, with their bindings: the whole book in one directory
    // read, rechecking the book's scope as the page's read does.
    public Task<object> PpaBook(NpgsqlConnection connection,TradingView view,Guid book,CancellationToken ct)=>
        Read(connection,view,async data=>
        {
            var store=Ppa();
            var directory=await store.Directory(connection,view.CalculationFrame,new(view.Frame.Id,[book],PpaCutoff(view),0,PpaBookLimit),ct,Bound(view));
            await store.RecheckScope(connection,view.CalculationFrame,PpaCutoff(view),"books",[book],store.ReferenceFor(view.CalculationFrame),ct,Bound(view));
            return directory;
        },ct);
    // The most trades one directory read lists.
    internal const int PpaBookLimit=64;

    PpaStore Ppa()
    {
        var manifest=database.Manifest;
        return new(database,new TouProductStore(manifest.CatalogId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId));
    }
    // The context a PPA read binds: the view's, which Read bound exactly, so the read captures none (D184).
    static Guid Bound(TradingView view)=>view.Context.ContextId??throw new InvalidOperationException("A trading view binds no context.");
    // A PPA read's knowledge cutoff: the view's, in the form the PPA reads take.
    static string PpaCutoff(TradingView view)=>
        SchedulePreview.ParseAt(view.Context.KnownAt).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",System.Globalization.CultureInfo.InvariantCulture);

    // The one trading read path: a read of the view's frame under its cutoff and trading context. A
    // read that can answer without the frame's market data goes first, and falls back with null.
    internal Task<object> Read(NpgsqlConnection connection,TradingView view,Func<PreviewData,Task<object>> read,CancellationToken ct,Func<Task<object?>>? readWithoutMarket=null,
        ReferenceReadScope referenceScope=ReferenceReadScope.DefinitionClosure,
        ObservationReadScope observationScope=ObservationReadScope.FactHistory)
        =>ReadBound(connection,view,async()=>
            (readWithoutMarket is null?null:await readWithoutMarket())??await read(await database.Read(connection,ct,
                observationIds:view.Frame.ObservationIds,referenceScope:referenceScope,observationScope:observationScope)),ct);
    internal async Task<object> ReadBound(NpgsqlConnection connection,TradingView view,Func<Task<object>> read,CancellationToken ct)
    {
        var catalog=database.Manifest.CatalogId;
        await view.Bind(connection,catalog,ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",catalog,view.Frame.At))await bind.ExecuteNonQueryAsync(ct);
        var value=await read();
        await using var check=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadTrades')",catalog);
        return Equals(await check.ExecuteScalarAsync(ct),true)?value:throw new PreviewDeniedException();
    }
}
