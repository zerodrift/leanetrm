using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using System.Globalization;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    internal sealed record CurveRead(object Value,int Rows,ImmutableArray<Guid> ReferenceCatalogs);
    // Callers bind this pin once in their authorized request transaction.
    internal static async Task<CurveRead> CatalogRead(NpgsqlConnection connection,PreviewDatabase database,PinnedContext pin,CancellationToken ct)
    {
        var store=new CustomCurvesStore(database);
        var data=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
        var schedules=new ScheduleStore(database.Manifest.CatalogId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId);
        var versions=pin.Context is {} selected?await schedules.ReadAt(connection,selected,ct):await schedules.Read(connection,ct);
        var calendars=await schedules.Calendars(connection,ct);
        var recipes=await store.Read(connection,ct);
        var publishedSources=new List<object>();
        foreach(var recipe in recipes.Where(r=>r.State=="published"&&r.EngineVersion==RecipeEngine.Version))
        {
            await using var eligible=ScheduleStore.Command(connection,"SELECT security.custom_curve_uses_revision($1,$2,$3,$4)",
                data.DefinitionRevision.CatalogId,recipe.Id,recipe.Version,data.DefinitionRevision.RevisionId);
            if(Equals(await eligible.ExecuteScalarAsync(ct),true))publishedSources.Add(new{
                label=$"{recipe.Definition.Name} ({recipe.Definition.Code}) · v{recipe.Version} · {recipe.Definition.Output}",kind=CurveKinds.Of(recipe.Definition),
                reference=new PublishedRecipeReference(data.DefinitionRevision.CatalogId,recipe.Id,recipe.Version,recipe.Definition.Output)});
        }
        async Task<bool> Can(string permission)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.has_permission($1)",permission);
            return pin.Context is null&&Equals(await command.ExecuteScalarAsync(ct),true);
        }
        var value=new{kindsVersion=CurveKinds.Version,context=pin.Context,canEdit=await Can("WriteMarketDefinitions"),canPublish=await Can("SealMarketDefinitions"),
            sources=data.Definitions["curves"].Select(row=>new{code=row.Text("code"),name=row.Text("name"),kind=PreviewData.CurveKind(row.Text("rule"),row.OptionalText("direct_role")),catalogId=data.DefinitionRevision.CatalogId,revisionId=data.DefinitionRevision.RevisionId,contracts=data.DefinitionRows("curve_members","curve_code",row.Text("code")).OrderBy(m=>m.GetProperty("ordinal").GetInt32()).Select(m=>data.ContractRows(m.Id("contract_id")).Single()).Select(k=>new{id=k.Key.Id,label=k.DeliveryFrom.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),k.DeliveryFrom,k.DeliveryTo})}),
            calendars=calendars.Select(row=>new{code=row.Reference.Code,name=row.Reference.Code,row.Reference.CatalogId,row.Reference.RevisionId}),
            schedules=versions.Where(v=>v.State=="published").Select(v=>new{value=v.Id.ToString("D")+":"+v.Version.ToString(CultureInfo.InvariantCulture),name=v.Definition.Name,v.Id,v.Version}),
            operators=Operators(),templates=Templates(data),recipes,publishedSources,formulaSupported=true,
            componentHistory=new{policy=RecipeComponentHistory.Policy,label="Raw prices · contract roll jumps retained",kinds=new[]{"select","weighted_contracts","sample"},selectionModes=new[]{"positions"},sampleSelectionPort="selection",
                sampling="context_utc_time",corrections="exact_context_frame_history_latest_visible",
                description="Sample at the selected close's UTC time. Keep the component's contract-binding rule and read historical corrections visible at the selected knowledge cutoff. Every requested observation is required."}};
        return new(value,recipes.Length+publishedSources.Count+data.Definitions["curves"].Length+versions.Length+calendars.Length,
            await ReferenceCatalogs(connection,data,recipes,versions.Where(v=>v.Calendar is not null).Select(v=>v.Calendar!.CatalogId).Concat(calendars.Select(c=>c.Reference.CatalogId)),ct));
    }
    internal static async Task<CurveRead> PreviewRead(NpgsqlConnection connection,PreviewDatabase database,PinnedContext pin,CustomCurvePreviewRequest body,CancellationToken ct,ComponentHistoryRequest? history=null)
    {
        var store=new CustomCurvesStore(database);
        var context=pin.Context??throw Invalid("Recipe pricing requires its resolved financial context.");
        var frame=pin.RequireFrame() with{ContextId=context.ContextId};
        var data=await database.Read(connection,ct,includeCalendarDays:false);
        Validate(body.Definition);
        var references=await store.References(connection,body.Definition,data,ct);
        if((body.Id is null)!=(body.Version is null))throw Invalid("Supply both the saved curve identity and exact version.");
        if(body.Id is {} id)
        {
            var saved=(await store.Read(connection,ct)).SingleOrDefault(v=>v.Id==id&&v.Version==body.Version)??throw Invalid("This exact curve version is unavailable in the selected knowledge context.");
            if(saved.EngineVersion!=RecipeEngine.Version)throw Invalid("The selected retained curve engine is unavailable.");
            if(!Equivalent(saved.Definition,body.Definition)||saved.Sources.Any(p=>!references.Sources.TryGetValue(p.Key,out var value)||value!=p.Value)||saved.Calendars.Any(p=>!references.Calendars.TryGetValue(p.Key,out var value)||value!=p.Value)||saved.Schedules.Any(p=>!references.Schedules.TryGetValue(p.Key,out var value)||value!=p.Value))throw Invalid("The selected graph or exact references differ from its retained version.");
        }
        var inspected=body.Definition;
        if(history is not null)
        {
            inspected=RecipeComponentHistory.Build(body.Definition,history.Node,history.Dates,history.Policy);
            Validate(inspected);
            references=await store.References(connection,inspected,data,ct);
        }
        var graph=await PrepareDefinition(connection,database,inspected,data,frame,references.Sources,references.Calendars,references.Schedules,
            pin.Context??throw Invalid("Recipe pricing requires its resolved financial context."),ct);
        var value=Result(EvaluateGraph(graph,CalculationContext(frame,data.DefinitionRevision,context),database,ct),data,pin,inspected,body.Id,body.Version,history is not null);
        if(history is not null)value=new{node=history.Node,policy=history.Policy,preview=value};
        var referenceCatalogs=graph.Definitions.SelectMany(d=>d.Points).SelectMany(point=>point.Expression is ConnectedRecipeProjection projected?
            projected.Declaration.References.Calendars.Select(c=>c.Reference.Revision.CatalogId).Concat(projected.Declaration.References.Schedules.Where(s=>s.Calendar is not null).Select(s=>s.Calendar!.Reference.Revision.CatalogId)):[]);
        return new(value,graph.Outputs.Length,[..referenceCatalogs.Append(data.ReferenceRevision.CatalogId).Distinct()]);
    }
    static async Task<ImmutableArray<Guid>> ReferenceCatalogs(NpgsqlConnection connection,PreviewData data,
        ImmutableArray<CustomCurveVersion> versions,IEnumerable<Guid> calendars,CancellationToken ct)
    {
        var result=calendars.Concat(versions.SelectMany(v=>v.Calendars.Values.Select(c=>c.CatalogId))).Append(data.ReferenceRevision.CatalogId).ToHashSet();
        foreach(var schedule in versions.SelectMany(v=>v.Schedules.Values).Distinct())
        {
            await using var command=ScheduleStore.Command(connection,"SELECT calendar_catalog_id FROM market_data.date_schedule_versions WHERE(catalog_id,schedule_id,version)=($1,$2,$3)",schedule.CatalogId,schedule.Id,schedule.Version);
            await using var reader=await command.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))throw Invalid("A retained schedule version is unavailable.");
            if(!reader.IsDBNull(0))result.Add(reader.GetGuid(0));
        }
        foreach(var source in versions.SelectMany(v=>v.Sources.Values).Select(s=>(s.CatalogId,s.RevisionId)).Distinct())
        {
            await using var command=ScheduleStore.Command(connection,"SELECT reference_catalog_id FROM market_data.revisions WHERE(catalog_id,id)=($1,$2)",source.CatalogId,source.RevisionId);
            if(await command.ExecuteScalarAsync(ct) is Guid reference)result.Add(reference);
            else throw Invalid("A retained native source revision is unavailable.");
        }
        return [..result.Order()];
    }
}
