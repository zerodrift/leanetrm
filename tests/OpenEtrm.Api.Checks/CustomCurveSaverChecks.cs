using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

// D260 through the managed store under the agent's own request context: each generation records who saved it, the
// assistant's identical save writes nothing, a refused claim writes nothing, and no read carries the turn's token.
// tools/check_agent_turns.py supplies a running drafting turn, its token and successful save steps, then checks the rows.
internal static class CustomCurveSaverChecks
{
    public static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var agent=new AgentIdentity(fixture.GetProperty("principal").GetString()!,fixture.GetProperty("delegation").GetGuid());
        var turn=fixture.GetProperty("turn").GetGuid();var token=Convert.FromHexString(fixture.GetProperty("token").GetString()!);
        var steps=fixture.GetProperty("steps").Deserialize<int[]>()!;
        var definition=fixture.GetProperty("definition").Deserialize<RecipeDefinition>(json)!;
        var store=new CustomCurvesStore(database);
        if(fixture.TryGetProperty("moved",out var moved)){await Moved(database,store,agent,turn,token,steps[6],definition with{Code=definition.Code+"_m"},moved.GetGuid());return;}
        var requests=new List<Guid>();var receipted=new List<Guid>();var personal=new List<Guid>();var refusedCodes=new List<string>();
        CustomCurveSaveRequest Request(RecipeDefinition saved,CustomCurveVersion? from=null,Guid? request=null)
        {
            var id=request??Guid.NewGuid();if(!requests.Contains(id))requests.Add(id);
            return new(id,saved,ImmutableDictionary<string,CurveNodePosition>.Empty,Guid.NewGuid(),from?.Id,from?.Version,from?.Generation,from?.State);
        }
        async Task<CustomCurveVersion> Save(NpgsqlConnection connection,CustomCurveSaveRequest body,CurveDraftSaver saver,CancellationToken ct)=>
            await store.Save(connection,body,await database.Read(connection,ct,observationIds:[],includeCalendarDays:false),saver,ct);
        Task<T> AsAgent<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> work)=>
            database.WithAgent(agent,TimeSpan.FromSeconds(RuntimeSet.MaxReadSeconds),["ReadMarketData"],_=>{},work,CancellationToken.None);
        Task<T> AsEditor<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> work)=>
            database.WithIdentity("editor","WriteMarketDefinitions",work,CancellationToken.None);
        CurveDraftSaver Assistant(int step)=>CurveDraftSaver.Assistant(turn,step,token);
        void Shows(CustomCurveVersion version,string saver)
        {
            var text=JsonSerializer.Serialize(version,json);
            if(!text.Contains("\"saver\":"+saver,StringComparison.Ordinal))throw new Exception($"Expected saver {saver}: {text}");
            if(text.Contains(Convert.ToHexString(token),StringComparison.OrdinalIgnoreCase)||text.Contains(Convert.ToBase64String(token),StringComparison.Ordinal))
                throw new Exception("A saved curve carried the turn's token.");
        }
        async Task Refused(string code,Func<Task> work)
        {
            try{await work();}
            catch(MarketDataException e)when(e.Code==code){return;}
            throw new Exception("Expected the structured refusal "+code);
        }

        // The assistant's new curve, then its revision: each a new generation recorded as its own step's.
        var created=await AsAgent((c,ct)=>Save(c,Request(definition),Assistant(steps[0]),ct));
        receipted.Add(requests[^1]);
        if((created.Version,created.Generation,created.State,created.Saver)!=(1,1,"draft",new CurveSaver("assistant")))
            throw new Exception("The assistant's new draft did not record its saver.");
        // Reads say who saved a generation and no more (D260): the turn and step stay stored.
        Shows(created,"{\"savedBy\":\"assistant\"}");
        var renamed=definition with{Name="Synthetic assistant draft revised"};
        var revised=await AsAgent((c,ct)=>Save(c,Request(renamed,created),Assistant(steps[1]),ct));
        receipted.Add(requests[^1]);
        if((revised.Generation,revised.Saver)!=(2,new CurveSaver("assistant")))throw new Exception("The assistant's revision did not record its saver.");
        Console.WriteLine("PASS the store records the assistant as the saver of its new draft and of its revision, and reads show only who saved it (D260)");

        // The same graph, stated in another order, is the current generation: nothing is written, and a retry is the same.
        var restated=renamed with{Nodes=[..renamed.Nodes.Reverse()]};var identical=Request(restated,revised);
        foreach(var _ in new[]{1,2})
        {
            var same=await AsAgent((c,ct)=>Save(c,identical,Assistant(steps[2]),ct));
            if(JsonSerializer.Serialize(same,json)!=JsonSerializer.Serialize(revised,json))throw new Exception("An identical assistant save changed the draft.");
        }
        Console.WriteLine("PASS the assistant's save of a definition identical to the current generation returns it unchanged and writes nothing, and so does its retry (D260)");

        // A refused claim writes nothing: a second claim in one context, another token, or a person's context.
        string Fresh(){var code=$"{definition.Code}_r{refusedCodes.Count}";refusedCodes.Add(code);return code;}
        await Refused("custom_curve_agent_claimed",()=>AsAgent(async(c,ct)=>
        {
            await Save(c,Request(definition with{Code=Fresh()}),Assistant(steps[3]),ct);
            return await Save(c,Request(definition with{Code=Fresh()}),Assistant(steps[3]),ct);
        }));
        await Refused("agent_runtime_unavailable",()=>AsAgent((c,ct)=>Save(c,Request(definition with{Code=Fresh()}),CurveDraftSaver.Assistant(turn,steps[3],new byte[32]),ct)));
        await Refused("delegation_denied",()=>AsEditor((c,ct)=>Save(c,Request(definition with{Code=Fresh()}),Assistant(steps[3]),ct)));
        // A new curve that names a version is malformed for the assistant as for a person, never a scope question.
        await Refused("custom_curve_conflict",()=>AsAgent((c,ct)=>Save(c,Request(definition with{Code=Fresh()}) with{Version=5},Assistant(steps[3]),ct)));

        // A person's save of the assistant's draft is theirs, and the draft leaves the assistant's scope.
        var taken=await AsEditor((c,ct)=>Save(c,Request(renamed with{Name="Synthetic person revision"},revised),CurveDraftSaver.Person,ct));
        personal.Add(requests[^1]);
        if((taken.Generation,taken.Saver)!=(3,CurveSaver.Person))throw new Exception("A person's save did not record a person as its saver.");
        Shows(taken,"{\"savedBy\":\"person\"}");
        await Refused("custom_curve_agent_scope",()=>AsAgent((c,ct)=>Save(c,Request(renamed with{Name="Synthetic retaken"},taken),Assistant(steps[4]),ct)));
        var read=await AsEditor((c,ct)=>store.Read(c,ct));
        Shows(read.Single(v=>v.Id==created.Id),"{\"savedBy\":\"person\"}");
        Console.WriteLine("PASS a refused save, for a second claim in one context, another token, a person's context, a new curve naming a version or a person's draft, is a structured refusal and writes nothing; a person's save records a person");

        // A second draft, whose exact references the runner then moves to an earlier bundle.
        var moving=await AsAgent((c,ct)=>Save(c,Request(definition with{Code=definition.Code+"_m"}),Assistant(steps[5]),ct));
        receipted.Add(requests[^1]);
        Console.WriteLine(JsonSerializer.Serialize(new{curve=created.Id,moving=moving.Id,requests,receipted,personal,refusedCodes},json));
    }

    // The assistant's identical graph is not its current generation once the exact references it resolves to have moved:
    // the save writes a new generation pinned to the selected bundle (D260).
    static async Task Moved(PreviewDatabase database,CustomCurvesStore store,AgentIdentity agent,Guid turn,byte[] token,int step,RecipeDefinition definition,Guid curve)
    {
        var request=new CustomCurveSaveRequest(Guid.NewGuid(),definition,ImmutableDictionary<string,CurveNodePosition>.Empty,Guid.NewGuid(),curve,1,1,"draft");
        var saved=await database.WithAgent(agent,TimeSpan.FromSeconds(RuntimeSet.MaxReadSeconds),["ReadMarketData"],_=>{},async(c,ct)=>
            await store.Save(c,request,await database.Read(c,ct,observationIds:[],includeCalendarDays:false),CurveDraftSaver.Assistant(turn,step,token),ct),CancellationToken.None);
        if(saved.Generation!=2||saved.Saver!=new CurveSaver("assistant")||saved.Sources.Values.Any(s=>s.RevisionId!=database.Manifest.RevisionId)||saved.Sources.IsEmpty)
            throw new Exception("An identical graph whose exact references moved was not saved as a new generation on the selected bundle.");
        Console.WriteLine("PASS the assistant's identical graph whose exact references moved writes a new generation pinned to the selected bundle (D260)");
        Console.WriteLine(JsonSerializer.Serialize(new{request=request.RequestId},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
