using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.Api;

public sealed record ContractPublicationPriceRow(string NodeId,string CurveCode,Guid TargetContractId,Guid SourceContractId,
    DateOnly DeliveryFrom,DateOnly DeliveryTo,DateTimeOffset Expiry,string State,Guid PublicationId,int PublicationVersion,
    string Index,Guid? OccurrenceId,DateTimeOffset? ExpectedPublicationAt,DateTimeOffset? PublishedAt,Guid? ReleaseId,string? FixingPrice,
    bool SelectedAsDependency,string? Price,string? PricingError);
public sealed record ContractPublicationPriceStatus(Guid SnapshotId,Guid CurveId,int Version,ImmutableArray<ContractPublicationPriceRow> Rows);

public static partial class CustomCurves
{
    static void MapContractPublicationStatus(this RouteGroupBuilder api,PreviewDatabase database,CustomCurveOperation operation)
    {
        api.MapGet("/contract-price-terms/status",(HttpContext c,Guid snapshotId,Guid curveId,int version)=>operation(c,"ReadMarketData",snapshotId,async(connection,pin,ct)=>
        {
            var saved=(await new CustomCurvesStore(database).Read(connection,ct)).SingleOrDefault(v=>v.Id==curveId&&v.Version==version)
                ??throw Invalid("Select an exact visible authored curve version.");
            pin=await SelectedClose.Pin(connection,database,pin,ct);
            var revision=await CurveVersionInputs.Resolve(database,connection,saved.MarketRevisionId,ct);
            var frame=pin.RequireFrame();var knownAt=SchedulePreview.ParseAt(pin.Context!.KnownAt);
            var data=await database.Read(connection,ct,selectedRevision:revision,observationIds:[],includeCalendarDays:false);
            var publications=await new PricePublicationStore(database).Read(connection,ct);
            var rows=ImmutableArray.CreateBuilder<ContractPublicationPriceRow>();
            foreach(var node in saved.Definition.Nodes.Where(n=>n.Kind=="source"&&n.ContractTerms is not null))
            {
                var source=saved.Sources[node.Id];
                if(source.CatalogId!=data.DefinitionRevision.CatalogId||source.RevisionId!=data.DefinitionRevision.RevisionId)
                    throw Invalid("Contract publication status requires the selected exact source revision.");
                var members=ContractPriceTermsStore.Members(data,source.Code);var terms=node.ContractTerms!;
                var maps=terms.Mappings.Where(m=>terms.For(m.Target).Settlement==ContractPriceTerms.PublishedRelease).ToArray();
                var bindings=new Dictionary<CurveRequest,ContractPriceTermsBinding>();
                foreach(var map in maps)
                {
                    var binding=ContractPriceTermsStore.Binding(data,source.Code,terms.For(map.Target),members.Single(k=>k.Key==map.Source),publications);
                    var request=new CurveRequest(new(data.DefinitionRevision,source.Code),map.Source);
                    if(bindings.TryGetValue(request,out var previous)&&previous!=binding)throw Invalid("A source contract has conflicting settlement terms.");
                    bindings[request]=binding;
                }
                bindings=await new ContractPriceTermsStore(database).PublicationPrices(connection,data,bindings,frame.At,knownAt,ct);
                var states=bindings.ToDictionary(p=>p.Key,p=>p.Value.PublicationPrices is null?null:
                    PublicationPriceResolution.Inspect(members.Single(k=>k.Key==p.Key.Contract),p.Value,frame.At,frame.At,knownAt));
                var ready=states.Where(p=>p.Value?.State==PublicationPriceStatus.Projected||p.Value?.Final is not null)
                    .Select(p=>p.Key).ToImmutableArray();
                var values=new Dictionary<CurveRequest,string>();var errors=new Dictionary<CurveRequest,string>();
                if(!ready.IsEmpty)
                {
                    // Inspect each requested contract independently, loading its
                    // authorized close and publication pins in one bounded read.
                    // One unavailable source must not hide another row's price.
                    var pins=frame.ObservationIds.Concat(frame.FrozenObservationIds.IsDefault?[]:frame.FrozenObservationIds)
                        .Concat(states.Values.Where(s=>s?.Final is not null).Select(s=>s!.Final!.Observation.Id)).Distinct().ToImmutableArray();
                    var selected=await database.ReadObservationWindows(connection,data,[],ct,pins);
                    var context=CalculationContext(frame,data.DefinitionRevision,pin.Context);
                    foreach(var request in ready)
                    try
                    {
                        var pricingTerms=new Dictionary<CurveRequest,ContractPriceTermsBinding>{{request,bindings[request]}};
                        var snapshot=await ReadSourceSnapshot(connection,database,selected,frame,[request],true,true,ct,priceTerms:pricingTerms);
                        var graph=data.Model.CompileContractTerms(snapshot,[request],pricingTerms,false,context,ct);
                        var point=graph.Points[(request,InputUse.Variable)];
                        var plan=ConnectedCurves.Compile(graph.Definitions,[point],context,
                            new PreparedBindings(graph.Definitions.Select(d=>d.Key).ToHashSet(),context),ct);
                        values.Add(request,plan.Evaluate(point,ct).ToExactString());
                    }
                    catch(MarketDataException error){errors.Add(request,error.Message);}
                }
                foreach(var map in maps)
                {
                    var target=members.Single(k=>k.Key==map.Target);var contract=members.Single(k=>k.Key==map.Source);
                    var binding=bindings[new(new(data.DefinitionRevision,source.Code),map.Source)];var prices=binding.PublicationPrices;
                    var request=new CurveRequest(new(data.DefinitionRevision,source.Code),map.Source);var status=states[request];
                    var final=status?.Final;
                    rows.Add(new(node.Id,source.Code,target.Key.Id,contract.Key.Id,target.DeliveryFrom,target.DeliveryTo,contract.Expiry,
                        status?.State??"publication_schedule_missing",binding.Publication!.Key.Id,binding.Publication.Key.Version,binding.Fixing!.Index.Code,
                        prices?.OccurrenceId,prices?.Occurrence.ExpectedReleaseAt,final?.Release.PublishedAt,final?.Release.Id,final?.Release.Price.Value.ToString(),
                        final is not null&&values.ContainsKey(request),values.GetValueOrDefault(request),
                        status is null?"An exact publication occurrence is required.":status.State==PublicationPriceStatus.Awaiting?"Awaiting the required final publication; no estimate is permitted.":
                        errors.GetValueOrDefault(request)));
                }
            }
            return Results.Ok(new ContractPublicationPriceStatus(snapshotId,curveId,version,[..rows.OrderBy(r=>r.NodeId,StringComparer.Ordinal).ThenBy(r=>r.DeliveryFrom).ThenBy(r=>r.TargetContractId)]));
        }));
    }
}
