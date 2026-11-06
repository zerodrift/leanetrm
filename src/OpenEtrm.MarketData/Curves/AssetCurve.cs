using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Curves;

public abstract record AssetQuery;
public sealed record AssetContract(ContractKey Contract):AssetQuery;
public sealed record AssetDeliveryDate(DateOnly Date):AssetQuery;
public sealed record AssetDeliveryPeriod(DateOnly From,DateOnly To):AssetQuery;
public sealed record AssetPointDate(DateOnly Date):AssetQuery;
public sealed record AssetCurveMember(CurvePointKey Point,ContractKey? Contract,DateOnly From,DateOnly To,DateOnly? SampleDate=null);
public sealed record AssetCurveValue(ExactUnitPrice Value,string ScenarioId,ResolvedCalculationContext Context,
    ImmutableArray<PricePointInput> Inputs,ImmutableHashSet<ObservationKey> Observations);

public sealed record AssetCurvePolicy(CurveLookupSettings Settings):CurveEvaluationPolicy(Settings.Implementation,ConnectedCurves.NumericalPolicy);
public sealed record AssetCurveSelection(AssetQuery Query,ImmutableArray<PricePointInput> Inputs);
public sealed record PreparedAssetSelection(CurvePointKey Root,ImmutableArray<PricePointInput> Inputs);

public sealed class AssetCurve:CurveBase<AssetQuery,AssetCurveValue,AssetCurveSelection>
{
    private readonly ImmutableArray<ConnectedCurveDefinition> definitions;
    private readonly ImmutableArray<AssetCurveMember> members;
    private readonly CurveLookupGrid<CurvePointKey> grid;
    public ResolvedCalculationContext Context{get;}
    public string Policy=>CurveLookupSettings.Version;
    public override CurveEvaluationPolicy EvaluationPolicy=>new AssetCurvePolicy(grid.Settings);
    public override ImmutableArray<CurveDomain> Coverage=>grid.Coverage;
    public AssetCurve(CurveKey key,ResolvedCalculationContext context,ImmutableArray<ConnectedCurveDefinition> definitions,
        ImmutableArray<AssetCurveMember> members,CurveLookupSettings settings):base(key)
    {
        if(context is null||definitions.IsDefaultOrEmpty||members.IsDefaultOrEmpty||members.Any(m=>m is null))
            throw new MarketDataException("curve_definition","Supply a resolved context, compiled sources and explicit members.");
        Context=context;this.definitions=ConnectedCurves.MergeDefinitions(definitions);this.members=members;
        var directory=this.definitions.SelectMany(d=>d.Points).ToDictionary(p=>p.Key);
        foreach(var m in members)
            if(!directory.TryGetValue(m.Point,out var p)||p.DeliveryFrom!=m.From||p.DeliveryTo!=m.To||p.Contract?.Key!=m.Contract)
                throw new MarketDataException("curve_definition","Lookup metadata must equal its declared graph point and contract.");
        if(members.Select(m=>directory[m.Point].Unit).Distinct().Count()!=1)throw new MarketDataException("unit_mismatch","Curve members require identical price units.");
        grid=new([..members.Select(m=>new CurveLookupMember<CurvePointKey>(m.Point,m.From,m.To,m.SampleDate))],settings);
    }
    public static AssetCurve FromNative(CurveModel model,MarketSnapshot snapshot,CurveKey curve,CurveLookupSettings settings)
    {
        if(snapshot.Knowledge is not {} knowledge)
            throw new MarketDataException("curve_context","A native curve query requires its explicitly resolved knowledge cutoff and context identity.");
        var terms=model.AssetDefinition(curve);
        var context=new ResolvedCalculationContext(knowledge.ContextId,"asset_curve_query_v1",snapshot.ValuationAt,knowledge.KnownAt,snapshot.ValuationAt,snapshot.MarketCutoff);
        var export=model.Compile(snapshot,[..terms.Constituents.Select(c=>new CurveRequest(curve,c))],false,context);
        var points=export.Definitions.SelectMany(d=>d.Points).ToDictionary(p=>p.Key);
        return new(curve,context,export.Definitions,[..terms.Constituents.Select(c=>
        {var point=points[export.Points[(new(curve,c),InputUse.Variable)]];return new AssetCurveMember(point.Key,c,point.DeliveryFrom,point.DeliveryTo);})],settings);
    }
    public static AssetCurve FromRecipe(ConnectedRecipeGraph graph,CurveLookupSettings settings)
    {
        var points=graph.Definitions.SelectMany(d=>d.Points).ToDictionary(p=>p.Key);
        return new(graph.Curve,graph.Context,graph.Definitions,[..graph.Outputs.Select(o=>
        {var p=points[o.Point];return new AssetCurveMember(p.Key,p.Contract?.Key,p.DeliveryFrom,p.DeliveryTo);})],settings);
    }
    private ImmutableArray<PricePointInput> ResolveInputs(AssetQuery request)
    {
        if(request is AssetContract exact)
        {
            var selected=members.Where(m=>m.Contract==exact.Contract).Take(2).ToArray();
            return selected.Length==1?[new(selected[0].Point,ExactDecimal.Parse("1"))]:throw new MarketDataException("request_member","The exact contract is absent or ambiguous.");
        }
        var weights=request switch{AssetDeliveryDate d=>grid.Select(d.Date,true),AssetDeliveryPeriod p=>grid.SelectInterval(p.From,p.To),
            AssetPointDate d=>grid.Select(d.Date,false),_=>throw new MarketDataException("curve_query","Select a supported typed asset query.")};
        return [..weights.Select(w=>new PricePointInput(w.Id,w.Weight))];
    }
    protected override AssetCurveSelection ResolveQuery(AssetQuery request)=>new(request,ResolveInputs(request));
    protected override ImmutableArray<CurveInputReference> RequiredInputs(AssetCurveSelection selection)=>[..selection.Inputs];
    protected override AssetCurveValue EvaluateResolved(AssetCurveSelection selection)=>PrepareResolved([selection]).Lookup(selection.Query).Value;
    public AssetCurveBatch Prepare(ImmutableArray<AssetQuery> requests)
    {
        if(requests.IsDefaultOrEmpty||requests.Length>366)throw new MarketDataException("curve_batch","Supply one to 366 typed queries.");
        var unique=requests.Distinct().ToDictionary(request=>request,ResolveQuery);
        return PrepareResolved([..requests.Select(request=>unique[request])]);
    }
    private AssetCurveBatch PrepareResolved(ImmutableArray<AssetCurveSelection> selections)
    {
        var requests=selections.Select(selection=>selection.Query).ToImmutableArray();
        var directory=definitions.SelectMany(d=>d.Points).ToDictionary(p=>p.Key);
        var queryCurve=new CurveKey(new(Key.Revision.CatalogId,ConnectedIdentity.Id(new{Version=Policy,Key,grid.Settings})),Key.Code);
        var created=new List<ConnectedCurvePoint>();var selected=new Dictionary<AssetQuery,(CurvePointKey Root,ImmutableArray<PricePointInput> Inputs)>();
        foreach(var selection in selections.DistinctBy(selection=>selection.Query))
        {
            var query=selection.Query;var inputs=selection.Inputs;var root=inputs[0].Point;
            if(inputs.Length>1)
            {
                var date=((AssetPointDate)query).Date;
                root=new(queryCurve,ConnectedIdentity.Id(new{Version=Policy,Date=date,grid.Settings}));
                created.Add(new(root,default,default,directory[inputs[0].Point].Unit,
                    new ConnectedWeightedMean([..inputs.Select(i=>new ConnectedWeightedTerm(CurvePointReference.Exact(i.Point),i.Weight))])){PointDate=date});
            }
            selected.Add(query,(root,inputs));
        }
        var all=ConnectedCurves.MergeDefinitions(definitions.Concat(created.Count==0?[]:[new ConnectedCurveDefinition(queryCurve,[..created])]));
        var plan=ConnectedCurves.Compile(all,[..selected.Values.Select(v=>v.Root).Distinct()],Context,new Bindings(all));
        return new(Key,EvaluationPolicy,plan,requests,[..requests.Select(q=>new PreparedAssetSelection(selected[q].Root,selected[q].Inputs))]);
    }
    private sealed class Bindings(ImmutableArray<ConnectedCurveDefinition> definitions):ICurveBindingResolver
    {
        private readonly HashSet<CurveKey> keys=definitions.Select(d=>d.Key).ToHashSet();
        public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new MarketDataException("curve_binding","Query preparation requires exact source definitions.");
        public void RequireVisible(CurveKey key,ResolvedCalculationContext context)
        {if(!keys.Contains(key))throw new MarketDataException("curve_binding","An undeclared curve entered query preparation.");}
    }
}

// Prepared lookups bind one exact scenario. Child scenarios reuse the plan and
// selection; the normal Lookup method reads their shocked values directly.
public sealed class AssetCurveBatch:CurveBase<AssetQuery,AssetCurveValue,PreparedAssetSelection>
{
    private readonly ImmutableArray<PreparedAssetSelection> selected;
    private readonly ConnectedScenario scenario;
    private readonly ConnectedInputEvidence evidence;
    public ConnectedScenario Base{get;}
    public ImmutableArray<AssetQuery> Requests{get;}
    public override CurveEvaluationPolicy EvaluationPolicy{get;}
    public override ImmutableArray<CurveDomain> Coverage=>[..Requests.Select((query,ordinal)=>query switch
    {
        AssetPointDate point=>new CurveDomain(point.Date,point.Date,true),
        AssetDeliveryDate day=>new CurveDomain(day.Date,day.Date,true),
        AssetDeliveryPeriod period=>new CurveDomain(period.From,period.To,false),
        _=>new CurveDomain(Base.Plan.Points[selected[ordinal].Root].DeliveryFrom,Base.Plan.Points[selected[ordinal].Root].DeliveryTo,false)
    }).Distinct()];
    internal AssetCurveBatch(CurveKey key,CurveEvaluationPolicy policy,ConnectedCurvePlan plan,ImmutableArray<AssetQuery> requests,ImmutableArray<PreparedAssetSelection> selected)
        :this(key,policy,ConnectedScenario.Base(plan),requests,selected){}
    private AssetCurveBatch(CurveKey key,CurveEvaluationPolicy policy,ConnectedScenario scenario,ImmutableArray<AssetQuery> requests,ImmutableArray<PreparedAssetSelection> selected):base(key)
    {Base=ConnectedScenario.Base(scenario.Plan);this.scenario=scenario;Requests=requests;this.selected=selected;EvaluationPolicy=policy;evidence=new(scenario.Plan);}
    public AssetCurveBatch WithScenario(ConnectedScenario scenario)
    {
        if(scenario is null||!ReferenceEquals(scenario.Plan,Base.Plan))throw new MarketDataException("curve_context","Use this batch's exact prepared scenario graph.");
        return new(Key,EvaluationPolicy,scenario,Requests,selected);
    }
    protected override PreparedAssetSelection ResolveQuery(AssetQuery query)
    {
        var ordinal=Requests.IndexOf(query);
        return ordinal>=0?selected[ordinal]:throw new MarketDataException("curve_query","The query is outside this prepared demand set.");
    }
    protected override ImmutableArray<CurveInputReference> RequiredInputs(PreparedAssetSelection selection)=>[..selection.Inputs];
    protected override AssetCurveValue EvaluateResolved(PreparedAssetSelection selection)=>new(scenario.Price(selection.Root),scenario.Id,
        scenario.Plan.Context,selection.Inputs,evidence.Resolve([selection.Root],default).Lineage);
}
