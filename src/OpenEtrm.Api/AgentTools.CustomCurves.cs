using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using CurveReferences=(System.Collections.Immutable.ImmutableDictionary<string,OpenEtrm.Api.CurveSourceReference> Sources,
    System.Collections.Immutable.ImmutableDictionary<string,OpenEtrm.Api.CalendarReference> Calendars,
    System.Collections.Immutable.ImmutableDictionary<string,OpenEtrm.Api.CurveScheduleReference> Schedules);

namespace OpenEtrm.Api;

public sealed partial class AgentTools
{
    static ImmutableArray<AgentTool> CurveTools=>[
        Tool("read_custom_curves","Reads the custom-curve operators, templates, native source directory and exact published versions at the turn's pinned knowledge. Use these explicit backend contracts to prepare a curve; never infer missing weights, units, alignment or dates. Native contract identities are available from read_market_data.",
            """{"type":"object","properties":{},"additionalProperties":false}""",["ReadMarketData"]),
        Tool("prepare_custom_curve","Validates and prices a proposed custom curve through the same managed operation as the curve editor, at this turn's pinned dates and exact inputs. Read read_custom_curves first. Supply the backend formula and exact engine, code and name; ask the user about missing financial choices. Returned dependencies identify inputs, not PV exposure. Does not save or publish. Its target opens an explicitly reviewed draft in the editor; it is not a retained result or permission to execute portfolio risk.",
            """
            {"type":"object","properties":{"code":{"type":"string","minLength":1,"maxLength":32,"pattern":"^[a-z][a-z0-9_]{0,31}$"},
             "name":{"type":"string","minLength":1,"maxLength":80},"engine":{"type":"string","enum":["custom_curve_recipe_v2"]},
             "formula":{"type":"string","minLength":1,"maxLength":6144}},"required":["code","name","engine","formula"],"additionalProperties":false}
            """,["ReadMarketData"]),
        // Retain the legacy definition for stored versions; its execution path is closed by D284.
        Tool("save_custom_curve_draft","This legacy direct-save tool is disabled and returns approval_required. It never publishes or saves. "+
            "Use prepare_custom_curve for a preview, then discover_page_operations and propose_page_action for the ordinary custom-curves/save "+
            "command. Only the user's separate authorization can save the reviewed proposal.",
            $$$"""
            {"type":"object","properties":{"code":{"type":"string","minLength":1,"maxLength":32,"pattern":"^[a-z][a-z0-9_]{0,31}$"},
             "name":{"type":"string","minLength":1,"maxLength":80},"engine":{"type":"string","enum":["custom_curve_recipe_v2"]},
             "formula":{"type":"string","minLength":1,"maxLength":6144},
             "curveId":{"type":["string","null"],"pattern":"{{{Uuid}}}","description":"The draft to revise, as an earlier save returned it, or null for a new curve."}},
             "required":["code","name","engine","formula","curveId"],"additionalProperties":false}
            """,["ReadMarketData","WriteMarketDefinitions"])
    ];
    async Task<ToolResult> CurveCatalog(NpgsqlConnection connection,PinnedContext pin,CancellationToken ct)
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        var read=await CustomCurves.CatalogRead(connection,database,pin,ct);
        var result=JsonSerializer.SerializeToNode(read.Value,JsonSerializerOptions.Web)!.AsObject();
        result.Remove("canEdit");result.Remove("canPublish");
        foreach(var source in result["sources"]!.AsArray())source!.AsObject().Remove("contracts");
        foreach(var recipe in result["recipes"]!.AsArray()){
            var row=recipe!.AsObject();var definition=row["definition"]!.AsObject();
            row["code"]=definition["code"]!.DeepClone();row["name"]=definition["name"]!.DeepClone();row.Remove("definition");row.Remove("layout");
        }
        return CurveToolResult(read,result);
    }
    async Task<ToolResult> PrepareCurve(NpgsqlConnection connection,PinnedContext pin,PrepareCurveArguments input,CancellationToken ct)
    {
        var definition=CompileCurve(input);
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        var read=await CustomCurves.PreviewRead(connection,database,pin,new(definition,pin.RequireFrame().Id),ct);
        var result=new JsonObject{["definition"]=JsonSerializer.SerializeToNode(definition,JsonSerializerOptions.Web),
            ["formula"]=RecipeFormula.Format(definition),["preview"]=JsonSerializer.SerializeToNode(read.Value,JsonSerializerOptions.Web),
            ["status"]="proposed_draft",["engine"]=RecipeEngine.Version};
        return CurveToolResult(read,result);
    }
    static ToolResult CurveToolResult(CustomCurves.CurveRead read,JsonObject value)=>new(value,read.Rows,false,
        [..read.ReferenceCatalogs.Select(c=>new ReadAccess("reference_access",c,"ReadMarketData"))]);
    internal static RecipeDefinition CompileCurve(PrepareCurveArguments input)
    {
        if(input.Engine!=RecipeEngine.Version)throw new MarketDataException("custom_curve_engine","This proposal requires its original supported curve engine.");
        var definition=RecipeFormula.Compile(input.Formula,input.Code,input.Name);CustomCurves.Validate(definition);return definition;
    }

    // Refusals of a save before any claim or read (D260): a historical pin, which the page shows read-only, and the curve the
    // page reports open with unsaved edits, which the person saves or discards first.
    internal static string? DraftRefusal(TurnReads turn,SaveCurveArguments save)=>!turn.Pin.Working?"historical_read_only"
        :save.CurveId is {} curve&&PageReports.HoldsUnsaved(turn.PageReport,curve)?"custom_curve_open_unsaved":null;

    // How a save that bound its context ended (D260): authority lost at a write or at commit is delegation_denied, and a code
    // already held, by a curve the agent may not see too, is a conflict that names nothing of it.
    internal static (string? Code,ToolStepEnd End) ClassifySave(Exception e)=>e switch
    {
        PostgresException{SqlState:"42501"}=>("delegation_denied",ToolStepEnd.Refusal),
        PostgresException{SqlState:"23505"}=>("custom_curve_conflict",ToolStepEnd.Refusal),
        _=>Classify(e)
    };

    // The exact references a definition resolves to at the turn's pin, read as its preparation reads them.
    internal Task<CurveReferences> Previewed(TurnReads turn,RecipeDefinition definition,CancellationToken ct)=>
        database.WithAgent(turn.Agent,turn.ReadLimit,MarketData,_=>{},async(connection,t)=>
        {
            var context=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,turn.Pin.ContextId,t)).Context;
            await turn.Pin.Page(context).Bind(connection,database.Manifest.CatalogId,t);
            var data=await database.Read(connection,t,observationIds:[],includeCalendarDays:false);
            return await new CustomCurvesStore(database).References(connection,definition,data,t);
        },ct);

    // What a save step leaves: the version saved, or the one already holding the definition, and whether this step wrote it.
    internal sealed record DraftSave(CustomCurveVersion Version,bool Written);

    // The managed save of the assistant's draft for one step, in the caller's transaction under the agent's context (D260).
    // Its request identity and a new curve's identity are name-based on the turn and step (D238), so a re-run of the step
    // replays its first outcome, a new curve's included, and writes nothing, while the draft is still the assistant's (the
    // claim precedes the replay); a second save in the turn is a new step and a new
    // generation. A revision names the assistant's own draft at version 1, the only version its scope admits, at the
    // generation its claim locks. Whether the step wrote is whether its saver is recorded.
    internal async Task<DraftSave> SaveDraft(NpgsqlConnection connection,TurnReads turn,SaveCurveArguments save,RecipeDefinition definition,int step,
        TurnWrites writes,CurveReferences pinned,CancellationToken ct)
    {
        var (request,created)=DraftKeys(turn.TurnId,step);
        var body=new CustomCurveSaveRequest(request,definition,ImmutableDictionary<string,CurveNodePosition>.Empty,turn.Pin.RequireFrame().Id,
            save.CurveId,save.CurveId is null?null:1);
        var data=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
        var saved=await new CustomCurvesStore(database).Save(connection,body,data,writes.Saver(step),ct,save.CurveId is null?created:null,pinned);
        await using var written=ScheduleStore.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM market_data.custom_curve_generation_savers WHERE catalog_id=$1 AND turn_id=$2 AND step=$3)",
            database.Manifest.CatalogId,turn.TurnId,step);
        return new(saved,Equals(await written.ExecuteScalarAsync(ct),true));
    }

    // The result a save returns: the curve it saved, with the draft target's turn and step, or the unchanged draft.
    static JsonObject Saved(DraftSave draft,Guid turn,int step)
    {
        var version=draft.Version;
        var result=new JsonObject{["status"]=draft.Written?"saved_draft":"unchanged_draft",
            ["curve"]=new JsonObject{["id"]=version.Id.ToString(),["code"]=version.Definition.Code,["name"]=version.Definition.Name,["kind"]=version.Kind,
                ["version"]=version.Version,["generation"]=version.Generation,["state"]=version.State,["savedBy"]=version.Saver.SavedBy},
            ["formula"]=RecipeFormula.Format(version.Definition)};
        if(draft.Written)result["draft"]=new JsonObject{["turnId"]=turn.ToString(),["step"]=step};
        else result["message"]="The draft already holds this definition on the same exact references, so nothing was saved. Curve builder lists it.";
        return result;
    }

    // A save step's request identity, UUIDv5(turn, step), and the identity its new curve takes (D238).
    internal static (Guid Request,Guid Curve) DraftKeys(Guid turn,int step)=>
        (NameUuid(turn,step.ToString(CultureInfo.InvariantCulture)),NameUuid(turn,"curve:"+step.ToString(CultureInfo.InvariantCulture)));

    // A name-based identity (RFC 4122 version 5): SHA-1 of the namespace and the name, versioned and with its variant set.
    internal static Guid NameUuid(Guid space,string name)
    {
        var hash=SHA1.HashData([..space.ToByteArray(bigEndian:true),..Encoding.UTF8.GetBytes(name)]);
        hash[6]=(byte)(hash[6]&0x0F|0x50);
        hash[8]=(byte)(hash[8]&0x3F|0x80);
        return new Guid(hash.AsSpan(0,16),bigEndian:true);
    }
}
