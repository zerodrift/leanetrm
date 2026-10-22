using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed record ConnectedSampleObservation(Guid Id,Guid SeriesId,string Use);
public sealed record ConnectedSampleEvidence(Guid SnapshotId,DateTimeOffset At,string SourceCurve,Guid ContractId,
    bool FreezeHistorical,ImmutableArray<ConnectedSampleObservation> Observations);
public sealed record ConnectedMarketEvidence(Guid RevisionId,string ManifestId,
    ImmutableArray<CustomCurveProjectionBinding> Recipes,ImmutableArray<ConnectedSampleEvidence> Samples);
public sealed record ConnectedPreparationWork(int SourceBuilds,int EvaluatedSourceNodes,int ReusedSourceNodes);

public static partial class CustomCurves
{
    internal sealed record PreparedMarket(ConnectedValuation Valuation,CurveBuild Authority,
        ImmutableDictionary<CurveRequest,CurvePointKey> Bindings,ConnectedMarketEvidence Evidence,ConnectedPreparationWork Preparation);

    // Only authorized retained data enters this resolver. Synthetic runtime
    // aliases are coordinates in that materialization, never database identities.
    private sealed class PreparedBindings(HashSet<CurveKey> definitions,ResolvedCalculationContext selected):ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey logical,ResolvedCalculationContext context)=>
            throw Invalid("A booked custom projection requires an exact published version.");
        public void RequireVisible(CurveKey exact,ResolvedCalculationContext context)
        {
            if(context!=selected||!definitions.Contains(exact))throw Invalid("The graph references a definition outside its authorized materialization.");
        }
    }

    internal static Guid StableSampleId(MarketSnapshot snapshot)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{snapshot.Definitions,snapshot.ValuationAt,snapshot.MarketCutoff,
            inputs=snapshot.Selections.OrderBy(s=>s.Input.Series.Id).ThenBy(s=>s.Input.Use)
                .Select(s=>new{s.Input,s.Observation})},RiskJobs.Json);
        return new Guid(SHA256.HashData(bytes).AsSpan(0,16));
    }

    internal static async Task<PreparedMarket> PrepareMarket(NpgsqlConnection connection,PreviewDatabase database,
        PreviewData data,InputFrame frame,MarketSnapshot snapshot,ScheduleContext context,
        ImmutableArray<Instrument> instruments,ImmutableArray<CurveRequest> dependencies,CancellationToken ct,
        ConnectedMarketEvidence? retained=null)
    {
        var exact=PinnedContext.Exact(context);
        if(frame.ContextId!=exact.ContextId||snapshot.Id!=frame.Id||snapshot.ValuationAt!=frame.At||
            snapshot.Definitions!=data.DefinitionRevision)
            throw Invalid("Connected pricing requires the exact retained financial and market context.");
        var custom=instruments.OfType<FinancialSwapInstrument>().Where(i=>i.Floating.CustomProjection is not null)
            .SelectMany(i=>i.Floating.Projections.Select(p=>new{Binding=i.Floating.CustomProjection!,Original=p.Market}))
            .Distinct().ToArray();
        if(custom.Length==0)throw Invalid("Select at least one published custom projection for connected preparation.");
        var aliases=custom.ToDictionary(p=>p.Binding.Request(p.Original),p=>p.Original);
        var needed=dependencies.ToHashSet();
        var originals=dependencies.Where(d=>!aliases.ContainsKey(d)).ToImmutableArray();
        var authority=data.Model.Build(snapshot,originals);
        var inputManifest=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{
            snapshot.Id,snapshot.Definitions,snapshot.ValuationAt,snapshot.MarketCutoff,context=exact,
            selections=snapshot.Selections.OrderBy(s=>s.Input.Series.Id).ThenBy(s=>s.Input.Use)},RiskJobs.Json)));
        var resolved=new ResolvedCalculationContext(exact.ContextId!.Value,inputManifest,frame.At,
            SchedulePreview.ParseAt(exact.KnownAt),frame.At,snapshot.MarketCutoff);
        var export=data.Model.ExportConnected(authority,false,resolved,ct);
        var definitions=export.Definitions.ToList();
        var bindings=ImmutableDictionary.CreateBuilder<CurveRequest,CurvePointKey>();
        foreach(var request in dependencies.Where(d=>!aliases.ContainsKey(d)))bindings.Add(request,export.Points[(request,InputUse.Variable)]);
        var required=custom.Where(c=>needed.Contains(c.Binding.Request(c.Original))).ToArray();
        var recipeBindings=required.Select(c=>c.Binding).Distinct().OrderBy(c=>c.CurveId).ThenBy(c=>c.Version)
            .ThenBy(c=>c.OutputNode,StringComparer.Ordinal).ThenBy(c=>c.Position).ToImmutableArray();
        var samples=ImmutableArray.CreateBuilder<ConnectedRecipeSample>();
        if(!recipeBindings.IsEmpty)
        {
            var historyPins=retained is null?(ImmutableArray<Guid>?)null:retained.Samples.SelectMany(s=>s.Observations.Select(o=>o.Id))
                .Concat(frame.ObservationIds).Distinct().ToImmutableArray();
            var sourceData=await database.Read(connection,ct,data.DefinitionRevision.RevisionId,data,historyPins,
                includeCalendarDays:false,observationScope:retained is null?ObservationReadScope.FactHistory:ObservationReadScope.ExactPins);
            var saved=await new CustomCurvesStore(database).Read(connection,ct);
            foreach(var group in recipeBindings.GroupBy(b=>(b.CatalogId,b.CurveId,b.Version)))
            {
                ct.ThrowIfCancellationRequested();
                if(group.Key.CatalogId!=data.DefinitionRevision.CatalogId)throw Invalid("The projection must belong to this exact market catalog.");
                var version=saved.SingleOrDefault(v=>v.Id==group.Key.CurveId&&v.Version==group.Key.Version)
                    ??throw Invalid("The exact published custom curve is unavailable in this knowledge context.");
                if(version.State!="published"||version.EngineVersion!=RecipeEngine.Version||version.PublishedAt is null||
                    version.PublishedAt>SchedulePreview.ParseAt(exact.KnownAt)||version.Sources.Values.Any(s=>s.CatalogId!=group.Key.CatalogId||s.RevisionId!=data.DefinitionRevision.RevisionId)||
                    group.Any(b=>b.OutputNode!=version.Definition.Output)||version.Definition.Nodes.Any(n=>n.Kind=="sql_transform"))
                    throw Invalid("Connected projections require a published managed recipe, its exact output and retained source revision.");
                var materialized=await Inputs(connection,database,version.Definition,sourceData,frame,version.Sources,version.Calendars,version.Schedules,exact,ct);
                var graph=RecipeEngine.Connect(version.Definition,materialized.Values,
                    new(group.Key.CatalogId,group.Key.CurveId,group.Key.Version,version.EngineVersion),materialized.Samples,resolved,ct,materialized.References);
                definitions.AddRange(graph.Definitions);samples.AddRange(materialized.Samples);
                foreach(var binding in group)
                {
                    var outputs=graph.Outputs.Where(p=>p.Position==binding.Position).ToArray();
                    if(outputs.Length!=1)throw Invalid("The explicit output position must identify exactly one computed price.");
                    foreach(var item in required.Where(c=>c.Binding==binding))bindings.Add(binding.Request(item.Original),outputs[0].Point);
                }
            }
        }
        if(bindings.Count!=dependencies.Length)throw Invalid("Every declared pricing dependency must have exactly one retained graph binding.");
        var merged=ConnectedCurves.MergeDefinitions(definitions);
        // Fully fixed instruments have no projected roots; source authority is
        // still retained for the ordinary fixing contract.
        var roots=bindings.Values.Distinct().ToImmutableArray();
        if(roots.IsEmpty)throw Invalid("A fully fixed custom projection has no connected pricing dependencies.");
        var plan=ConnectedCurves.Compile(merged,roots,resolved,new PreparedBindings(merged.Select(d=>d.Key).ToHashSet(),resolved),ct);
        if(samples.Any(s=>s.Build.Snapshot.MarketCutoff!=s.Build.Snapshot.ValuationAt))
            throw Invalid("This retained sample contract requires market cutoff and sample valuation to be the same explicit instant.");
        var evidence=new ConnectedMarketEvidence(data.DefinitionRevision.RevisionId,plan.ManifestId,recipeBindings,
            samples.DistinctBy(s=>(s.Source,s.Date,s.Contract)).OrderBy(s=>s.Source.Code,StringComparer.Ordinal).ThenBy(s=>s.Date).ThenBy(s=>s.Contract.Id)
                .Select(s=>new ConnectedSampleEvidence(s.Build.Snapshot.Id,s.Build.Snapshot.ValuationAt,s.Source.Code,s.Contract.Id,s.FreezeHistorical,
                    s.Build.Snapshot.Selections.OrderBy(o=>o.Input.Series.Id).ThenBy(o=>o.Input.Use)
                        .Select(o=>new ConnectedSampleObservation(o.Observation.Key.Id,o.Input.Series.Id,o.Input.Use.ToString())).ToImmutableArray())).ToImmutableArray());
        if(retained is not null&&JsonSerializer.Serialize(evidence,RiskJobs.Json)!=JsonSerializer.Serialize(retained,RiskJobs.Json))
            throw new MarketDataException("risk_graph_manifest","The rebuilt graph differs from its admitted recipe, sample or topology manifest.");
        var valuation=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),snapshot,samples.Select(s=>s.Build.Snapshot).Distinct().ToImmutableArray(),ct);
        var builds=new HashSet<CurveBuild>(ReferenceEqualityComparer.Instance){authority};
        foreach(var sample in samples)builds.Add(sample.Build);
        return new(valuation,authority,bindings.ToImmutable(),evidence,new(builds.Count,builds.Sum(b=>b.EvaluatedConstituents),builds.Sum(b=>b.ReusedConstituents)));
    }
}
