using System.Collections.Immutable;
using Npgsql;

namespace OpenEtrm.Api;

// Trade reads callable without an HTTP request or workspace session, on the same terms as
// PartyReads. Each binds its view's cutoff and the trading context at the view's frame, and
// returns only while the reader still has trade access. Results are the pages' reads without
// the context, As of and rights the pages add.
public sealed class TradeReads
{
    readonly PreviewDatabase database;
    readonly FuturesStore futures;
    readonly SwapStore swaps;
    readonly TradingBlotter blotter;

    public TradeReads(PreviewDatabase database)
    {
        this.database=database;futures=new(database.Manifest);swaps=new(database);blotter=new(database.Manifest,futures,swaps);
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
    // A trade's selected revision, or its latest under the view, with every revision the view knows.
    public Task<object> FuturesTrade(NpgsqlConnection connection,TradingView view,Guid tradeId,int? revision,CancellationToken ct)=>
        Read(connection,view,data=>futures.Editor(connection,data,view.CalculationFrame,tradeId,revision,ct),ct);
    public Task<object> SwapTrade(NpgsqlConnection connection,TradingView view,Guid tradeId,int? revision,CancellationToken ct)=>
        Read(connection,view,data=>swaps.Editor(connection,data,view.CalculationFrame,tradeId,revision,ct),ct);

    // The one trading read path: a read of the view's frame under its cutoff and trading context. A
    // read that can answer without the frame's market data goes first, and falls back with null.
    internal async Task<object> Read(NpgsqlConnection connection,TradingView view,Func<PreviewData,Task<object>> read,CancellationToken ct,Func<Task<object?>>? readWithoutMarket=null,
        ReferenceReadScope referenceScope=ReferenceReadScope.DefinitionClosure,
        ObservationReadScope observationScope=ObservationReadScope.FactHistory)
    {
        var catalog=database.Manifest.CatalogId;
        await view.Bind(connection,catalog,ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",catalog,view.Frame.At))await bind.ExecuteNonQueryAsync(ct);
        var value=(readWithoutMarket is null?null:await readWithoutMarket())??await read(await database.Read(connection,ct,observationIds:view.Frame.ObservationIds,referenceScope:referenceScope,observationScope:observationScope));
        await using var check=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadTrades')",catalog);
        return Equals(await check.ExecuteScalarAsync(ct),true)?value:throw new PreviewDeniedException();
    }
}
