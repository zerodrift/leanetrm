using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData.CustomCurves;

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
            """,["ReadMarketData"])
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
        if(input.Engine!=RecipeEngine.Version)throw new MarketData.MarketDataException("custom_curve_engine","This proposal requires its original supported curve engine.");
        var definition=RecipeFormula.Compile(input.Formula,input.Code,input.Name);CustomCurves.Validate(definition);return definition;
    }
}
