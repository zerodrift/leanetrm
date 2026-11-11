using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    static async Task<CurveRead> PowerIntervalPreview(NpgsqlConnection connection,PreviewDatabase database,PinnedContext pin,
        CustomCurvePreviewRequest body,PreviewData terms,CancellationToken ct)
    {
        var context=pin.Context!;
        var frame=await MarketInputSelection.FrameFor(database,connection,terms,pin.RequireFrame(),ct);
        var data=await database.Read(connection,ct,ppaBundle:CatalogueSource.From(terms),observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins,includeCalendarDays:true);
        var definition=PowerIntervalCurves.Read(data,body.Definition,await PowerIntervalCurves.Products(connection,data,ct),
            body.Id is {} id?new(data.DefinitionRevision.CatalogId,id,body.Version!.Value,body.Definition.Output):null,await CustomCurvesStore.ReadVersions(connection,data.DefinitionRevision.CatalogId,ct));
        var from=definition.Curve.Grid.Clock.StartOfDay(definition.Terms.From).ToUniversalTime();
        var to=definition.Curve.Grid.Clock.StartOfDay(definition.Terms.To).ToUniversalTime();
        var read=await PowerIntervalCurves.PrepareRead(connection,data,definition,[(from,to)],frame.At,DateTimeOffset.Parse(context.KnownAt!,System.Globalization.CultureInfo.InvariantCulture),ct);
        var prepared=read.Prepared;
        ImmutableArray<CurveRequest> dependencies=[..prepared.Months.SelectMany(m=>prepared.Dependencies(m)).Distinct()];
        if(dependencies.Any(d=>!frame.CurveContracts.GetValueOrDefault(d.Curve.Code,[]).Contains(d.Contract.Id)))
            throw new MarketDataException("missing_curve_view","The selected frame must explicitly include every native source contract.");
        var built=data.Model.Build(data.Snapshot(frame).WithKnowledge(context.ContextId!.Value,DateTimeOffset.Parse(context.KnownAt!,System.Globalization.CultureInfo.InvariantCulture)),dependencies);
        var resolver=new IntervalPriceResolver(built,ImmutableDictionary<CurveKey,PreparedCurve>.Empty.Add(definition.Curve.Key,prepared));
        var prices=prepared.Months.SelectMany(m=>resolver.Prices(definition.Curve.Key,m)).ToArray();
        var format=new AmountFormat(6,CashTieRule.ToEven,true,true);
        var unit=data.PriceUnit(definition.Curve.Blocks[0].Curve.Code);
        var peaks=definition.Curve.Blocks.Where(b=>b.Membership is BlockMembership.Peak or BlockMembership.DailyPeak).Select(b=>b.Curve).ToHashSet();
        CurveRequest Source(IntervalPriceResolution price)=>price.ConstructionSource??price.Anchor?.Request??throw new MarketDataException("power_interval_source","Construction provenance is missing.");
        var compact=definition.Curve.PriceShape is null&&definition.Terms.Cutover is not null&&prices.All(p=>p.State==IntervalPriceState.Projected);
        var anchors=prices.GroupBy(Source).OrderBy(g=>data.ContractRows(g.Key.Contract.Id).Single().DeliveryFrom).ThenBy(g=>peaks.Contains(g.Key.Curve)?0:1).ToArray();
        var rows=compact?anchors.Select(g=>{var c=data.ContractRows(g.Key.Contract.Id).Single();return new[]{c.DeliveryFrom.ToString("yyyy-MM-dd"),c.DeliveryTo.ToString("yyyy-MM-dd"),g.First().Price!.Format(format),unit,
            peaks.Contains(g.Key.Curve)?"Peak":"Off-peak",g.Key.Curve.Code,c.DeliveryTo==c.DeliveryFrom.AddDays(1)?"Daily":"Monthly",g.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)};}).ToArray()
            :prices.Select(p=>{var c=data.ContractRows(Source(p).Contract.Id).Single();return new[]{p.Start.ToString("O"),p.End.ToString("O"),p.Price!.Format(format),unit,
                peaks.Contains(Source(p).Curve)?"Peak":"Off-peak",Source(p).Curve.Code,c.DeliveryFrom.ToString("yyyy-MM-dd"),c.DeliveryTo.ToString("yyyy-MM-dd")};}).ToArray();
        if(!compact&&definition.Settlement is not null)
            rows=rows.Select((row,i)=>row.Append(prices[i].State.ToString()).ToArray()).ToArray();
        var columns=compact?new[]{"Delivery from","Delivery to","Price","Unit","Product","Source","Tenor","Hours"}:new[]{"UTC start","UTC end","Price","Unit","Product","Source","Delivery from","Delivery to"};
        if(!compact&&definition.Settlement is not null)columns=[..columns,"Price state"];
        var value=new{id=body.Id,version=body.Version,context=pin.Context,snapshotId=frame.Id,at=frame.At,engineVersion=RecipeEngine.Version,
            outputType="power_intervals",summary=new{points=rows.Length,intervals=prices.Length,unavailablePoints=0,anchors=dependencies.Length,
                peakIntervals=prices.Count(p=>peaks.Contains(Source(p).Curve)),offPeakIntervals=prices.Count(p=>!peaks.Contains(Source(p).Curve)),unit,
                firstPeriod=new{from=compact?rows[0][0]:definition.Terms.From.ToString("yyyy-MM-dd"),to=compact?rows[0][1]:definition.Terms.To.ToString("yyyy-MM-dd")},
                lastPeriod=new{from=compact?rows[^1][0]:definition.Terms.From.ToString("yyyy-MM-dd"),to=compact?rows[^1][1]:definition.Terms.To.ToString("yyyy-MM-dd")}},
            columns,rows,
            chart=new{title=compact?"Daily then monthly power forwards":"Power forward intervals",axis="observation",axisName=compact?"Delivery date":"Delivery UTC",unit,
                series=compact?new[]{"Peak","Off-peak"}.Select(product=>new{name=product,points=rows.Where(r=>r[4]==product).Select(r=>new{x=r[0],y=r[2],label=r[0]+" · "+r[6]})})
                    :new[]{new{name="Price",points=rows.Select(r=>new{x=r[0],y=r[2],label=r[0]})}}},
            dependencies=built.Values.SelectMany(v=>v.Lineage).Distinct(),inputs=built.Values,
            trace=new{recipe=body.Definition,definitionRevision=data.DefinitionRevision,referenceRevision=data.ReferenceRevision,
                output=definition.Curve.Output,definition.Terms,definition.Clock,definition.SourceProfiles,
                implementation=RecipeEngine.PowerIntervalOutput(body.Definition)!.Parameters["implementation"],profileMapping="exact_native_grid_terms_v1",numericalPolicy=built.NumericalPolicy,
                readContexts=read.Contexts,settlement=(object?)definition.Settlement??"forward_only_no_actual_substitution",
                intervalEvidence=definition.Settlement is null?null:prices.Select(p=>new{p.Start,p.End,p.State,p.Actual,p.ConstructionSource,p.ForwardLineage})}};
        return new(value,rows.Length,[data.ReferenceRevision.CatalogId]);
    }
}
