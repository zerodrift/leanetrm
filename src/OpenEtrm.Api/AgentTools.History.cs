using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

public sealed partial class AgentTools
{
    async Task<ToolStep> RunHistory(TurnReads turn,string tool,JsonElement input,HistoryArguments arguments,CancellationToken ct)
    {
        DateTimeOffset? readAt=null;
        ImmutableArray<ReadAccess> access=[];
        try
        {
            var result=await database.WithAgent(turn.Agent,turn.ReadLimit,[],at=>readAt=at,async(connection,read)=>
                await ModelProviders.Read(connection,"SELECT assistant.search_history($1,$2,$3::uuid)::text",
                    [turn.TurnId,arguments.Query,(object?)arguments.Before??DBNull.Value],read),ct);
            access=[..result.GetProperty("access").EnumerateArray().Select(a=>new ReadAccess(a.GetProperty("access").GetString()!,
                a.GetProperty("catalogId").ValueKind==JsonValueKind.Null?null:a.GetProperty("catalogId").GetGuid(),a.GetProperty("permission").GetString()!))];
            var value=JsonNode.Parse(result.GetRawText())!.AsObject();value.Remove("access");
            var content=Bounded(value,result.GetProperty("matches").GetArrayLength(),turn.OutputLimits);
            return new(tool,"assistant.history_search_v1",input,content is null?"result_too_large":null,readAt,access,
                content??Refusal("result_too_large"),content is null?ToolStepEnd.Refusal:ToolStepEnd.Result);
        }
        catch(Exception e)
        {
            var (code,end)=Classify(e);
            if(end==ToolStepEnd.Failure)logger.LogError("A history search failed: {ErrorType}",e.GetType().Name);
            return new(tool,readAt is null?null:"assistant.history_search_v1",input,code,readAt,access,
                end==ToolStepEnd.Refusal?Refusal(code!):null,end);
        }
    }
}
