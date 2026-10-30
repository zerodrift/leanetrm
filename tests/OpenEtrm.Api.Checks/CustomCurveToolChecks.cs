using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData.CustomCurves;

// Save curve draft (D260) as the worker runs it: AgentTools.Run on a running turn of a drafting agent, with the turn's token and
// the live worker, records a successful save step in the save's own transaction, and the worker's ordinary path records every
// refused step. tools/check_agent_turns.py supplies the turn and the fixture rows for each phase, then checks the rows.
internal static class CustomCurveToolChecks
{
    sealed class Phase(PreviewDatabase database,JsonElement fixture)
    {
        readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
        public readonly Guid Turn=fixture.GetProperty("turn").GetProperty("id").GetGuid();
        public readonly byte[] Token=Convert.FromHexString(fixture.GetProperty("token").GetString()!);
        public readonly AgentTools Tools=new(database,null!,null!,null!,null!,Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        public readonly TurnTargets Targets=new();
        public TurnReads Reads=>Tools.Reads(fixture.GetProperty("turn"),fixture.GetProperty("started"));
        public TurnWrites Writes=>new(Turn,Token);
        public readonly List<JsonElement> Seen=[];
        readonly NpgsqlDataSource worker=PreviewDatabase.WorkerSources(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")!,null).Writes;
        int requests;

        public JsonObject Input(RecipeDefinition definition,Guid? curve)=>new()
        {
            ["code"]=definition.Code,["name"]=definition.Name,["engine"]=RecipeEngine.Version,["formula"]=RecipeFormula.Format(definition),["curveId"]=curve?.ToString()
        };

        // One request answered as the worker answers it: the tool's own record, or the worker's record of any other step.
        public async Task<(ToolStep Step,JsonElement? Recorded)> Ask(JsonObject input,TurnReads? reads=null)
        {
            var request=new ModelToolRequest($"toolu_curve_draft_{++requests}","save_custom_curve_draft",JsonSerializer.SerializeToElement(input));
            var step=await Tools.Run(reads??Reads,Targets,request,Writes,CancellationToken.None);
            var recorded=step.Recorded;
            if(recorded is null)
            {
                await using var connection=await worker.OpenConnectionAsync();
                try{recorded=await Writes.Record(connection,step,Reads.Pin,CancellationToken.None);}
                catch(PostgresException e)when(e.MessageText=="agent_runtime_unavailable"){return(step,null);}
            }
            if(recorded.Value.TryGetProperty("step",out _))Targets.Issue(step.Issued);
            Seen.Add(recorded.Value);
            if(step.Content is {} content)Seen.Add(JsonSerializer.SerializeToElement(content));
            return(step,recorded);
        }

        public JsonObject Result(ToolStep step)=>JsonNode.Parse(step.Content!)!["result"]!.AsObject();

        public void Hidden()
        {
            foreach(var seen in Seen)
            {
                var text=seen.GetRawText();
                if(text.Contains(Convert.ToHexString(Token),StringComparison.OrdinalIgnoreCase)||text.Contains(Convert.ToBase64String(Token),StringComparison.Ordinal))
                    throw new Exception("A step carried the turn's token.");
            }
        }

        public async Task<T> AsAgent<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> work)=>
            await database.WithAgent(Reads.Agent,TimeSpan.FromSeconds(RuntimeSet.MaxReadSeconds),["ReadMarketData"],_=>{},work,CancellationToken.None);
    }

    public static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var phase=new Phase(database,fixture);
        var definition=fixture.GetProperty("definition").Deserialize<RecipeDefinition>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        void Require(bool condition,string message,object? observed=null){if(!condition)throw new Exception(observed is null?message:$"{message}: {observed}");}
        void Refused((ToolStep Step,JsonElement? Recorded) asked,string code,bool claimed)
        {
            Require(asked.Step.End==ToolStepEnd.Refusal&&asked.Step.Code==code&&asked.Step.Recorded is null&&(asked.Step.Operation is not null)==claimed
                &&JsonNode.Parse(asked.Step.Content!)!["code"]!.GetValue<string>()==code,$"expected the refusal {code}",asked.Step.Content);
        }
        var output=new JsonObject();
        switch(fixture.GetProperty("phase").GetString())
        {
            case "main":
            {
                // A new curve: its draft and its successful step commit together, and its target opens the draft at that step.
                var created=await phase.Ask(phase.Input(definition,null));
                Require(created.Step is {End:ToolStepEnd.Result,Code:null,Operation:"custom_curves.save_draft_v1",Recorded:not null}
                    &&created.Recorded!.Value.GetProperty("step").GetInt32()==1,"a new curve's save records its step with its write",created.Step.Content);
                var saved=phase.Result(created.Step);
                var curve=Guid.Parse(saved["curve"]!["id"]!.GetValue<string>());
                Require(saved["status"]!.GetValue<string>()=="saved_draft"&&saved["curve"]!["generation"]!.GetValue<int>()==1&&saved["curve"]!["savedBy"]!.GetValue<string>()=="assistant"
                    &&curve==AgentTools.DraftKeys(phase.Turn,1).Curve
                    &&created.Step.Issued is [{Target:{Page:"custom_curves",Kind:"draft"} draft}]&&draft.Selection!["turnId"]!.GetValue<string>()==phase.Turn.ToString()
                    &&draft.Selection["step"]!.GetValue<int>()==1&&draft.Label=="Saved curve draft: "+definition.Name,
                    "a new draft is the assistant's generation 1 under its step's identity, with a draft target at its step",created.Step.Content);
                Console.WriteLine("PASS Save curve draft saves a new curve's draft as the assistant's, recording its successful step in the same transaction, with a draft target at that step (D260)");

                // A second save in the turn is a new step and a new generation; an identical one writes nothing.
                var renamed=definition with{Name=definition.Name+" revised"};
                var revised=await phase.Ask(phase.Input(renamed,curve));
                var second=phase.Result(revised.Step);
                Require(revised.Step.End==ToolStepEnd.Result&&revised.Recorded!.Value.GetProperty("step").GetInt32()==2&&second["status"]!.GetValue<string>()=="saved_draft"
                    &&second["curve"]!["generation"]!.GetValue<int>()==2&&second["curve"]!["id"]!.GetValue<string>()==curve.ToString(),
                    "a second save in the turn is a new generation",revised.Step.Content);
                var same=await phase.Ask(phase.Input(renamed,curve));
                var unchanged=phase.Result(same.Step);
                Require(same.Step.End==ToolStepEnd.Result&&same.Recorded!.Value.GetProperty("step").GetInt32()==3&&unchanged["status"]!.GetValue<string>()=="unchanged_draft"
                    &&unchanged["curve"]!["generation"]!.GetValue<int>()==2&&!unchanged.ContainsKey("draft")
                    &&same.Step.Issued is [{Target:{Page:"custom_curves",Kind:"page",Selection:null}}],
                    "an identical save writes nothing and names Curve builder, as no draft generation is its own",same.Step.Content);
                Console.WriteLine("PASS a second save in one turn is a new step and a new generation; an identical save writes nothing and its target names Curve builder");

                // A retried step replays its first outcome: the same request and new curve identity, nothing written (D238).
                var reads=phase.Reads;
                var first=(SaveCurveArguments)AgentTools.Parse("save_custom_curve_draft",JsonSerializer.SerializeToElement(phase.Input(definition,null))).Arguments;
                var compiled=AgentTools.CompileCurve(first.Curve);
                var pinned=await phase.Tools.Previewed(reads,compiled,CancellationToken.None);
                var replayed=await phase.AsAgent((c,ct)=>phase.Tools.SaveDraft(c,reads,first,compiled,1,phase.Writes,pinned,ct));
                Require(replayed.Written&&(replayed.Version.Id,replayed.Version.Generation,replayed.Version.Definition.Name)==(curve,1,definition.Name),
                    "a retried step returns its first outcome",JsonSerializer.Serialize(replayed.Version));
                Console.WriteLine("PASS a retried save step returns its first recorded outcome, its new curve's included, and writes nothing (D238)");

                // Refused before any claim: the curve open with unsaved edits, and a historical pin.
                var open=reads with{PageReport=JsonSerializer.SerializeToElement(new{page="custom_curves",selection=new{curve=curve.ToString(),unsaved=true}})};
                Refused(await phase.Ask(phase.Input(renamed with{Name="Synthetic over unsaved edits"},curve),open),"custom_curve_open_unsaved",false);
                var historical=reads with{Pin=TurnPin.From(JsonSerializer.SerializeToElement(new{mode="retained",requestedContextId=reads.Pin.ContextId,frameId=(Guid?)null,
                    contextId=reads.Pin.ContextId,cutoff=reads.Pin.Cutoff,recordedAt=reads.Pin.Cutoff}),JsonSerializer.SerializeToElement<object?>(null),database.Manifest.CatalogId,database.Manifest)};
                Refused(await phase.Ask(phase.Input(definition with{Code=definition.Code+"_h"},null),historical),"historical_read_only",false);
                Console.WriteLine("PASS a save of the curve the page reports open with unsaved edits, or under a historical pin, is refused before any claim");

                // Refused by the claim or the save: a published curve, and a code a draft the agent cannot see holds, which names nothing of it.
                Refused(await phase.Ask(phase.Input(definition with{Code=fixture.GetProperty("publishedCode").GetString()!},fixture.GetProperty("published").GetGuid())),
                    "custom_curve_agent_scope",true);
                var clash=await phase.Ask(phase.Input(definition with{Code=fixture.GetProperty("hiddenCode").GetString()!},null));
                Refused(clash,"custom_curve_conflict",true);
                Require(!clash.Step.Content!.Contains(fixture.GetProperty("hidden").GetString()!,StringComparison.Ordinal)&&!clash.Step.Content.Contains("person",StringComparison.Ordinal),
                    "a hidden code clash names nothing of the other curve",clash.Step.Content);
                phase.Hidden();
                Console.WriteLine("PASS a published curve is out of the assistant's scope, a code held by a draft it cannot see is a conflict naming nothing of it, and no step carries the turn's token");
                output["curve"]=curve.ToString();
                output["hiddenRefused"]=AgentTools.DraftKeys(phase.Turn,phase.Targets.Steps).Curve.ToString();
                break;
            }
            // The person lost market-data authoring, the turn's cancel was requested or its worker is stale: refused, nothing written.
            case var refusal:
            {
                var (code,claimed)=refusal switch{"revoked"=>("delegation_denied",true),"cancelled"=>("delegation_denied",true),
                    "stale"=>("agent_runtime_unavailable",true),_=>throw new Exception("Unknown phase.")};
                foreach(var attempt in new[]{1,2})
                {
                    var asked=await phase.Ask(phase.Input(definition,null));
                    Refused(asked,code,claimed);
                    Require(!(asked.Recorded?.TryGetProperty("step",out _)??false)||refusal=="revoked","a refusal of a cancelled or stale turn records no step",asked.Recorded);
                }
                phase.Hidden();
                Console.WriteLine($"PASS a save step is refused {code} and writes nothing, and so is its retry, when {refusal switch{"revoked"=>"the person lost market-data authoring",
                    "cancelled"=>"the turn's cancel was requested",_=>"its worker's generation is stale"}}");
                output["curve"]=AgentTools.DraftKeys(phase.Turn,1).Curve.ToString();
                break;
            }
        }
        Console.WriteLine(output.ToJsonString());
    }
}
