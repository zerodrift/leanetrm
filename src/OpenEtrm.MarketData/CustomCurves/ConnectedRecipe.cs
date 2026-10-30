using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.CustomCurves;

public sealed record ConnectedRecipeIdentity(Guid CatalogId,Guid CurveId,int Version,string EngineVersion);
public sealed record ConnectedRecipeSample(CurveKey Source,DateOnly Date,ContractKey Contract,CurveModel Model,MarketSnapshot Snapshot,bool FreezeHistorical);
public sealed record ConnectedRecipeOutput(CurvePointKey Point,int Position,ImmutableArray<DateOnly> Dates,
    ImmutableArray<ContractKey> Contracts,int SampleCount);
public sealed record ConnectedRecipeSourceReference(string Node,CurveKey Curve);
public sealed record ConnectedPublishedRecipeReference(string Node,PublishedRecipeReference Reference,CurveKey Curve,string ManifestId);
public sealed record ConnectedRecipeDateBinding(string Node,ImmutableArray<DateOnly> Dates);
public sealed record ConnectedRecipeCalendarPin(string Node,ReferenceKey Reference,DateSet BusinessDates);
public sealed record ConnectedRecipeSchedulePin(string Node,Guid CatalogId,Guid ScheduleId,int Version,string EngineVersion,
    DateTimeOffset KnownFrom,int PublicationOrder,string Selection,int Origin,int First,int Count,
    ImmutableArray<MonthlyOccurrence> Occurrences,ConnectedRecipeCalendarPin? Calendar);
public sealed record ConnectedRecipeReferencePins(ImmutableArray<ConnectedRecipeCalendarPin> Calendars,
    ImmutableArray<ConnectedRecipeSchedulePin> Schedules);
public sealed record ConnectedRecipeProjection(CurvePointReference Value,ConnectedRecipeDeclaration Declaration,int Position):ConnectedCurveExpression;

public sealed record ConnectedRecipeDeclaration(ConnectedRecipeIdentity Identity,RecipeDefinition Definition,
    ImmutableArray<ConnectedRecipeSourceReference> Sources,ImmutableArray<ConnectedRecipeDateBinding> Dates,
    ConnectedRecipeReferencePins References,ImmutableArray<ConnectedPublishedRecipeReference> PublishedSources=default)
{
    public CurveKey Curve=>new(new(Identity.CatalogId,ConnectedIdentity.Id(new{Version="saved_recipe_graph_v1",Identity})),Definition.Code);
    public string ManifestId=>ConnectedIdentity.Digest(Canonical);
    internal object Canonical=>new{Adapter="saved_recipe_graph_v1",Identity,Definition.Code,Definition.Name,Definition.Output,
        Nodes=Definition.Nodes.OrderBy(n=>n.Id,StringComparer.Ordinal).Select(NodeEvidence).ToArray(),
        Sources=Sources.OrderBy(s=>s.Node,StringComparer.Ordinal).ToArray(),Dates=Dates.OrderBy(d=>d.Node,StringComparer.Ordinal).ToArray(),
        PublishedSources=PublishedSources.IsDefault?[]:PublishedSources.OrderBy(s=>s.Node,StringComparer.Ordinal).ToArray(),
        References=new{Calendars=References.Calendars.OrderBy(c=>c.Node,StringComparer.Ordinal).Select(CalendarEvidence).ToArray(),
            Schedules=References.Schedules.OrderBy(s=>s.Node,StringComparer.Ordinal).Select(s=>new{s.Node,s.CatalogId,s.ScheduleId,s.Version,
                s.EngineVersion,s.KnownFrom,s.PublicationOrder,s.Selection,s.Origin,s.First,s.Count,s.Occurrences,
                Calendar=s.Calendar is {} calendar?CalendarEvidence(calendar):null}).ToArray()}};
    internal static JsonObject NodeEvidence(RecipeNode node)
    {
        var result=JsonSerializer.SerializeToNode(new{node.Id,node.Kind,
            ContractWeights=node.ContractWeights.IsDefault?[]:node.ContractWeights.OrderBy(w=>w.OutputPosition).ThenBy(w=>w.SourcePort,StringComparer.Ordinal).ThenBy(w=>w.ContractId).ToArray(),
            Parameters=node.Parameters.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new{p.Key,p.Value}).ToArray(),
            Inputs=node.Inputs.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new{p.Key,p.Value}).ToArray(),node.PublishedSource})!.AsObject();
        // Existing immutable declarations keep their canonical bytes. Only a
        // node that actually declares curve weights gains this evidence field.
        if(!node.PanelWeights.IsDefaultOrEmpty)result["PanelWeights"]=JsonSerializer.SerializeToNode(node.PanelWeights.OrderBy(w=>w.SourcePort,StringComparer.Ordinal).ToArray());
        return result;
    }
    internal static object CalendarEvidence(ConnectedRecipeCalendarPin calendar)=>new{calendar.Node,calendar.Reference,
        BusinessDates=new{calendar.BusinessDates.From,calendar.BusinessDates.To,Values=calendar.BusinessDates.Dates.ToArray()}};
    internal static string ScheduleEvidence(ConnectedRecipeSchedulePin schedule)=>ConnectedIdentity.Digest(new
    {schedule.CatalogId,schedule.ScheduleId,schedule.Version,schedule.EngineVersion,schedule.KnownFrom,schedule.PublicationOrder,
        schedule.Selection,schedule.Occurrences,Calendar=schedule.Calendar is {} calendar?new{calendar.Reference,
            calendar.BusinessDates.From,calendar.BusinessDates.To,Dates=calendar.BusinessDates.Dates.ToArray()}:null});
    internal void Validate(ResolvedCalculationContext context)
    {
        if(Identity is null||Identity.CatalogId==Guid.Empty||Identity.CurveId==Guid.Empty||Identity.Version<1||Identity.EngineVersion!=RecipeEngine.Version)
            throw new MarketDataException("recipe_identity","Select an exact saved recipe identity and its supported engine.");
        RecipeEngine.Validate(Definition);
        var published=PublishedSources.IsDefault?ImmutableArray<ConnectedPublishedRecipeReference>.Empty:PublishedSources;
        if(published.Any(p=>p is null||p.Reference is null||p.Reference.CatalogId!=Identity.CatalogId||p.Reference.CurveId==Guid.Empty||
            p.Reference.Version<1||p.Reference.OutputNode is null||p.Curve.Revision.CatalogId!=Identity.CatalogId||p.Curve.Revision.RevisionId==Guid.Empty||
            p.ManifestId is not {Length:64}||p.ManifestId.Any(c=>c is not(>= '0' and <= '9' or >= 'a' and <= 'f')))||published.Select(p=>p.Node).Distinct().Count()!=published.Length||
            !Definition.Nodes.Where(n=>n.Kind=="published_source").Select(n=>n.Id).ToHashSet().SetEquals(published.Select(p=>p.Node))||
            published.Any(p=>Definition.Nodes.Single(n=>n.Id==p.Node).PublishedSource!=p.Reference))
            throw new MarketDataException("recipe_reference","Retain every exact published source and graph manifest.");
        if(Sources.IsDefault||Dates.IsDefault||Sources.Any(s=>s is null)||Dates.Any(d=>d is null)||
            Sources.Select(s=>s.Node).Distinct().Count()!=Sources.Length||Dates.Select(d=>d.Node).Distinct().Count()!=Dates.Length||
            !Definition.Nodes.Where(n=>n.Kind=="source").Select(n=>n.Id).ToHashSet().SetEquals(Sources.Select(s=>s.Node))||
            !Definition.Nodes.Where(n=>n.Kind=="dates").Select(n=>n.Id).ToHashSet().SetEquals(Dates.Select(d=>d.Node))||
            Sources.Any(s=>s.Curve.Revision.CatalogId==Guid.Empty||s.Curve.Revision.RevisionId==Guid.Empty||
                Definition.Nodes.Single(n=>n.Id==s.Node).Parameters["curve"]!=s.Curve.Code)||
            Dates.Any(d=>d.Dates.IsDefaultOrEmpty||d.Dates.Length>366||d.Dates.Distinct().Count()!=d.Dates.Length||!d.Dates.SequenceEqual(d.Dates.Order())))
            throw new MarketDataException("recipe_input","Retain every exact source and ordered date binding in the recipe declaration.");
        if(References is null||References.Calendars.IsDefault||References.Schedules.IsDefault||References.Calendars.Any(c=>c is null)||References.Schedules.Any(s=>s is null)||
            References.Calendars.Select(c=>c.Node).Distinct().Count()!=References.Calendars.Length||References.Schedules.Select(s=>s.Node).Distinct().Count()!=References.Schedules.Length)
            throw new MarketDataException("recipe_reference","Supply explicit unique resolved calendar and schedule pins.");
        var dateNodes=Definition.Nodes.Where(n=>n.Kind=="dates").ToDictionary(n=>n.Id);
        if(!dateNodes.Values.Where(n=>n.Parameters.GetValueOrDefault("mode")=="prior_business").Select(n=>n.Id).ToHashSet().SetEquals(References.Calendars.Select(c=>c.Node))||
            !dateNodes.Values.Where(n=>n.Parameters.GetValueOrDefault("mode")=="schedule").Select(n=>n.Id).ToHashSet().SetEquals(References.Schedules.Select(s=>s.Node)))
            throw new MarketDataException("recipe_reference","Business-calendar and scheduled date nodes require their exact resolved reference pins.");
        void Calendar(ConnectedRecipeCalendarPin calendar)
        {
            Money.RequireKey(calendar.Reference);
            if(calendar.BusinessDates is null)throw new MarketDataException("recipe_reference","Retain the calendar's complete business-day coverage.");
        }
        foreach(var node in dateNodes.Values.Where(n=>n.Parameters.GetValueOrDefault("mode")=="delivery_range"))
            if(!Dates.Single(d=>d.Node==node.Id).Dates.SequenceEqual(RecipeEngine.DeliveryDates(node)))
                throw new MarketDataException("recipe_input","The retained delivery dates differ from the explicit range.");
        var contextDate=DateOnly.FromDateTime(context.ValuationAt.UtcDateTime);
        foreach(var calendar in References.Calendars)
        {
            Calendar(calendar);var node=dateNodes[calendar.Node];
            if(node.Parameters.GetValueOrDefault("calendar")!=calendar.Reference.Code||
                !int.TryParse(node.Parameters.GetValueOrDefault("count"),NumberStyles.None,CultureInfo.InvariantCulture,out var count)||
                !bool.TryParse(node.Parameters.GetValueOrDefault("includeContext"),out var includeContext)||
                !Dates.Single(d=>d.Node==node.Id).Dates.SequenceEqual(RecipeEngine.PriorBusinessDates(calendar.BusinessDates,contextDate,count,includeContext)))
                throw new MarketDataException("recipe_reference","The resolved business-date selection differs from its exact calendar and explicit policy.");
        }
        foreach(var schedule in References.Schedules)
        {
            var node=dateNodes[schedule.Node];
            if(schedule.CatalogId!=Identity.CatalogId||schedule.ScheduleId==Guid.Empty||schedule.Version<1||
                schedule.EngineVersion is not ("date_schedule_v1" or "date_schedule_v2")||
                !TemporalCurveBindings.IsInstant(schedule.KnownFrom)||schedule.KnownFrom>context.KnownAt||schedule.PublicationOrder<1||
                node.Parameters.GetValueOrDefault("schedule")!=schedule.ScheduleId.ToString("D")+":"+schedule.Version.ToString(CultureInfo.InvariantCulture)||
                !int.TryParse(node.Parameters.GetValueOrDefault("first"),NumberStyles.None,CultureInfo.InvariantCulture,out var first)||first!=schedule.First||
                !int.TryParse(node.Parameters.GetValueOrDefault("count"),NumberStyles.None,CultureInfo.InvariantCulture,out var count)||count!=schedule.Count||
                schedule.EngineVersion=="date_schedule_v2"&&schedule.Calendar is null)
                throw new MarketDataException("recipe_reference","Retain the exact published schedule, supported engine, knowledge receipt and explicit occurrence range.");
            if(schedule.Calendar is {} calendar){Calendar(calendar);if(calendar.Node!=schedule.Node)throw new MarketDataException("recipe_reference","A schedule calendar pin belongs to its date node.");}
            var selected=RecipeEngine.ScheduleDates(schedule.Occurrences,schedule.Selection,contextDate,schedule.First,schedule.Count,out var origin);
            if(origin!=schedule.Origin||!Dates.Single(d=>d.Node==node.Id).Dates.SequenceEqual(selected))
                throw new MarketDataException("recipe_reference","The resolved schedule selection differs from its retained occurrence mapping.");
        }
    }
}

public sealed class ConnectedRecipeGraph
{
    public CurveKey Curve{get;internal init;}
    public ConnectedRecipeIdentity Identity{get;internal init;}=null!;
    public ImmutableArray<ConnectedCurveDefinition> Definitions{get;internal init;}
    public ImmutableArray<ConnectedRecipeOutput> Outputs{get;internal init;}
    public int SourceCompilations{get;internal init;}
    public int DeclaredSourceNodes{get;internal init;}
    public int PublishedDepth{get;internal init;}
    internal ResolvedCalculationContext Context{get;init;}=null!;
    internal ImmutableArray<(ConnectedRecipeSample Sample,CurvePointKey Point)> SamplePoints{get;init;}
    internal ConnectedRecipeDeclaration Declaration{get;init;}=null!;
    internal RecipeEngine.Panel PreparedPanel{get;init;}=null!;
    internal CurvePointKey OutputPoint(int index)=>new(Curve,ConnectedIdentity.Id(new{Version="saved_recipe_output_v2",identity=Identity,Output=Declaration.Definition.Output,Index=index}));
    public RecipeResult Result(ConnectedScenario scenario,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(scenario is null||scenario.Plan.Context!=Context||Outputs.Any(o=>scenario.Plan.GetPoint(o.Point).Expression is not ConnectedRecipeProjection p||
            p.Declaration.ManifestId!=Declaration.ManifestId||p.Position!=o.Position))
            throw new MarketDataException("recipe_context","A connected recipe result requires its exact declared graph and resolved calculation context.");
        var points=Outputs.Select(o=>new RecipePoint(o.Position.ToString(CultureInfo.InvariantCulture),o.Position,scenario.Price(o.Point,cancellation),o.Contracts,o.Dates,o.SampleCount)).ToImmutableArray();
        var evidence=new ConnectedInputEvidence(scenario.Plan);
        var primitivePoints=scenario.Plan.Points.Keys.GroupBy(scenario.Plan.IndexOf).ToDictionary(g=>g.Key,g=>g.First());
        QuoteFactor Factor(ConnectedPrimitive p)
        {
            var source=p.Source;return new(source.Input,source.Quote.Key,p.Unit,source.Request.Contract,source.Observation.Key,
                source.Observation.Source,source.Observation.Role,scenario.Price(primitivePoints[p.Node],cancellation).ToUnitPriceExact().Value,
                [source.Request.Curve],source.Quote.TradingTick);
        }
        var consumed=new HashSet<int>();var pending=new Stack<int>(Outputs.Select(o=>scenario.Plan.IndexOf(o.Point)));
        while(pending.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested();if(!consumed.Add(node))continue;
            foreach(var dependency in scenario.Plan.DependencyPlan.Dependencies(node))pending.Push(dependency);
        }
        var inputs=SamplePoints.Where(s=>scenario.Plan.Points.ContainsKey(s.Point)&&consumed.Contains(scenario.Plan.IndexOf(s.Point))).Select(s=>
        {
            var closure=evidence.Resolve([s.Point],cancellation);var factors=closure.Primitives.Where(p=>p.Source.Input.Use==InputUse.Variable).Select(Factor).ToImmutableArray();
            return new RecipeSample(s.Sample.Date,s.Sample.Snapshot.ValuationAt,s.Sample.Contract,scenario.Price(s.Point,cancellation).ToUnitPriceExact(),
                closure.Lineage.OrderBy(o=>o.Id).ToImmutableArray(),factors,factors.IsEmpty);
        }).ToImmutableArray();
        var live=evidence.Resolve(Outputs.Select(o=>o.Point).ToImmutableArray(),cancellation).Primitives.Where(p=>p.Source.Input.Use==InputUse.Variable)
            .Select(Factor).OrderBy(f=>f.Input.Series.Id).ThenBy(f=>f.Input.Use).ToImmutableArray();
        return new(points,inputs,live);
    }
}

public static partial class RecipeEngine
{
    public static ConnectedRecipeGraph Connect(RecipeDefinition definition,RecipeInputs inputs,ConnectedRecipeIdentity identity,
        ImmutableArray<ConnectedRecipeSample> samples,ResolvedCalculationContext context,CancellationToken cancellation=default,
        ConnectedRecipeReferencePins? references=null,IReadOnlyDictionary<string,ConnectedRecipeGraph>? publishedSources=null,RecipeOutputDemand? demand=null)
    {
        cancellation.ThrowIfCancellationRequested();
        if(inputs is null||context is null||samples.IsDefault||inputs.ValuationAt!=context.ValuationAt)
            throw Missing("Supply the same resolved valuation context and explicit source snapshot bindings.");
        Validate(definition);
        if(demand is not null&&(demand.Deliveries is null||demand.Deliveries.IsEmpty||demand.Deliveries.Any(d=>d is null||d.From>=d.To)||
            demand.SamplingInstants is null||demand.SamplingInstants.Any(s=>s.Value.Offset!=TimeSpan.Zero||DateOnly.FromDateTime(s.Value.UtcDateTime)!=s.Key)))
            throw Missing("Output demand requires explicit nonempty delivery intervals and coherent UTC sampling instants.");
        publishedSources??=ImmutableDictionary<string,ConnectedRecipeGraph>.Empty;
        if(samples.Any(s=>s is null)||samples.Select(s=>(s.Source,s.Date,s.Contract)).Distinct().Count()!=samples.Length)
            throw Missing("Each directly declared source sample must be supplied once.");
        if(!definition.Nodes.Where(n=>n.Kind=="published_source").Select(n=>n.Id).ToHashSet().SetEquals(publishedSources.Keys)||
            publishedSources.Any(p=>p.Value is null||p.Value.Context!=context||p.Value.PreparedPanel is null||
                definition.Nodes.Single(n=>n.Id==p.Key).PublishedSource!=new PublishedRecipeReference(p.Value.Identity.CatalogId,p.Value.Identity.CurveId,p.Value.Identity.Version,p.Value.Declaration.Definition.Output)))
            throw Missing("Every published source requires its exact graph under the same resolved context.");
        if(publishedSources.Values.SelectMany(g=>g.Definitions).SelectMany(d=>d.Points).Any(p=>
            p.Expression is ConnectedRecipeProjection projection&&projection.Declaration.Identity==identity))
            throw Invalid("Published recipe dependencies must be acyclic.");
        var publishedDepth=publishedSources.Count==0?0:publishedSources.Values.Max(g=>g.PublishedDepth)+1;
        if(publishedDepth>32)throw Missing("A recipe supports at most 32 published-source edges in any dependency path.");
        if(publishedSources.Values.SelectMany(g=>g.Definitions).SelectMany(d=>d.Points)
            .Where(p=>p.Expression is ConnectedRecipeProjection).Select(p=>((ConnectedRecipeProjection)p.Expression).Declaration.Identity).Distinct().Count()>=256)
            throw Missing("A calculation supports at most 256 exact published recipe versions.");
        var declaration=new ConnectedRecipeDeclaration(identity,definition,
            inputs.Sources.Select(s=>new ConnectedRecipeSourceReference(s.Key,s.Value.Key)).ToImmutableArray(),
            inputs.Dates.Select(d=>new ConnectedRecipeDateBinding(d.Key,d.Value)).ToImmutableArray(),references??new([],[]),
            publishedSources.Select(p=>new ConnectedPublishedRecipeReference(p.Key,definition.Nodes.Single(n=>n.Id==p.Key).PublishedSource!,p.Value.Curve,p.Value.Declaration.ManifestId)).ToImmutableArray());
        declaration.Validate(context);
        var retained=new Dictionary<SampleKey,ConnectedRecipeSample>();
        var inherited=publishedSources.Values.SelectMany(g=>g.SamplePoints).DistinctBy(p=>p.Point).ToImmutableArray();
        foreach(var sample in samples)
        {
            if(sample is null||sample.Model is null||sample.Snapshot is null||sample.Source.Revision!=sample.Contract.Revision||
                sample.Snapshot.Definitions!=sample.Source.Revision||sample.Date!=DateOnly.FromDateTime(sample.Snapshot.ValuationAt.UtcDateTime)||
                sample.FreezeHistorical!=(sample.Snapshot.ValuationAt<inputs.ValuationAt))
                throw Missing("Each exact source/date/contract requires one coherent snapshot and historical-use policy.");
            var key=new SampleKey(sample.Source,sample.Date,sample.Contract);
            retained.Add(key,sample);
        }
        if(samples.Any(s=>!inputs.Sources.Values.Any(v=>v.Key==s.Source)))throw Missing("A sample belongs to an undeclared source.");
        var panel=Prepare(definition,inputs,samples,cancellation,publishedSources.ToDictionary(p=>p.Key,p=>new PanelSource(p.Value.PreparedPanel,p.Value.OutputPoint)),demand);
        var selected=panel.Cells.Select((cell,index)=>(Cell:cell,Index:index))
            .Where(c=>demand is null||demand.Deliveries.Contains(new(c.Cell.DeliveryFrom,c.Cell.DeliveryTo))).ToArray();
        var localKeys=new HashSet<SampleKey>();var visited=new HashSet<CellFormula>(ReferenceEqualityComparer.Instance);
        var pending=new Stack<CellFormula>(selected.Select(c=>c.Cell.Formula));
        while(pending.TryPop(out var formula))
        {
            cancellation.ThrowIfCancellationRequested();if(!visited.Add(formula)||formula.PublishedPoint is not null)continue;
            if(formula.Sample is {} key)localKeys.Add(key);
            foreach(var child in formula.Inputs)pending.Push(child);
        }
        var local=localKeys.OrderBy(k=>k.Source.Code,StringComparer.Ordinal).ThenBy(k=>k.Date).ThenBy(k=>k.Contract.Id).ToArray();
        if(demand is not null)
        foreach(var key in local)
        {
            var contract=inputs.Sources.Values.First(s=>s.Key==key.Source).Contracts.Single(c=>c.Contract.Key==key.Contract);
            if(!retained.TryGetValue(key,out var sample)||!demand.SamplingInstants.TryGetValue(key.Date,out var at)||
                sample.Snapshot.ValuationAt!=at||contract.EligibleFrom>at||contract.Contract.Expiry<=at)
                throw Missing("Every consumed forecast requires its exact live eligible sample; actuals cannot supply forecasts.");
        }
        var exports=new List<ConnectedCurveExport>();var samplePoints=new Dictionary<SampleKey,CurvePointKey>();
        foreach(var group in local.Select(k=>(Key:k,Binding:retained[k])).GroupBy(s=>(s.Binding.Model,s.Binding.Snapshot,s.Binding.FreezeHistorical)))
        {
            cancellation.ThrowIfCancellationRequested();
            var requests=group.Select(s=>new CurveRequest(s.Key.Source,s.Key.Contract)).Distinct().ToImmutableArray();
            var export=group.Key.Model.Compile(group.Key.Snapshot,requests,group.Key.FreezeHistorical,context,cancellation);exports.Add(export);
            var exportedPoints=export.Definitions.SelectMany(d=>d.Points).ToDictionary(p=>p.Key);
            foreach(var item in group)
            {
                var request=new CurveRequest(item.Key.Source,item.Key.Contract);var point=export.Points[(request,InputUse.Variable)];
                var sources=inputs.Sources.Values.Where(v=>v.Key==item.Key.Source).ToArray();
                if(sources.Any(v=>v.Unit!=exportedPoints[point].Unit||v.Contracts.Single(c=>c.Contract.Key==item.Key.Contract).Contract!=exportedPoints[point].Contract))
                    throw Missing("Recipe source metadata must match its exact compiled curve and contract.");
                samplePoints.Add(item.Key,point);
            }
        }
        var curve=declaration.Curve;
        var internalCurve=new CurveKey(new(identity.CatalogId,ConnectedIdentity.Id(new{Version="saved_recipe_internal_curve_v2",identity})),definition.Code);
        var created=new Dictionary<(string,int),CurvePointKey>();var points=new List<ConnectedCurvePoint>();
        var imported=ConnectedCurves.MergeDefinitions(exports.SelectMany(e=>e.Definitions).Concat(publishedSources.Values.SelectMany(g=>g.Definitions)));
        var directory=imported.SelectMany(d=>d.Points).ToDictionary(p=>p.Key);
        CurvePointKey CompileFormula(CellFormula formula)
        {
            if(formula.PublishedPoint is {} published)return published;
            cancellation.ThrowIfCancellationRequested();if(formula.Sample is {} sample)return samplePoints[sample];
            if(created.TryGetValue((formula.Node,formula.Index),out var prior))return prior;
            var dependencies=formula.Inputs.Select(CompileFormula).ToImmutableArray();
            var key=new CurvePointKey(internalCurve,ConnectedIdentity.Id(new{Version="saved_recipe_cell_v2",identity,formula.Node,formula.Index}));
            var members=dependencies.Select(p=>directory[p]).ToArray();
            var terms=dependencies.Select((point,i)=>(point,weight:formula.Weights[i])).GroupBy(t=>t.point)
                .Select(g=>new ConnectedWeightedTerm(CurvePointReference.Exact(g.Key),g.Aggregate(default(ExactDecimal),(sum,t)=>sum+t.weight))).ToImmutableArray();
            ConnectedCurveExpression expression=formula.Aggregation==WeightedMean?new ConnectedWeightedMean(terms):new ConnectedWeightedSum(terms);
            var calculated=new ConnectedCurvePoint(key,members.Min(c=>c.DeliveryFrom),members.Max(c=>c.DeliveryTo),members[0].Unit,expression);
            points.Add(calculated);directory.Add(key,calculated);
            created.Add((formula.Node,formula.Index),key);return key;
        }
        var outputs=ImmutableArray.CreateBuilder<ConnectedRecipeOutput>();
        foreach(var (cell,i) in selected)
        {
            var source=CompileFormula(cell.Formula);
            var key=new CurvePointKey(curve,ConnectedIdentity.Id(new{Version="saved_recipe_output_v2",identity,definition.Output,Index=i}));
            points.Add(new(key,cell.DeliveryFrom,cell.DeliveryTo,cell.Unit,new ConnectedRecipeProjection(CurvePointReference.Exact(source),declaration,cell.Position)));
            outputs.Add(new(key,cell.Position,cell.Dates,cell.Inputs.Select(s=>s.Contract).Distinct().ToImmutableArray(),cell.Inputs.Length));
        }
        var definitions=ConnectedCurves.MergeDefinitions(imported.Concat(points.GroupBy(p=>p.Key.Curve).Select(g=>new ConnectedCurveDefinition(g.Key,g.ToImmutableArray()))));
        return new(){Curve=curve,Identity=identity,Context=context,Declaration=declaration,Definitions=definitions,Outputs=outputs.ToImmutable(),
            SamplePoints=local.Select(key=>(Sample:retained[key],Point:samplePoints[key]))
                .Concat(inherited).DistinctBy(p=>p.Point).ToImmutableArray(),PreparedPanel=panel,
            SourceCompilations=exports.Count,DeclaredSourceNodes=exports.Sum(e=>e.Definitions.Sum(d=>d.Points.Length)),PublishedDepth=publishedDepth};
    }
    public static ImmutableArray<DateOnly> PriorBusinessDates(DateSet calendar,DateOnly contextDate,int count,bool includeContext)
    {
        var last=includeContext?contextDate:contextDate.AddDays(-1);
        if(calendar is null||count is <1 or >366||last<calendar.From||last>=calendar.To)
            throw Missing("The complete business-date window requires explicit count and retained calendar coverage.");
        var result=calendar.Dates.Where(d=>d<=last).TakeLast(count).ToImmutableArray();
        if(result.Length!=count)throw Missing("The complete business-date window falls outside retained calendar coverage.");
        return result;
    }
    public static ImmutableArray<DateOnly> ScheduleDates(ImmutableArray<MonthlyOccurrence> occurrences,string selection,
        DateOnly contextDate,int first,int count,out int origin)
    {
        if(occurrences.IsDefaultOrEmpty||occurrences.Length>120||occurrences.Any(o=>o is null||o.Month.Day!=1||o.Unadjusted.Year!=o.Month.Year||o.Unadjusted.Month!=o.Month.Month)||
            occurrences.Zip(occurrences.Skip(1)).Any(p=>p.First.Month>=p.Second.Month)||first<0||count is <1 or >366)
            throw Missing("Retain a complete ordered monthly occurrence sequence and an explicit bounded selection.");
        origin=selection switch{"absolute_index"=>0,"context_month"=>Array.FindIndex(occurrences.ToArray(),o=>o.Month.Year==contextDate.Year&&o.Month.Month==contextDate.Month),_=>-1};
        if(origin<0||(long)origin+first+count>occurrences.Length)
            throw Missing("The selected occurrence range is outside the exact published schedule.");
        return occurrences.Skip(origin+first).Take(count).Select(o=>o.Date).ToImmutableArray();
    }
}
