using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using System.Globalization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveTargetRequest(string Kind,CurveKey Curve,ImmutableArray<Guid>? Points=null,
    ContractKey? Contract=null,string? From=null,string? To=null,string? Convention=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveRiskAxisRequest(string Coordinate,CurveTargetRequest Target);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveBumpRequest(CurveRiskAxisRequest Factor,string Size,string Convention);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveShiftRequest(CurveRiskAxisRequest Factor,string Size,string Convention);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioRiskCatalogRequest(FuturesReadRequest Context,ImmutableArray<Guid> BookIds,ImmutableArray<Guid> PortfolioIds);

public sealed record PortfolioRiskAxis(string Id,DefinitionRevision Market,CurveRiskAxisRequest Request,
    CurveShockCoordinate Coordinate,CurveShockTarget Target,PriceUnit Unit,string Label,
    ImmutableArray<CurvePointKey> Points,QuoteFactor? Quote);
public sealed record PortfolioCurveBump(PortfolioRiskAxis Axis,ExactDecimal Size);
public sealed record PortfolioCurveShift(PortfolioRiskAxis Axis,ExactDecimal Size);
public sealed record RetainedRiskAxis(string Id,Guid MarketRevisionId,CurveRiskAxisRequest Factor,ImmutableArray<CurvePointKey> Points);
// The scenario targets of what the held instruments are priced from, as the catalog offers them and in its order: a whole contract
// for each held price, and a whole curve for each curve one is on. A held price or curve no such target covers is named by its label.
public sealed record HeldRiskTargets(ImmutableArray<string> Contracts,ImmutableArray<string> Curves,
    ImmutableArray<string> ContractsWithoutTarget,ImmutableArray<string> CurvesWithoutTarget);

// The transport names an exact coordinate. Selection, units and membership are
// resolved by the same managed plan that prices every instrument.
internal static class CurveRiskAxes
{
    // Metadata belongs to the exact prepared object. Weak ownership prevents an
    // index from extending a job's lifetime; prices remain in its graph session.
    private static readonly ConditionalWeakTable<PortfolioValuation.MarketPartition,MarketIndex> MarketIndexes=new();
    private static readonly ConditionalWeakTable<PortfolioValuation.Prepared,PreparedIndex> PreparedIndexes=new();
    private static MarketIndex Index(PortfolioValuation.MarketPartition market,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();return MarketIndexes.GetValue(market,value=>new(value,ct));}
    private static PreparedIndex Index(PortfolioValuation.Prepared prepared,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();return PreparedIndexes.GetValue(prepared,value=>new(value,ct));}
    private sealed class MarketIndex
    {
        internal ImmutableDictionary<CurveKey,ImmutableArray<ConnectedCurvePoint>> Curves{get;}
        internal ImmutableDictionary<InputKey,ConnectedCurvePoint> LiveSources{get;}
        internal ImmutableDictionary<InputKey,QuoteFactor> Quotes{get;}
        internal ImmutableDictionary<string,string> Names{get;}
        private readonly Dictionary<(CurveKey Curve,ContractKey Contract,CurveShockCoordinate Coordinate),bool> wholeContracts=new();
        internal MarketIndex(PortfolioValuation.MarketPartition market,CancellationToken ct)
        {
            var curves=ImmutableDictionary.CreateBuilder<CurveKey,ImmutableArray<ConnectedCurvePoint>>();
            foreach(var group in market.Connected.Scenario.Plan.Points.Values.GroupBy(p=>p.Key.Curve))
            {
                ct.ThrowIfCancellationRequested();
                var points=group.OrderBy(p=>p.DeliveryFrom).ThenBy(p=>p.DeliveryTo).ThenBy(p=>p.Key.PointId).ToImmutableArray();
                curves.Add(group.Key,points);
                foreach(var contract in points.Where(p=>p.Contract is not null).GroupBy(p=>p.Contract!.Key))
                foreach(var coordinate in new[]{CurveShockCoordinate.MarketQuote,CurveShockCoordinate.ComputedOutput})
                    wholeContracts.Add((group.Key,contract.Key,coordinate),contract.All(p=>Public(p,coordinate)));
            }
            Curves=curves.ToImmutable();
            LiveSources=Curves.OrderBy(c=>c.Key.Revision.CatalogId).ThenBy(c=>c.Key.Revision.RevisionId).ThenBy(c=>c.Key.Code,StringComparer.Ordinal)
                .SelectMany(c=>c.Value).Where(p=>p.Expression is ConnectedSource{Input.Use:InputUse.Variable})
                .GroupBy(p=>((ConnectedSource)p.Expression).Input).ToImmutableDictionary(g=>g.Key,g=>g.First());
            Quotes=market.Data.Model.QuoteFactors(market.Snapshot).ToImmutableDictionary(f=>f.Input);
            Names=market.Data.Definitions["curves"].ToImmutableDictionary(row=>row.Text("code"),row=>row.Text("name"),StringComparer.Ordinal);
            ct.ThrowIfCancellationRequested();
        }
        internal CurveShockTarget PointTarget(ConnectedCurvePoint point,CurveShockCoordinate coordinate)=>
            point.Contract is {} contract&&wholeContracts.GetValueOrDefault((point.Key.Curve,contract.Key,coordinate))?
                new ContractCurveTarget(point.Key.Curve,contract.Key):new PointCurveTarget(point.Key.Curve,[point.Key.PointId]);
    }
    private sealed class PreparedIndex
    {
        internal ImmutableDictionary<CurveKey,ImmutableArray<PortfolioValuation.MarketPartition>> Curves{get;}
        internal ImmutableDictionary<DefinitionRevision,PortfolioValuation.MarketPartition> Revisions{get;}
        internal PreparedIndex(PortfolioValuation.Prepared prepared,CancellationToken ct)
        {
            Curves=prepared.Markets.SelectMany(m=>Index(m,ct).Curves.Keys.Select(curve=>(Curve:curve,Market:m)))
                .GroupBy(p=>p.Curve).ToImmutableDictionary(g=>g.Key,g=>g.Select(p=>p.Market).ToImmutableArray());
            Revisions=prepared.Markets.ToImmutableDictionary(m=>m.Snapshot.Definitions);
        }
    }

    public static string Coordinate(CurveShockCoordinate coordinate)=>coordinate switch
    {
        CurveShockCoordinate.MarketQuote=>"market_quote",CurveShockCoordinate.ComputedOutput=>"computed_output",
        _=>throw Invalid("scenario_coordinate","Select a supported curve coordinate.")
    };
    private static CurveShockCoordinate Coordinate(string value)=>value switch
    {
        "market_quote"=>CurveShockCoordinate.MarketQuote,"computed_output"=>CurveShockCoordinate.ComputedOutput,
        _=>throw Invalid("scenario_coordinate","Select market quotes or computed curve outputs explicitly.")
    };
    public static CurveShockTarget Target(CurveTargetRequest request)
    {
        if(request is null||request.Curve.Revision.CatalogId==Guid.Empty||request.Curve.Revision.RevisionId==Guid.Empty||string.IsNullOrWhiteSpace(request.Curve.Code))
            throw Invalid("scenario_target","Select one exact curve and target kind.");
        var noDates=request.From is null&&request.To is null&&request.Convention is null;
        var noPoints=request.Points is null||request.Points.Value.IsDefaultOrEmpty;
        return request.Kind switch
        {
            "parallel" when noDates&&noPoints&&request.Contract is null=>new ParallelCurveTarget(request.Curve),
            "contract" when noDates&&noPoints&&request.Contract is {} contract=>new ContractCurveTarget(request.Curve,contract),
            "points" when noDates&&!noPoints&&request.Contract is null=>new PointCurveTarget(request.Curve,request.Points!.Value),
            "delivery_bucket" when noPoints&&request.Contract is null&&request.Convention==DeliveryBucketCurveTarget.WholeDelivery&&
                DateOnly.TryParseExact(request.From,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)&&
                DateOnly.TryParseExact(request.To,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var to)
                =>new DeliveryBucketCurveTarget(request.Curve,from,to,request.Convention),
            _=>throw Invalid("scenario_target","Supply only the fields of the explicitly selected curve target.")
        };
    }
    public static CurveTargetRequest Request(CurveShockTarget target)=>target switch
    {
        ParallelCurveTarget=>new("parallel",target.Curve),
        ContractCurveTarget contract=>new("contract",target.Curve,Contract:contract.Contract),
        PointCurveTarget points=>new("points",target.Curve,points.Points.Order().ToImmutableArray()),
        DeliveryBucketCurveTarget bucket=>new("delivery_bucket",target.Curve,From:bucket.From.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),To:bucket.To.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),Convention:bucket.Convention),
        _=>throw Invalid("scenario_target","The curve target is unsupported.")
    };
    public static bool Public(ConnectedCurvePoint point,CurveShockCoordinate coordinate)=>coordinate switch
    {
        CurveShockCoordinate.MarketQuote=>point.Expression is ConnectedSource{Input.Use:InputUse.Variable},
        CurveShockCoordinate.ComputedOutput=>point.Expression is ConnectedBuiltCurve or ConnectedRecipeProjection,
        _=>false
    };
    public static PortfolioRiskAxis Resolve(PortfolioValuation.Prepared prepared,CurveRiskAxisRequest request,CancellationToken ct)
    {
        if(request is null)throw Invalid("scenario_target","Select an explicit curve factor.");
        var coordinate=Coordinate(request.Coordinate);var target=Target(request.Target);
        var markets=Index(prepared,ct).Curves.GetValueOrDefault(target.Curve,[]);
        if(markets.Length!=1)throw Invalid("scenario_target","The exact curve must belong to one prepared financial market.");
        return Resolve(markets[0],coordinate,target,ct);
    }
    public static PortfolioRiskAxis Resolve(PortfolioValuation.MarketPartition market,CurveShockCoordinate coordinate,CurveShockTarget target,CancellationToken ct)
    {
        var selected=market.Connected.Scenario.ResolveTarget(coordinate,target,ct);
        if(selected.Any(p=>!Public(p,coordinate)))throw Invalid("scenario_target","Select a published curve output or live market quote.");
        var units=selected.Select(p=>p.Unit).Distinct().ToArray();
        if(units.Length!=1)throw Invalid("unit_mismatch","A shared curve shock requires one exact price unit.");
        var request=new CurveRiskAxisRequest(Coordinate(coordinate),Request(target));
        var id=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request,RiskJobs.Json)));
        var first=selected[0];var index=Index(market,ct);
        var name=Name(index,first);
        var positions=selected.Select(p=>p.Expression).OfType<ConnectedRecipeProjection>().Select(p=>p.Position).Distinct().Order().ToArray();
        var output=positions.Length switch
        {
            0=>"selected points",
            1=>"output position "+positions[0].ToString(CultureInfo.InvariantCulture),
            _=>"output positions "+string.Join(", ",positions.Select(p=>p.ToString(CultureInfo.InvariantCulture)))
        };
        var label=target switch
        {
            ContractCurveTarget=>FormattableString.Invariant($"{name} · {first.DeliveryFrom:yyyy-MM-dd} to {first.DeliveryTo:yyyy-MM-dd}"),
            PointCurveTarget=>FormattableString.Invariant($"{name} · {first.DeliveryFrom:yyyy-MM-dd} to {first.DeliveryTo:yyyy-MM-dd} · {output}"),
            DeliveryBucketCurveTarget bucket=>FormattableString.Invariant($"{name} · {bucket.From:yyyy-MM-dd} to {bucket.To:yyyy-MM-dd}"),
            _=>first.Expression is ConnectedRecipeProjection?$"{name} · all retained outputs":$"{name} · all retained contracts"
        };
        var sources=selected.Select(p=>p.Expression).OfType<ConnectedSource>().Select(s=>s.Input).Distinct().ToArray();
        var quote=sources.Length==1?index.Quotes.GetValueOrDefault(sources[0]):null;
        return new(id,market.Snapshot.Definitions,request,coordinate,target,units[0],label,
            selected.Select(p=>p.Key).ToImmutableArray(),quote);
    }
    public static ImmutableArray<PortfolioRiskAxis> Catalog(PortfolioValuation.Prepared prepared,CancellationToken ct)
    {
        var result=ImmutableArray.CreateBuilder<PortfolioRiskAxis>();
        foreach(var market in prepared.Markets)
        {
            var index=Index(market,ct);
            foreach(var coordinate in new[]{CurveShockCoordinate.MarketQuote,CurveShockCoordinate.ComputedOutput})
            foreach(var (curve,all) in index.Curves.OrderBy(g=>g.Key.Code,StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                // Broad targets retain their complete membership. Mixed frozen
                // and live contracts instead expose their exact live points.
                if(all.All(p=>Public(p,coordinate)))result.Add(Resolve(market,coordinate,new ParallelCurveTarget(curve),ct));
                var emittedContracts=new HashSet<ContractKey>();
                foreach(var point in all.Where(p=>Public(p,coordinate)))
                {
                    var target=index.PointTarget(point,coordinate);
                    if(target is ContractCurveTarget contract&&!emittedContracts.Add(contract.Contract))continue;
                    result.Add(Resolve(market,coordinate,target,ct));
                }
            }
        }
        return result.DistinctBy(a=>a.Id).ToImmutableArray();
    }
    // The name a point's targets are labeled with: its saved recipe and version for a custom curve's output, and otherwise its curve.
    private static string Name(MarketIndex index,ConnectedCurvePoint point)
    {
        var name=point.Expression is ConnectedRecipeProjection projection?
            FormattableString.Invariant($"{projection.Declaration.Definition.Name} · version {projection.Declaration.Identity.Version}"):
            index.Names.GetValueOrDefault(point.Key.Curve.Code);
        return string.IsNullOrWhiteSpace(name)?point.Key.Curve.Code:name;
    }
    // What the presets Shock every held contract and Parallel shift shock (blotter-desk.md, Risk report inline): for each point a
    // held instrument's declared dependency is priced from, the whole-contract target on the coordinate the point is published on,
    // and the whole-curve target of its curve on that coordinate. A held price that is no whole contract, such as a custom curve's
    // output, or that no coordinate publishes, such as a frozen final settlement, is named instead, and so is a curve whose prices a
    // whole-curve target does not all cover: neither is ever covered by a broader or a narrower target in its place.
    public static HeldRiskTargets Held(PortfolioValuation.Prepared prepared,ImmutableArray<PortfolioRiskAxis> catalog,CancellationToken ct)
    {
        var contracts=new HashSet<string>(StringComparer.Ordinal);var curves=new HashSet<string>(StringComparer.Ordinal);
        var contractGaps=new SortedSet<string>(StringComparer.Ordinal);var curveGaps=new SortedSet<string>(StringComparer.Ordinal);
        foreach(var market in prepared.Markets)
        {
            var index=Index(market,ct);var plan=market.Connected.Scenario.Plan;
            // Each curve's whole-curve target on a coordinate, or none where it does not cover every point of the curve: resolved once.
            var wholeCurves=new Dictionary<(CurveKey Curve,CurveShockCoordinate Coordinate),string?>();
            foreach(var key in market.Connected.DeclaredPoints)
            {
                ct.ThrowIfCancellationRequested();
                var point=plan.Points[key];
                CurveShockCoordinate? published=Public(point,CurveShockCoordinate.MarketQuote)?CurveShockCoordinate.MarketQuote:
                    Public(point,CurveShockCoordinate.ComputedOutput)?CurveShockCoordinate.ComputedOutput:null;
                if(published is not {} coordinate)
                {
                    contractGaps.Add(FormattableString.Invariant($"{Name(index,point)} · {point.DeliveryFrom:yyyy-MM-dd} to {point.DeliveryTo:yyyy-MM-dd}"));
                    curveGaps.Add(Name(index,point));
                    continue;
                }
                var target=index.PointTarget(point,coordinate);
                if(target is ContractCurveTarget)contracts.Add(Resolve(market,coordinate,target,ct).Id);
                else contractGaps.Add(Resolve(market,coordinate,target,ct).Label);
                if(!wholeCurves.TryGetValue((key.Curve,coordinate),out var whole))
                    wholeCurves[(key.Curve,coordinate)]=whole=index.Curves[key.Curve].All(p=>Public(p,coordinate))?
                        Resolve(market,coordinate,new ParallelCurveTarget(key.Curve),ct).Id:null;
                if(whole is not null)curves.Add(whole);
                else curveGaps.Add(Name(index,point));
            }
        }
        var offered=catalog.Select(a=>a.Id).ToImmutableArray();
        if(!contracts.Concat(curves).All(offered.Contains))
            throw Invalid("risk_contract","Every held target must be one the risk catalog offers.");
        return new([..offered.Where(contracts.Contains)],[..offered.Where(curves.Contains)],[..contractGaps],[..curveGaps]);
    }
    public static PortfolioRiskAxis Quote(PortfolioValuation.Prepared prepared,QuoteFactor factor,CancellationToken ct)
    {
        if(!Index(prepared,ct).Revisions.TryGetValue(factor.Input.Series.Revision,out var market))
            throw Invalid("risk_factor","The declared quote requires its exact prepared financial market.");
        var index=Index(market,ct);
        if(!index.LiveSources.TryGetValue(factor.Input,out var point))
            throw Invalid("risk_factor","A declared live market input is absent from the prepared graph.");
        return Resolve(market,CurveShockCoordinate.MarketQuote,index.PointTarget(point,CurveShockCoordinate.MarketQuote),ct);
    }
    public static ImmutableArray<RetainedRiskAxis> Retained(PortfolioRiskPreview.Prepared prepared)=>prepared.Bumps.Select(b=>b.Axis)
        .Concat(prepared.Stress.Select(s=>s.Axis)).DistinctBy(a=>a.Id).OrderBy(a=>a.Id,StringComparer.Ordinal)
        .Select(a=>new RetainedRiskAxis(a.Id,a.Market.RevisionId,a.Request,a.Points.OrderBy(p=>p.Curve.Revision.RevisionId)
            .ThenBy(p=>p.Curve.Code,StringComparer.Ordinal).ThenBy(p=>p.PointId).ToImmutableArray())).ToImmutableArray();
    private static MarketDataException Invalid(string code,string message)=>new(code,message);
}
