using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Curves;

public abstract record AssetQuery;
public sealed record AssetContract(ContractKey Contract):AssetQuery;
public sealed record AssetDeliveryDate(DateOnly Date):AssetQuery;
public sealed record AssetPointDate(DateOnly Date):AssetQuery;
public sealed record AssetCurveMember(CurvePointKey Point,ContractKey? Contract,DateOnly From,DateOnly To,DateOnly? SampleDate=null);
public sealed record AssetCurveValue(ExactUnitPrice Value,string ScenarioId,ResolvedCalculationContext Context,
    ImmutableArray<PricePointInput> Inputs,ImmutableHashSet<ObservationKey> Observations);

public sealed class AssetCurve:CurveBase<AssetQuery,AssetCurveValue>
{
    private readonly ImmutableArray<ConnectedCurveDefinition> definitions;
    private readonly ImmutableArray<AssetCurveMember> members;
    private readonly CurveLookupGrid<CurvePointKey> grid;
    public ResolvedCalculationContext Context{get;}
    public override string Policy=>CurveLookupSettings.Version;
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
        var terms=model.AssetDefinition(curve);
        var context=new ResolvedCalculationContext(snapshot.Id,"asset_curve_query_v1",snapshot.ValuationAt,snapshot.MarketCutoff,snapshot.ValuationAt,snapshot.MarketCutoff);
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
    private ImmutableArray<PricePointInput> Resolve(AssetQuery request)
    {
        if(request is AssetContract exact)
        {
            var selected=members.Where(m=>m.Contract==exact.Contract).Take(2).ToArray();
            return selected.Length==1?[new(selected[0].Point,ExactDecimal.Parse("1"))]:throw new MarketDataException("request_member","The exact contract is absent or ambiguous.");
        }
        var weights=request switch{AssetDeliveryDate d=>grid.Select(d.Date,true),AssetPointDate d=>grid.Select(d.Date,false),_=>throw new MarketDataException("curve_query","Select a supported typed asset query.")};
        return [..weights.Select(w=>new PricePointInput(w.Id,w.Weight))];
    }
    public override void ValidateQuery(AssetQuery request)=>_=Resolve(request);
    public override ImmutableArray<CurveInputReference> RequiredInputs(AssetQuery request)=>[..Resolve(request)];
    public override AssetCurveValue Evaluate(AssetQuery request)=>EvaluateMany([request])[0];
    public override ImmutableArray<AssetCurveValue> EvaluateMany(ImmutableArray<AssetQuery> requests)
    {var batch=Prepare(requests);return batch.Evaluate(batch.Base);}
    public AssetCurveBatch Prepare(ImmutableArray<AssetQuery> requests)
    {
        if(requests.IsDefaultOrEmpty||requests.Length>366)throw new MarketDataException("curve_batch","Supply one to 366 typed queries.");
        var directory=definitions.SelectMany(d=>d.Points).ToDictionary(p=>p.Key);
        var queryCurve=new CurveKey(new(Key.Revision.CatalogId,ConnectedIdentity.Id(new{Version=Policy,Key,grid.Settings})),Key.Code);
        var created=new List<ConnectedCurvePoint>();var selected=new Dictionary<AssetQuery,(CurvePointKey Root,ImmutableArray<PricePointInput> Inputs)>();
        foreach(var query in requests.Distinct())
        {
            var inputs=Resolve(query);var root=inputs[0].Point;
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
        return new(plan,requests,[..requests.Select(q=>selected[q])]);
    }
    private sealed class Bindings(ImmutableArray<ConnectedCurveDefinition> definitions):ICurveBindingResolver
    {
        private readonly HashSet<CurveKey> keys=definitions.Select(d=>d.Key).ToHashSet();
        public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new MarketDataException("curve_binding","Query preparation requires exact source definitions.");
        public void RequireVisible(CurveKey key,ResolvedCalculationContext context)
        {if(!keys.Contains(key))throw new MarketDataException("curve_binding","An undeclared curve entered query preparation.");}
    }
}

public sealed class AssetCurveBatch
{
    private readonly ImmutableArray<(CurvePointKey Root,ImmutableArray<PricePointInput> Inputs)> selected;
    public ConnectedScenario Base{get;}
    public ImmutableArray<AssetQuery> Requests{get;}
    internal AssetCurveBatch(ConnectedCurvePlan plan,ImmutableArray<AssetQuery> requests,ImmutableArray<(CurvePointKey,ImmutableArray<PricePointInput>)> selected)
    {Base=ConnectedScenario.Base(plan);Requests=requests;this.selected=selected;}
    public ImmutableArray<AssetCurveValue> Evaluate(ConnectedScenario scenario)
    {
        if(scenario is null||!ReferenceEquals(scenario.Plan,Base.Plan))throw new MarketDataException("curve_context","Use this batch's exact prepared scenario graph.");
        var evidence=new ConnectedInputEvidence(scenario.Plan);var values=new Dictionary<CurvePointKey,AssetCurveValue>();
        return [..selected.Select(s=>values.TryGetValue(s.Root,out var prior)?prior:values[s.Root]=new(scenario.Price(s.Root),scenario.Id,
            scenario.Plan.Context,s.Inputs,evidence.Resolve([s.Root],default).Lineage))];
    }
}
