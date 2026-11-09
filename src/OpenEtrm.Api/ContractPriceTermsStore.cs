using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Publications;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed partial class ContractPriceTermsStore(PreviewDatabase database)
{
    static MarketDataException Error(string message)=>new("contract_price_terms",message);
    internal static ImmutableArray<MarketContract> Members(PreviewData data,string code)=>[..data.DefinitionRows("curve_members","curve_code",code)
        .OrderBy(r=>r.GetProperty("ordinal").GetInt32()).Select(r=>data.ContractRows(r.Id("contract_id")).Single())];
    internal async Task Validate(NpgsqlConnection connection,RecipeDefinition definition,PreviewData data,CancellationToken ct)
    {
        var nodes=definition.Nodes.Where(n=>n.ContractTerms is not null).ToArray();if(nodes.Length==0)return;
        var publications=await new PricePublicationStore(database).Read(connection,ct);
        foreach(var node in nodes)
        {
            if(node.Kind!="source")throw Error("Contract terms require an exact native source node.");
            var set=node.ContractTerms!;_=set.For(default);var members=Members(data,node.Parameters["curve"]);
            var targets=members.Where(c=>set.Mappings.Any(m=>m.Target==c.Key)).ToImmutableArray();
            if(targets.Length!=members.Length)throw Error("Every source member requires an explicit mapping; the authored curve retains its complete universe.");
            var maps=ContractPriceMappings.Resolve(set.MappingRule,targets,members,set.MappingRule==ContractPriceMappings.SameDeliveryPeriod?[]:set.Mappings);
            if(!maps.ToHashSet().SetEquals(set.Mappings))throw Error("Stored source pairs differ from their selected mapping convention.");
            foreach(var target in targets)_=Binding(data,node.Parameters["curve"],set.For(target.Key),members.Single(c=>c.Key==set.SourceFor(target.Key)),publications);
        }
    }
    internal static ContractPriceTermsBinding Binding(PreviewData data,string curveCode,ContractPriceTerms terms,MarketContract contract,
        ImmutableArray<PricePublicationDefinition> publications)
    {
        terms.Validate();var curve=data.DefinitionRows("curves","code",curveCode).Single();var quote=data.QuoteRows(curve.Text("quote_code")).Single();
        var commodity=PpaQuantitiesStore.Quantity(data,quote.Quantity.Code).Commodity;
        PricePublicationDefinition? publication=null;IndexFixing? fixing=null;
        if(terms.UsesPublication)
        {
            publication=publications.SingleOrDefault(p=>p.Key==terms.Publication)??throw Error("The exact publication version is unavailable in this context.");
            if(PricePublicationBehavior.Create(publication) is UnsupportedPricePublication)throw new MarketDataException("publication_unsupported","This publication has no executable assessment implementation.");
            var member=publication.Indices.SingleOrDefault(i=>i.Key==terms.Index);
            if(member is null||member.Commodity!=commodity||member.Quote!=quote.Key||member.Unit!=PriceUnit.FromQuote(quote))throw Error("Choose an index with the source's exact commodity and quote unit.");
            var target=data.ReferenceRows("settlement_targets","code",curve.Text("settlement_target_code")).Single();
            if(target.Text("kind")!="index"||target.OptionalText("index_b_code") is not null||contract.Kind!=ContractKind.Forward)throw Error("Published settlement requires a single-index forward source.");
            var original=new ReferenceKey(data.ReferenceRevision,target.Text("index_a_code"));
            var selection=new PriceSettlementSelection(terms.Index==original?PriceSettlementMode.CurveTarget:PriceSettlementMode.Override,original,terms.Index!.Value,terms.ProjectionPolicy);
            fixing=PriceSettlementInputs.Fixings(data,selection,quote,[contract]).SingleOrDefault()??throw new MarketDataException("index_fixing_convention_missing","The selected publication index needs an exact qualified fixing definition for every mapped contract.");
        }
        else if(terms.Settlement==ContractPriceTerms.ExchangeFinal&&(contract.Kind!=ContractKind.Future||curve.Text("rule")!="direct"))
            throw Error("Official exchange settlement requires an exact direct futures source.");
        else if(terms.Settlement==ContractPriceTerms.Derived&&curve.Text("rule")=="direct")throw Error("Derived settlement requires a calculated source with explicit inputs.");
        return new(terms,publication,commodity,fixing);
    }
    internal async Task Insert(NpgsqlConnection connection,Guid id,int version,RecipeDefinition definition,CancellationToken ct)
    {
        var catalog=database.Manifest.CatalogId;
        foreach(var node in definition.Nodes.Where(n=>n.ContractTerms is not null))
        {
            var set=node.ContractTerms!;var rows=new[]{(Contract:(ContractKey?)null,Terms:set.Default)}.Concat(set.Replacements.Select(r=>(Contract:(ContractKey?)r.Contract,Terms:r.Terms))).ToArray();
            for(var i=0;i<rows.Length;i++)
            {
                var row=rows[i];var term=row.Terms;
                await using var command=ScheduleStore.Command(connection,"""
INSERT INTO market_data.custom_curve_price_terms(catalog_id,curve_id,version,node_id,ordinal,contract_id,settlement,
 publication_id,publication_version,reference_catalog_id,reference_revision_id,index_code,observation_rule,pending_policy,projection_policy,mapping_rule)
VALUES($1,$2,$3,$4,$5,$6::uuid,$7,$8::uuid,$9::integer,$10::uuid,$11::uuid,$12::text,$13,$14,$15::text,$16)
""",catalog,id,version,node.Id,i,(object?)row.Contract?.Id??DBNull.Value,term.Settlement,(object?)term.Publication?.Id??DBNull.Value,
                    (object?)term.Publication?.Version??DBNull.Value,(object?)term.Index?.Revision.CatalogId??DBNull.Value,(object?)term.Index?.Revision.RevisionId??DBNull.Value,
                    (object?)term.Index?.Code??DBNull.Value,term.ObservationRule,term.PendingPolicy,(object?)term.ProjectionPolicy??DBNull.Value,set.MappingRule);
                await command.ExecuteNonQueryAsync(ct);
            }
            foreach(var map in set.Mappings)
            {
                await using var command=ScheduleStore.Command(connection,"""
INSERT INTO market_data.custom_curve_price_mappings(catalog_id,curve_id,version,node_id,source_catalog_id,source_revision_id,source_curve_code,target_contract_id,source_contract_id)
VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
""",catalog,id,version,node.Id,map.Target.Revision.CatalogId,map.Target.Revision.RevisionId,node.Parameters["curve"],map.Target.Id,map.Source.Id);
                await command.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
