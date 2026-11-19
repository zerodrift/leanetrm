using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

// HTTP and SSE share these projections; transport never changes financial terms.
internal static class AsyncReadProjections
{
    internal const string CurveLineage="curve_lineage_v1";
    internal static object Curve(PriceRead read,PinnedContext pin,BuildRequest body,object? workspace=null)
    {
        if(pin.Context is null&&!body.IncludeSources&&!body.IncludeWorkspace)return read.Result;
        var json=read.UsedObservationIds.HasValue&&read.Result is JsonObject projection?projection:
            JsonSerializer.SerializeToNode(read.Result,AsyncReadContract.Json)!.AsObject();
        if(pin.Context is {} view)json["context"]=JsonSerializer.SerializeToNode(view,AsyncReadContract.Json);
        if(body.IncludeSources)
        {
            json["observations"]=JsonSerializer.SerializeToNode(read.Observations);
            var used=read.UsedObservationIds;
            json["observationScope"]=used.HasValue?CurveLineage:"selected_frame";
            var selection=used is {} ids?read.InputSelection with{ObservationIds=ids}:read.InputSelection;
            json["inputSelection"]=JsonSerializer.SerializeToNode(selection,AsyncReadContract.Json);
        }
        if(body.IncludeWorkspace)json["workspace"]=JsonSerializer.SerializeToNode(workspace??throw new InvalidOperationException("The requested workspace projection is missing."),AsyncReadContract.Json);
        return json;
    }
    internal static object History(ImmutableArray<HistorySample> points,PinnedContext pin,HistoryRequest body,string policy)
    {
        var chart=HistoryChart.Of(points);
        var read=new Dictionary<string,object?>{["mode"]="exact_visible_close_history_with_selected_instant_v1",["revisionPolicy"]=policy,["knowledgeCutoff"]=pin.Context?.KnownAt,
            ["knowledgeBasis"]=pin.Context is null?"current_knowledge":"selected_context",["points"]=points,
            ["chart"]=chart.Conflict is {} conflict?new{conflict=new{at=conflict.At,message=conflict.Message}}:new{points=chart.Points}};
        if(body.Overlays is {} requests)
        {
            if(chart.Conflict is null)read["overlays"]=OpenEtrm.Shared.Analytics.Overlays.Compute(chart.OverlayInput(),requests);
            else OpenEtrm.Shared.Analytics.Overlays.Compute([],requests);
        }
        return read;
    }
}
