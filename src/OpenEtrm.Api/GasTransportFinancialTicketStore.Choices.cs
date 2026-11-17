using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed partial class GasTransportFinancialTicketStore
{
    internal static bool CapacityBookable(JsonElement facts)=>facts.GetProperty("version").ValueKind==JsonValueKind.Object&&
        TradingInstant.Parse(facts.Text("asOf"),"gas_transport_financial_source")<TradingInstant.Parse(facts.GetProperty("entitlement").Text("through_at"),"gas_transport_financial_source");
    internal async Task<object> Choices(NpgsqlConnection connection,InputFrame frame,TicketFieldRequest request,CancellationToken ct)
    {
        if(frame.ContextId is null)throw GasTransportFinancialTicket.Invalid("profile","Retain the certified financial source context for ticket choices.");
        if(request.Side is not null||!GasTransportFinancialTicket.Describe(request.Draft).TradeFields.Any(field=>field.Id==request.Field))
            throw GasTransportFinancialTicket.Invalid(request.Field,"Select a declared Transport agreement field.");
        if(GasTransportFinancialTicket.StaticChoices(request.Field) is {} fixedChoices)return new TicketChoices(fixedChoices,null);
        if(request.Field=="book")return new TicketChoices([..(await BookScope.Read(connection,database.Manifest.CatalogId,ct)).Select(row=>new TicketChoice(row.Id("id").ToString(),row.Text("name")))],null);
        if(request.Field is "legal_entity" or "counterparty")
            return new TicketChoices([..(await TradeParties.Catalog(connection,database.Manifest.CatalogId,request.Field=="legal_entity",ct)).Select(party=>new TicketChoice($"{party.Id}@{party.Revision}",party.Name))],null);
        if(request.Field is "valuation_definition" or "valuation_reference_bundle" or "package_quantity" or "currency")
            return ValuationChoices(request,await ValuationSources(connection,frame,ct));
        if(request.Field is "entitlement" or "capacity_version")return await CapacityChoices(connection,frame,request,ct);
        return new TicketChoices([],null);
    }

    internal Task<object> Lookup(NpgsqlConnection connection,InputFrame frame,TicketFieldRequest request,CancellationToken ct)=>
        throw GasTransportFinancialTicket.Invalid(request.Field,"Select explicit Transport terms; no derived entry value is supplied.");

    // Published, effective direct definitions can be selected independently of
    // the gas catalogue role. Absence of an observation leaves booking unpriced.
    async Task<ImmutableArray<JsonElement>> ValuationSources(NpgsqlConnection connection,InputFrame frame,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"""
            SELECT DISTINCT jsonb_build_object('definitionRevisionId',m.id,'referenceCatalogId',m.reference_catalog_id,'referenceRevisionId',m.reference_revision_id,
                'label',c.name,'quantity',q.quantity_code,'quantityLabel',co.name||' · '||u.symbol||' · '||n.code,'currency',q.currency_code,'currencyLabel',money.display_code)::text
            FROM market_data.revisions m
            JOIN market_data.revision_periods period ON(period.catalog_id,period.revision_id)=(m.catalog_id,m.id)
            JOIN market_data.curves c ON(c.catalog_id,c.revision_id)=(m.catalog_id,m.id)
            JOIN market_data.curve_members member ON(member.catalog_id,member.revision_id,member.curve_code)=(c.catalog_id,c.revision_id,c.code)
            JOIN market_data.contracts contract ON(contract.catalog_id,contract.revision_id,contract.id)=(member.catalog_id,member.revision_id,member.contract_id)
            JOIN market_data.series series ON(series.catalog_id,series.revision_id,series.contract_id,series.quote_code)=(c.catalog_id,c.revision_id,contract.id,c.quote_code)
            JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(m.reference_catalog_id,m.reference_revision_id,c.quote_code)
            JOIN ref_data.quantity_specifications n ON(n.catalog_id,n.revision_id,n.code)=(q.catalog_id,q.revision_id,q.quantity_code)
            JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(n.catalog_id,n.revision_id,n.commodity_code)
            JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(n.catalog_id,n.revision_id,n.unit_code)
            JOIN ref_data.dimensions dimension ON(dimension.catalog_id,dimension.revision_id,dimension.code)=(u.catalog_id,u.revision_id,u.dimension_code)
            JOIN ref_data.currencies money ON(money.catalog_id,money.revision_id,money.code)=(q.catalog_id,q.revision_id,q.currency_code)
            WHERE m.catalog_id=$1 AND m.state='sealed' AND c.rule='direct' AND c.direct_role='trader_mark' AND q.kind='outright' AND q.multiplier=1
                AND(dimension.mass_power,dimension.length_power,dimension.time_power,dimension.temperature_power,dimension.amount_power,dimension.current_power,dimension.luminous_power)=(0,0,0,0,0,0,0)
                AND period.effective_from<=$2 AND $2<period.effective_to
                AND contract.expiry_at>$3
                AND security.reference_access(m.reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(m.reference_catalog_id,m.reference_revision_id)
            ORDER BY 1 LIMIT 5001
            """,[database.Manifest.CatalogId,frame.Time.Date,frame.At],ct);
        if(rows.Length>5000)throw GasTransportFinancialTicket.Invalid("valuation_definition","More than 5,000 compatible valuation sources require a qualified searched selection; no choices were omitted.");
        return rows;
    }

    internal static TicketChoices ValuationChoices(TicketFieldRequest request,ImmutableArray<JsonElement> sources)
    {
        if(request.Field=="valuation_definition")
        {
            var definitions=sources.GroupBy(row=>row.Id("definitionRevisionId")).Select(group=>new TicketChoice(group.Key.ToString(),
                string.Join(" / ",group.Select(row=>row.Text("label")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))).ToArray();
            TicketChoice Label(TicketChoice choice)
            {
                var collisions=definitions.Where(other=>other.Label==choice.Label&&other.Value!=choice.Value).ToArray();
                if(collisions.Length==0)return choice;
                var id=Guid.Parse(choice.Value).ToString("N");var length=8;
                while(collisions.Any(other=>Guid.Parse(other.Value).ToString("N")[..length]==id[..length]))length++;
                return choice with{Label=choice.Label+" · "+id[..length]};
            }
            return new([..definitions.Select(Label).OrderBy(choice=>choice.Label,StringComparer.Ordinal)],null);
        }
        var values=request.Draft.Trade;
        if(!Guid.TryParse(values.GetValueOrDefault("valuation_definition"),out var definition)||definition==Guid.Empty)
            throw GasTransportFinancialTicket.Invalid("valuation_definition","Select an exact valuation definition first.");
        var selected=sources.Where(row=>row.Id("definitionRevisionId")==definition).ToArray();
        if(selected.Length==0)throw GasTransportFinancialTicket.Invalid("valuation_definition","The selected published valuation definition has no visible compatible whole-package quote.");
        if(request.Field=="valuation_reference_bundle")return new([..selected.Select(row=>new TicketChoice($"{row.Id("referenceCatalogId")}@{row.Id("referenceRevisionId")}","Valuation reference bundle"))
            .DistinctBy(choice=>choice.Value)],null);
        var source=GasTransportFinancialTicket.ValuationSource(request.Draft);
        selected=selected.Where(row=>row.Id("referenceCatalogId")==source.ReferenceCatalogId&&row.Id("referenceRevisionId")==source.ReferenceRevisionId).ToArray();
        if(selected.Length==0)throw GasTransportFinancialTicket.Invalid("valuation_reference_bundle","Select the exact reference bundle belonging to this valuation definition.");
        if(request.Field=="package_quantity")return new([..selected.Select(row=>new TicketChoice(row.Text("quantity"),row.Text("quantityLabel"))).DistinctBy(choice=>choice.Value).OrderBy(choice=>choice.Label,StringComparer.Ordinal)],null);
        if(request.Field!="currency")throw GasTransportFinancialTicket.Invalid(request.Field,"Select a declared valuation source field.");
        if(!values.TryGetValue("package_quantity",out var quantity)||string.IsNullOrEmpty(quantity))throw GasTransportFinancialTicket.Invalid("package_quantity","Select the explicit whole-package basis first.");
        return new([..selected.Where(row=>row.Text("quantity")==quantity).Select(row=>new TicketChoice(row.Text("currency"),row.Text("currencyLabel"))).DistinctBy(choice=>choice.Value).OrderBy(choice=>choice.Label,StringComparer.Ordinal)],null);
    }

    async Task<TicketChoices> CapacityChoices(NpgsqlConnection connection,InputFrame frame,TicketFieldRequest request,CancellationToken ct)
    {
        var values=request.Draft.Trade;
        string Required(string field)=>values.TryGetValue(field,out var value)&&!string.IsNullOrEmpty(value)?value:throw GasTransportFinancialTicket.Invalid(field,"Select this operating scope explicitly first.");
        if(!Guid.TryParse(Required("book"),out var book)||book==Guid.Empty)throw GasTransportFinancialTicket.Invalid("book","Select an exact book.");
        var entity=Required("legal_entity");var party=Required("counterparty");
        var choices=ImmutableArray.CreateBuilder<TicketChoice>();
        bool Matches(JsonElement row)=>row.Id("book_id")==book&&$"{row.Id("legal_entity_id")}@{row.GetProperty("legal_entity_revision").GetInt32()}"==entity&&
            $"{row.Id("counterparty_id")}@{row.GetProperty("counterparty_revision").GetInt32()}"==party;
        if(request.Field=="entitlement")
        {
            foreach(var facts in await GasTransportFinancialCapacity.Read(connection,database,Context(frame),null,ct))
            {
                if(CapacityBookable(facts)&&Matches(facts.GetProperty("entitlement")))choices.Add(new(facts.GetProperty("entitlement").Id("id").ToString(),facts.Text("label")));
            }
        }
        else
        {
            if(!Guid.TryParse(Required("entitlement"),out var entitlement)||entitlement==Guid.Empty)throw GasTransportFinancialTicket.Invalid("entitlement","Select an exact operating entitlement.");
            var facts=await GasTransportFinancialCapacity.ReadOne(connection,database,Context(frame),entitlement,ct);
            var header=facts.GetProperty("entitlement");var version=facts.GetProperty("version");
            if(!Matches(header))throw GasTransportFinancialTicket.Invalid("entitlement","The selected entitlement must retain the exact book and party revisions.");
            if(CapacityBookable(facts))choices.Add(new(version.Id("id").ToString(),$"{header.Text("business_key")} · capacity revision {version.GetProperty("revision").GetInt32()}"));
        }
        return new(choices.ToImmutable(),null);
    }
}
