using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractPriceMapInput(Guid TargetContractId,Guid SourceContractId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractPriceTermsInput(string NodeId,string Scope,Guid? ContractId,string Mapping,
    ImmutableArray<ContractPriceMapInput> Mappings,string Settlement,Guid? PublicationId,int? PublicationVersion,
    string? Index,string ObservationRule,string PendingPolicy,string? ProjectionPolicy);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractPriceTermsPreviewRequest(Guid SnapshotId,Guid CurveId,int Version,ContractPriceTermsInput Terms);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractPriceTermsSaveRequest(Guid RequestId,Guid SnapshotId,Guid CurveId,int Version,int Generation,ContractPriceTermsInput Terms);
public sealed record ContractPriceContractChoice(Guid Id,string Label,DateOnly DeliveryFrom,DateOnly DeliveryTo,DateTimeOffset Expiry,string Kind);
public sealed record ContractPriceSourceChoice(string NodeId,string CurveCode,string Commodity,string Quote,
    ImmutableArray<ContractPriceContractChoice> Contracts,ContractPriceTermsSet? Terms,string? Mapping,ImmutableArray<ContractPriceMapInput> Mappings);
public sealed record ContractPriceCurveChoice(Guid Id,int Version,int Generation,string State,string Code,string Name,
    ImmutableArray<ContractPriceSourceChoice> Sources);
public sealed record ContractPricePublicationChoice(Guid Id,int Version,string Name,string Implementation,
    ImmutableArray<ContractPriceIndexChoice> Indices);
public sealed record ContractPriceIndexChoice(string Code,string Commodity,string Quote,string Symbol,string Statistic);
public sealed record ContractPriceTermsCatalog(Guid SnapshotId,bool CanWrite,ImmutableArray<ContractPriceCurveChoice> Curves,
    ImmutableArray<ContractPricePublicationChoice> Publications,ImmutableArray<string> MappingRules,ImmutableArray<string> SettlementSources,
    ImmutableArray<string> ObservationRules,ImmutableArray<string> PendingPolicies);

public static partial class CustomCurves
{
    public static void MapContractPriceTerms(this RouteGroupBuilder api,PreviewDatabase database,CustomCurveOperation operation)
    {
        api.MapGet("/contract-price-terms",(HttpContext c,Guid? snapshotId)=>operation(c,"ReadMarketData",snapshotId,async(connection,pin,ct)=>
        {
            var data=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
            var saved=await new CustomCurvesStore(database).Read(connection,ct);
            var publications=await new PricePublicationStore(database).Read(connection,ct);
            await using var permission=ScheduleStore.Command(connection,"SELECT security.market_access($1,'WriteMarketDefinitions') AND security.current_write_context()",database.Manifest.CatalogId);
            var canWrite=Equals(await permission.ExecuteScalarAsync(ct),true);
            var curves=saved.Where(v=>v.Sources.Values.All(s=>s.CatalogId==data.DefinitionRevision.CatalogId&&s.RevisionId==data.DefinitionRevision.RevisionId))
                .Select(v=>new ContractPriceCurveChoice(v.Id,v.Version,v.Generation,v.State,v.Definition.Code,v.Definition.Name,
                v.Definition.Nodes.Where(n=>n.Kind=="source").Select(n=>
                {
                    var curve=data.DefinitionRows("curves","code",v.Sources[n.Id].Code).Single();var quote=data.QuoteRows(curve.Text("quote_code")).Single();
                    return new ContractPriceSourceChoice(n.Id,v.Sources[n.Id].Code,PpaQuantitiesStore.Quantity(data,quote.Quantity.Code).Commodity.Code,quote.Key.Code,
                        ContractPriceTermsStore.Members(data,v.Sources[n.Id].Code).Select(k=>new ContractPriceContractChoice(k.Key.Id,k.DeliveryFrom.ToString("yyyy-MM"),k.DeliveryFrom,k.DeliveryTo,k.Expiry,k.Kind.ToString().ToLowerInvariant())).ToImmutableArray(),n.ContractTerms,
                        n.ContractTerms?.MappingRule,n.ContractTerms is null?[]:n.ContractTerms.Mappings.Select(m=>new ContractPriceMapInput(m.Target.Id,m.Source.Id)).ToImmutableArray());
                }).ToImmutableArray())).ToImmutableArray();
            return Results.Ok(new ContractPriceTermsCatalog(pin.RequireFrame().Id,canWrite,curves,
                publications.Select(p=>new ContractPricePublicationChoice(p.Key.Id,p.Key.Version,p.Name,p.Implementation,
                    p.Indices.Select(i=>new ContractPriceIndexChoice(i.Key.Code,i.Commodity.Code,i.Quote.Code,i.Symbol,i.Statistic)).ToImmutableArray())).ToImmutableArray(),
                [ContractPriceMappings.SameDeliveryPeriod,ContractPriceMappings.ExplicitContracts],[ContractPriceTerms.ExchangeFinal,ContractPriceTerms.PublishedIndex,ContractPriceTerms.Derived],
                [ContractPriceTerms.SingleAssessment],[ContractPriceTerms.RequireFinal]));
        }));
        api.MapPost("/contract-price-terms/preview",(HttpContext c,ContractPriceTermsPreviewRequest request)=>operation(c,"ReadMarketData",request.SnapshotId,async(connection,pin,ct)=>
        {
            var data=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
            var (saved,definition,rows)=await ContractTermsDefinition(connection,database,data,request.CurveId,request.Version,request.Terms,ct);
            pin=await SelectedClose.Pin(connection,database,pin,ct);
            var preview=await PreviewRead(connection,database,pin,new(definition,request.SnapshotId),ct);
            return Results.Ok(new{snapshotId=request.SnapshotId,curveId=saved.Id,version=saved.Version,nodeId=request.Terms.NodeId,mapping=definition.Nodes.Single(n=>n.Id==request.Terms.NodeId).ContractTerms!.MappingRule,mappings=rows,definition,preview=preview.Value});
        }));
        api.MapPost("/contract-price-terms/save",(HttpContext c,ContractPriceTermsSaveRequest request)=>operation(c,"WriteMarketDefinitions",request.SnapshotId,async(connection,pin,ct)=>
        {
            var store=new CustomCurvesStore(database);await store.Lock(connection,ct);
            var payload=System.Text.Json.JsonSerializer.Serialize(request,RiskJobs.Json);
            if(await store.Replay(connection,request.RequestId,"contract_price_terms",payload,"WriteMarketDefinitions",ct) is {} replay)return Results.Ok(replay);
            var data=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
            var (saved,definition,_)=await ContractTermsDefinition(connection,database,data,request.CurveId,request.Version,request.Terms,ct);
            if(saved.State!="draft"||saved.Generation!=request.Generation)throw new MarketDataException("custom_curve_conflict","Edit an exact current draft generation; published versions remain immutable.");
            var inner=new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("contract_price_terms_v1:"+request.RequestId.ToString("D"))).AsSpan(0,16));
            var result=await store.Save(connection,new(inner,definition,saved.Layout,request.SnapshotId,saved.Id,saved.Version,request.Generation,"draft"),data,CurveDraftSaver.Person,ct);
            await store.Record(connection,request.RequestId,"contract_price_terms",payload,result,"WriteMarketDefinitions",ct);
            return Results.Ok(result);
        }));
    }
    static async Task<(CustomCurveVersion Saved,OpenEtrm.MarketData.CustomCurves.RecipeDefinition Definition,object[] Rows)> ContractTermsDefinition(
        Npgsql.NpgsqlConnection connection,PreviewDatabase database,PreviewData data,Guid id,int version,ContractPriceTermsInput input,CancellationToken ct)
    {
        if(input is null||input.Scope is not("curve" or "contract")||input.Scope=="curve"&&input.ContractId is not null||input.Scope=="contract"&&input.ContractId is null||input.Mappings.IsDefault||input.Mappings.Length>120||input.Mappings.Any(m=>m is null))
            throw new OpenEtrm.MarketData.MarketDataException("contract_price_terms","Select a source node, whole-curve or exact-contract scope and explicit mapping.");
        var store=new CustomCurvesStore(database);var saved=(await store.Read(connection,ct)).SingleOrDefault(v=>v.Id==id&&v.Version==version)
            ??throw Invalid("The exact authored curve version is unavailable.");
        var node=saved.Definition.Nodes.SingleOrDefault(n=>n.Id==input.NodeId&&n.Kind=="source")??throw Invalid("Choose an exact native source node.");
        if(saved.Sources[node.Id].RevisionId!=data.DefinitionRevision.RevisionId)throw Invalid("The source must retain the selected exact market revision.");
        var members=ContractPriceTermsStore.Members(data,saved.Sources[node.Id].Code);
        var supplied=input.Mappings.Select(m=>new OpenEtrm.MarketData.ConstituentMapping(new(data.DefinitionRevision,m.TargetContractId),new(data.DefinitionRevision,m.SourceContractId))).ToImmutableArray();
        if(input.Scope=="contract"&&input.Mapping==ContractPriceMappings.ExplicitContracts&&supplied.Length==1)
        {
            if(node.ContractTerms is null||supplied[0].Target.Id!=input.ContractId)throw Invalid("A single contract mapping requires saved defaults and the selected exact target.");
            supplied=node.ContractTerms.Mappings.Select(m=>m.Target.Id==input.ContractId?supplied[0]:m).ToImmutableArray();
        }
        if(input.Scope=="contract"&&input.Mapping==ContractPriceMappings.ExplicitContracts&&node.ContractTerms is {} retained&&
            supplied.Any(m=>m.Target.Id!=input.ContractId&&retained.Mappings.SingleOrDefault(p=>p.Target==m.Target)!=m))
            throw Invalid("A contract-scoped request cannot change another target's source mapping.");
        var maps=ContractPriceMappings.Resolve(input.Mapping,members,members,supplied);
        var terms=new ContractPriceTerms(input.Settlement,input.PublicationId is {} publicationId&&input.PublicationVersion is {} publicationVersion?
            new(database.Manifest.CatalogId,publicationId,publicationVersion):null,input.Index is {} index?new(data.ReferenceRevision,index):null,
            input.ObservationRule,input.PendingPolicy,input.ProjectionPolicy);terms.Validate();
        if((input.PublicationId is null)!=(input.PublicationVersion is null))throw Invalid("Select both exact publication identity and version.");
        ContractPriceTermsSet set;
        if(input.Scope=="curve")
        {
            var replacements=node.ContractTerms?.Replacements??[];
            if(node.ContractTerms is {} prior)
            {
                if(input.Mapping==ContractPriceMappings.ExplicitContracts&&maps.Any(m=>replacements.Any(r=>r.Contract==m.Target)&&prior.Mappings.Single(p=>p.Target==m.Target)!=m))
                    throw Invalid("A whole-curve explicit mapping must retain existing contract exceptions. Choose one-contract scope to change an exception.");
                maps=maps.Select(m=>replacements.Any(r=>r.Contract==m.Target)?prior.Mappings.Single(p=>p.Target==m.Target):m).ToImmutableArray();
            }
            var mappingRule=input.Mapping==ContractPriceMappings.SameDeliveryPeriod&&maps.Any(m=>m.Target!=m.Source)?ContractPriceMappings.ExplicitContracts:input.Mapping;
            set=new(terms,replacements,mappingRule,maps);
        }
        else
        {
            var prior=node.ContractTerms??throw Invalid("Save explicit whole-curve terms before adding a contract replacement.");
            var target=members.SingleOrDefault(c=>c.Key.Id==input.ContractId)??throw Invalid("The replacement target must be a member of this exact source.");
            var replacement=maps.Single(m=>m.Target==target.Key);
            maps=prior.Mappings.Select(m=>m.Target==target.Key?replacement:m).ToImmutableArray();
            var mappingRule=prior.MappingRule==ContractPriceMappings.SameDeliveryPeriod&&maps.All(m=>m.Target==m.Source)?ContractPriceMappings.SameDeliveryPeriod:ContractPriceMappings.ExplicitContracts;
            set=prior with{Replacements=prior.Replacements.Where(r=>r.Contract!=target.Key).Append(new(target.Key,terms)).ToImmutableArray(),MappingRule=mappingRule,Mappings=maps};
        }
        var definition=saved.Definition with{Nodes=saved.Definition.Nodes.Select(n=>n.Id==node.Id?n with{ContractTerms=set}:n).ToImmutableArray()};
        await new ContractPriceTermsStore(database).Validate(connection,definition,data,ct);
        var publications=await new PricePublicationStore(database).Read(connection,ct);
        var occurrences=await new PricePublicationStore(database).Occurrences(connection,ct);
        var rows=maps.Select(map=>
        {
            var target=members.Single(c=>c.Key==map.Target);var source=members.Single(c=>c.Key==map.Source);
            var binding=ContractPriceTermsStore.Binding(data,saved.Sources[node.Id].Code,set.For(target.Key),source,publications);
            var occurrence=occurrences.Where(o=>o.Occurrence.Publication==binding.Terms.Publication&&o.Occurrence.Periods.Any(p=>p.From==source.DeliveryFrom&&p.To==source.DeliveryTo)).ToArray();
            if(occurrence.Length>1)throw new MarketDataException("publication_occurrence_ambiguity","The selected publication has multiple occurrences for this mapped source period; select an unambiguous schedule.");
            return (object)new{targetContractId=target.Key.Id,sourceContractId=source.Key.Id,targetFrom=target.DeliveryFrom,targetTo=target.DeliveryTo,
                sourceFrom=source.DeliveryFrom,sourceTo=source.DeliveryTo,expiry=source.Expiry,fixingAt=binding.Fixing?.FixingAt,expectedPublicationAt=occurrence.SingleOrDefault()?.Occurrence.ExpectedReleaseAt,occurrenceId=occurrence.SingleOrDefault()?.Id,
                settlement=binding.Terms.Settlement,index=binding.Terms.Index?.Code,publicationId=binding.Terms.Publication?.Id,publicationVersion=binding.Terms.Publication?.Version};
        }).ToArray();
        return(saved,definition,rows);
    }
}
