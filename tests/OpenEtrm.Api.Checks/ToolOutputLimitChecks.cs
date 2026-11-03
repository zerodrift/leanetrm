using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

static class ToolOutputLimitChecks
{
    internal static void Run(string file)
    {
        var admitted=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file));
        var limits=ToolOutputLimits.From(admitted);
        if(AgentTools.Bounded(new JsonObject{["value"]="small"},1,limits) is null ||
            AgentTools.Bounded(new JsonObject{["value"]="small"},2,limits) is not null ||
            AgentTools.Bounded(new JsonObject{["value"]=new string('x',1025)},1,limits) is not null)
            throw new Exception("The actual admitted output budget must refuse oversized results whole.");
        Console.WriteLine("PASS actual admitted tool limits: complete small result, oversized row and byte refusals without truncation");
    }
}
