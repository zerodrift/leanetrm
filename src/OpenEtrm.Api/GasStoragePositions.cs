using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

// One booked gas storage contract as retained: its trade facts, entitlement, ratchet bands, gas days and the plan in force, if any.
internal sealed record GasStoragePosition(JsonElement Head,JsonElement Book,JsonElement Terms,JsonElement Ratchets,JsonElement Days,JsonElement Plan)
{
    internal Guid Trade=>Head.Id("trade_id");
    internal Guid Binding=>Head.Id("instrument_id");
    internal Guid Definition=>Head.Id("definition_revision_id");
    internal string From=>Terms.Text("term_from");
    internal string To=>Terms.Text("term_to");
    internal string InjectionCurve=>Terms.Text("injection_curve_code");
    internal string WithdrawalCurve=>Terms.Text("withdrawal_curve_code");
    internal string MarketLabel=>$"{Terms.Text("commodity")} storage · {Terms.Text("maximum_inventory")} {Terms.Text("unit")}";
    internal ReferenceRevision Reference=>new(Terms.Id("reference_catalog_id"),Terms.Id("reference_revision_id"));
    internal ReferenceKey Quantity=>new(Reference,Terms.Text("quantity_code"));
    internal ReferenceKey Currency=>new(Reference,Terms.Text("currency_code"));
    internal bool Planned=>Plan.ValueKind==JsonValueKind.Object;
    internal TradePositionFact Fact()=>new(Trade,Binding,int.Parse(Head.Text("revision"),CultureInfo.InvariantCulture),TradingReference.Trade(Head),Book.Id("id"),Book.Text("name"),
        GasStoragePositions.Type,MarketLabel,From,To,Terms.Text("maximum_inventory"),Terms.Text("unit"));
    internal JsonObject Holding()=>JsonSerializer.SerializeToNode(new{
        booking=new{tradeDate=Head.Text("trade_date"),legalEntityId=Head.Id("legal_entity_id"),legalEntityRevision=int.Parse(Head.Text("legal_entity_revision"),CultureInfo.InvariantCulture),
            legalEntity=Head.Text("legal_entity"),counterpartyId=Head.Id("counterparty_id"),counterpartyRevision=int.Parse(Head.Text("counterparty_revision"),CultureInfo.InvariantCulture),counterparty=Head.Text("counterparty")},
        traderId=Head.Id("booker_id"),trader=Head.Text("booker_name"),traderCode=Head.Text("booker_login"),memo=Head.Text("reason"),
        tradeId=Trade,bindingId=Binding,instrumentId=Binding,revision=Head.Text("revision"),tradeReference=TradingReference.Trade(Head),instrumentReference=TradingReference.Instrument(Head),
        bookId=Book.Id("id"),book=Book.Text("name"),portfolioId=Book.Id("portfolio_id"),portfolio=Book.Text("portfolio"),
        instrumentType=GasStoragePositions.Type,contractIdentityKind=GasStoragePositions.Type,contractId=Binding,definitionRevisionId=Definition,
        curve=WithdrawalCurve,projectionCurves=Terms.GetProperty("projection_curves").EnumerateArray().Select(x=>x.GetString()).ToArray(),
        marketKey=$"{InjectionCurve}/{WithdrawalCurve}",marketLabel=MarketLabel,commodity=new{code=Terms.Text("commodity_code"),name=Terms.Text("commodity")},
        injectionCurve=InjectionCurve,withdrawalCurve=WithdrawalCurve,planRevision=Planned?Plan.Text("revision"):null,
        deliveryFrom=From,deliveryTo=To,lots=(string?)null,quantity=Terms.Text("maximum_inventory"),unit=Terms.Text("unit"),quantitySpecification=Quantity,
        currency=Terms.Text("currency"),currencyReference=Currency,quoteMultiplier="1",direction="buy",
        executedAt=Head.Text("executed_at"),bookingFacts=new{implementation=Head.Text("booking_implementation"),labelCapturedAt=Head.Text("label_captured_at")},
        executionPrice=(string?)null,fixedPrice=(string?)null,mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,
        settlement="unprocessed",measure=GasStoragePositions.Measure,measureLabel=GasStoragePositions.MeasureLabel,quantityPolicy="exclusive_right_v1",
        presentValue=(string?)null,unavailableReason=(string?)null
    },TradingBlotter.Json)!.AsObject();
}

internal static class GasStoragePositions
{
    internal const string Type="gas_storage",Measure="gas_storage_plan_present_value",MeasureLabel="Storage plan PV";
    private static MarketDataException Missing()=>new("position_facts","Every gas storage position needs its exact contract, ratchets, gas days, party and booking metadata.");
    internal static async Task<ImmutableArray<GasStoragePosition>> Read(NpgsqlConnection connection,PreviewManifest manifest,
        ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null,ImmutableHashSet<Guid>? trades=null)
    {
        var books=await BookScope.Read(connection,manifest.CatalogId,ct);
        if(position?.TradeId is {} required&&trades is not null&&!trades.Contains(required))throw TradePositionSelection.Unavailable();
        var scope=position?.TradeId is {} one?ImmutableHashSet.Create(one):trades;
        object[] arguments=[manifest.CatalogId,bookIds.ToArray(),portfolioIds.ToArray(),(object?)scope?.ToArray()??DBNull.Value];
        var inventory=await PreviewDatabase.Rows(connection,"SELECT value::text FROM jsonb_array_elements(security.gas_storage_position_scope($1,$2::uuid[],$3::uuid[],$4::uuid[]))",arguments,ct);
        var selected=BookScope.Select(books,bookIds,portfolioIds).ToImmutableDictionary(b=>b.Id("id"));
        if(selected.IsEmpty)return [];
        var rows=await PreviewDatabase.Rows(connection,"SELECT value::text FROM jsonb_array_elements(security.read_gas_storage_positions($1,$2::uuid[],$3::uuid[],$4::uuid[]))",arguments,ct);
        var heads=rows.Select(r=>r.GetProperty("head")).ToImmutableArray();PositionInventory.Require(inventory,heads,Missing);
        if(position is not null)position.Require(heads);
        var result=rows.Select(row=>new GasStoragePosition(row.GetProperty("head"),selected[row.GetProperty("head").Id("book_id")],row.GetProperty("terms"),
            row.GetProperty("ratchets"),row.GetProperty("days"),row.GetProperty("plan"))).ToImmutableArray();
        foreach(var item in result)AsyncReadVisibility.Bundle(manifest with{RevisionId=item.Definition,ReferenceCatalogId=item.Reference.CatalogId,ReferenceRevisionId=item.Reference.RevisionId});
        return result;
    }
}
