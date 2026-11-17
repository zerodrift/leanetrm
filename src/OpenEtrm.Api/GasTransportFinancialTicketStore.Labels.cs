using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

internal sealed partial class GasTransportFinancialTicketStore
{
    // Inspecting saved terms consumes their exact retained labels. It does not
    // discover new operating entitlements or require their authoring permissions.
    async Task<ImmutableDictionary<string,ImmutableArray<TicketChoice>>> RetainedChoices(NpgsqlConnection c,JsonElement agreement,TicketDraft draft,CancellationToken ct)
    {
        var terms=agreement.GetProperty("terms");var source=GasTransportFinancialTicket.ValuationSource(draft);
        var data=await MarketInputSelection.ReadTerms(database,c,source.DefinitionRevisionId,ct);
        if(data.ReferenceRevision.CatalogId!=source.ReferenceCatalogId||data.ReferenceRevision.RevisionId!=source.ReferenceRevisionId)throw GasTransportFinancialStore.Source();
        var labels=(await GasTransportFinancialPositions.ReadLabels(c,database,[agreement],ct)).Single();
        var reference=await PreviewDatabase.Rows(c,"SELECT jsonb_build_object('label',revision_key)::text FROM ref_data.revisions WHERE(catalog_id,id)=($1,$2)",
            [source.ReferenceCatalogId,source.ReferenceRevisionId],ct);
        if(reference.Length!=1||string.IsNullOrWhiteSpace(reference[0].Text("label")))throw GasTransportFinancialStore.Source();
        var quantity=data.References["quantity_specifications"].Single(q=>q.Text("code")==terms.Text("packageQuantity"));
        var commodity=data.References["commodities"].Single(q=>q.Text("code")==quantity.Text("commodity_code"));
        var quotes=data.References["quote_specifications"].Where(q=>q.Text("quantity_code")==terms.Text("packageQuantity")&&q.Text("currency_code")==terms.Text("currency"))
            .Select(q=>q.Text("code")).ToHashSet(StringComparer.Ordinal);
        var names=data.Definitions["curves"].Where(curve=>quotes.Contains(curve.Text("quote_code"))).Select(curve=>curve.Text("name"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if(names.Length==0||names.Any(string.IsNullOrWhiteSpace))throw GasTransportFinancialStore.Source();
        var state=agreement.GetProperty("financialState");
        var dynamic=new Dictionary<string,string>{["book"]=labels.Text("book"),["legal_entity"]=labels.Text("legal_entity"),["counterparty"]=labels.Text("counterparty"),
            ["valuation_definition"]=string.Join(" / ",names),["valuation_reference_bundle"]=reference[0].Text("label"),
            ["package_quantity"]=commodity.Text("name")+" · "+labels.Text("unit"),["currency"]=labels.Text("currency"),
            ["entitlement"]=terms.Text("entitlementReference"),["capacity_version"]="Capacity revision "+state.GetProperty("capacityVersionRevision").GetInt32()};
        return SelectedChoices(draft,dynamic);
    }

    internal static ImmutableDictionary<string,ImmutableArray<TicketChoice>> SelectedChoices(TicketDraft draft,IReadOnlyDictionary<string,string> labels)
    {
        var result=ImmutableDictionary.CreateBuilder<string,ImmutableArray<TicketChoice>>(StringComparer.Ordinal);
        foreach(var field in GasTransportFinancialTicket.Describe(draft).TradeFields.Where(f=>f.Choices is not null))
        {
            if(!draft.Trade.TryGetValue(field.Id,out var value)||string.IsNullOrWhiteSpace(value))throw GasTransportFinancialStore.Source();
            var fixedChoices=GasTransportFinancialTicket.StaticChoices(field.Id);
            var label=fixedChoices is {} choices?choices.SingleOrDefault(c=>c.Value==value)?.Label:labels.GetValueOrDefault(field.Id);
            if(string.IsNullOrWhiteSpace(label))throw GasTransportFinancialStore.Source();
            result.Add(field.Id,[new(value,label)]);
        }
        return result.ToImmutable();
    }
}
