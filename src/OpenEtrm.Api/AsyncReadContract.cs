using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AsyncReadRequest(Guid ChannelId,Guid RequestId,string Operation,JsonElement Body);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AsyncReadCancel(Guid ChannelId,Guid RequestId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AsyncReadOpenRequest([property:JsonRequired]Guid ClientId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AsyncReadEmpty;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AsyncReadJob(Guid Id);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AsyncReadArchive(string Method,int Limit,string? Before,Guid? Id);
internal sealed record ReadInput(string Operation,object Body,Guid? SnapshotId,ImmutableArray<string> Permissions,FuturesReadRequest? Context=null);
internal sealed record ReadPlan(ReadInput Input,Func<CancellationToken,Task<ReadOutcome>> Execute);
internal sealed record ReadOutcome(object Value,Func<NpgsqlConnection,CancellationToken,Task> Recheck)
{
    internal IReadOnlyDictionary<Guid,string>? ReadReferences {get;init;}
}
internal sealed class AsyncReadException(int status,string code,string message):Exception(message)
{
    internal int Status=>status;
    internal string Code=>code;
}

// Only named existing reads cross this transport. Body decoding stays typed;
// URLs, commands, retained-job submission and cancellation are not operations.
internal static class AsyncReadContract
{
    internal const string CurveDependencies="curve_dependencies_v1";
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {AllowDuplicateProperties=false,Converters={new ExactDecimalJsonConverter()}};
    internal static ReadInput Decode(AsyncReadRequest request)
    {
        if(request.Body.ValueKind!=JsonValueKind.Object)throw Invalid();
        return Input(request.Operation,request.Body.Deserialize(BodyType(request.Operation),Json)??throw Invalid());
    }
    static Type BodyType(string operation)=>operation switch
    {
        "curve.evaluate.v1"=>typeof(BuildRequest),
        "curve.history.v1"=>typeof(HistoryRequest),
        "curve.shapes.v1"=>typeof(CurveShapesRequest),
        "trading.directory.v1" or "trading.freshness.v1"=>typeof(FuturesReadRequest),
        "trading.positions.v1"=>typeof(TradingPositionsRequest),
        "trading.workspace.v1"=>typeof(TradingWorkspaceRequest),
        "trading.blotter.v1"=>typeof(FuturesBlotterRequest),
        "futures.editor.v1"=>typeof(FuturesEditorRequest),
        "swaps.editor.v1"=>typeof(SwapEditorRequest),
        "options.strategy.analyze.v1"=>typeof(OptionStrategyAnalyzeRequest),
        "portfolio.risk.scope.v1"=>typeof(RiskScopeRequest),
        "portfolio.risk.catalog.v1"=>typeof(PortfolioRiskCatalogRequest),
        "portfolio.risk.calculate.v1"=>typeof(PortfolioRiskRequest),
        "portfolio.risk.history.v1"=>typeof(PortfolioHistoryRequest),
        "portfolio.risk.jobs.list.v1"=>typeof(AsyncReadEmpty),
        "portfolio.risk.jobs.read.v1"=>typeof(AsyncReadJob),
        "portfolio.risk.archive.v1"=>typeof(AsyncReadArchive),
        _=>throw new AsyncReadException(422,"async_operation","This operation is not an admitted asynchronous read.")
    };
    // The input of an already-typed body: an ordinary route and the channel classify one read identically. The channel
    // requires the exact selection here; an ordinary route passes exactSelection false and refuses a missing one itself.
    internal static ReadInput Input(string operation,object body,bool exactSelection=true)
    {
        if(body.GetType()!=BodyType(operation))throw Invalid();
        ReadInput Market(Guid snapshot)=>snapshot!=Guid.Empty||!exactSelection?new(operation,body,snapshot,["ReadMarketData"]):throw Invalid();
        ReadInput Trading(FuturesReadRequest? context,bool risk=false)=>context is {SnapshotId:var snapshot}&&(snapshot!=Guid.Empty||!exactSelection)?
            new(operation,body,snapshot,risk?["ReadTrades","ReadMarketData","RunScenario"]:["ReadTrades","ReadMarketData"],context):throw Invalid();
        ReadInput Status()=>new(operation,body,null,["RunScenario"]);
        switch(body)
        {
            case BuildRequest evaluate:
                if(evaluate.SourceScope is not null&&(evaluate.SourceScope!=AsyncReadProjections.CurveLineage||!evaluate.IncludeSources))
                    throw Invalid();
                if(evaluate.DefinitionScope is not null&&(evaluate.DefinitionScope!=CurveDependencies||
                    evaluate.SourceScope!=AsyncReadProjections.CurveLineage||evaluate.IncludeWorkspace))
                    throw Invalid();
                return Market(evaluate.SnapshotId);
            case HistoryRequest history:return Market(history.SnapshotId);
            case CurveShapesRequest shapes:return Market(shapes.SnapshotId);
            case FuturesReadRequest directory:return Trading(directory);
            case TradingPositionsRequest positions:return Trading(positions.Context);
            case TradingWorkspaceRequest workspace:return Trading(workspace.Context);
            case FuturesBlotterRequest blotter:return Trading(blotter.Context);
            case FuturesEditorRequest future:return Trading(future.Context);
            case SwapEditorRequest swap:return Trading(swap.Context);
            case OptionStrategyAnalyzeRequest strategy:return Trading(strategy.Context);
            case RiskScopeRequest scope:return Trading(scope.Context,true);
            case PortfolioRiskCatalogRequest catalog:return Trading(catalog.Context,true);
            case PortfolioRiskRequest risk:return Trading(risk.Context,true);
            case PortfolioHistoryRequest riskHistory:return Trading(riskHistory.Scope?.Context,true);
            case AsyncReadEmpty:return Status();
            case AsyncReadJob job:return job.Id!=Guid.Empty||!exactSelection?Status():throw Invalid();
            case AsyncReadArchive archive:
                if(!exactSelection)return Status();
                if(archive.Method is not("statistical" or "parametric" or "historical" or "monte_carlo")||archive.Limit is <1 or >100||
                    (archive.Before is null)!=(archive.Id is null)||archive.Id==Guid.Empty)throw Invalid();
                if(archive.Before is not null)_=SchedulePreview.ParseAt(archive.Before);
                return Status();
            default:throw Invalid();
        }
    }
    internal static AsyncReadException Invalid()=>new(422,"async_request","Supply the complete typed read request and its exact selected context.");
}
