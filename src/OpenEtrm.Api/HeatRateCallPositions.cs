using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

// One booked heat-rate call option as retained: its trade facts, terms and dated exercises.
internal sealed record HeatRateCallPosition(JsonElement Head,JsonElement Book,JsonElement Terms,JsonElement Exercises)
{
    internal Guid Trade=>Head.Id("trade_id");
    internal Guid Binding=>Head.Id("instrument_id");
    internal Guid Definition=>Head.Id("definition_revision_id");
    internal string From=>Terms.Text("delivery_from");
    internal string To=>Terms.Text("delivery_to");
    internal string PowerCurve=>Terms.Text("power_curve_code");
    internal string FuelCurve=>Terms.Text("fuel_curve_code");
    internal string MarketLabel=>$"{PowerCurve} vs {FuelCurve} · heat rate {Terms.Text("heat_rate")}";
    internal ReferenceRevision Reference=>new(Terms.Id("reference_catalog_id"),Terms.Id("reference_revision_id"));
    internal ReferenceKey Capacity=>new(Reference,Terms.Text("capacity_quantity_code"));
    internal ReferenceKey Currency=>new(Reference,Terms.Text("currency_code"));
    internal ExactDecimal SignedQuantity=>ExactDecimal.Parse(Head.Text("quantity"));
    internal ImmutableArray<DateOnly> Months=>[..Exercises.EnumerateArray().Select(e=>DateWorkspace.Date(e.Text("delivery_day"))).Select(d=>new DateOnly(d.Year,d.Month,1)).Distinct()];
    internal TradePositionFact Fact()=>new(Trade,Binding,int.Parse(Head.Text("revision"),CultureInfo.InvariantCulture),TradingReference.Trade(Head),Book.Id("id"),Book.Text("name"),
        HeatRateCallPositions.Type,MarketLabel,From,To,SignedQuantity.ToString(),Terms.Text("capacity_unit"));
    internal JsonObject Holding()=>JsonSerializer.SerializeToNode(new{
        booking=new{tradeDate=Head.Text("trade_date"),legalEntityId=Head.Id("legal_entity_id"),legalEntityRevision=int.Parse(Head.Text("legal_entity_revision"),CultureInfo.InvariantCulture),
            legalEntity=Head.Text("legal_entity"),counterpartyId=Head.Id("counterparty_id"),counterpartyRevision=int.Parse(Head.Text("counterparty_revision"),CultureInfo.InvariantCulture),counterparty=Head.Text("counterparty")},
        traderId=Head.Id("booker_id"),trader=Head.Text("booker_name"),traderCode=Head.Text("booker_login"),memo=Head.Text("reason"),
        tradeId=Trade,bindingId=Binding,instrumentId=Binding,revision=Head.Text("revision"),tradeReference=TradingReference.Trade(Head),instrumentReference=TradingReference.Instrument(Head),
        bookId=Book.Id("id"),book=Book.Text("name"),portfolioId=Book.Id("portfolio_id"),portfolio=Book.Text("portfolio"),
        instrumentType=HeatRateCallPositions.Type,contractIdentityKind=HeatRateCallPositions.Type,contractId=Binding,definitionRevisionId=Definition,
        curve=PowerCurve,projectionCurves=Terms.GetProperty("projection_curves").EnumerateArray().Select(x=>x.GetString()).ToArray(),marketKey=$"{PowerCurve}/{FuelCurve}",marketLabel=MarketLabel,
        commodity=new{code=Terms.Text("power_commodity_code"),name=Terms.Text("power_commodity")},fuelCurve=FuelCurve,fuelCommodity=Terms.Text("fuel_commodity"),
        heatRate=Terms.Text("heat_rate"),heatRateUnit=$"{Terms.Text("fuel_unit")}/{Terms.Text("power_unit")}",strike=Terms.Text("strike"),
        exerciseStyle=Terms.Text("exercise_style"),model=Terms.Text("model"),exerciseDays=Exercises.GetArrayLength(),
        deliveryFrom=From,deliveryTo=To,lots=(string?)null,quantity=SignedQuantity.ToString(),unit=Terms.Text("capacity_unit"),quantitySpecification=Capacity,
        currency=Terms.Text("currency"),currencyReference=Currency,quoteMultiplier="1",direction=SignedQuantity.CompareTo(default)>0?"buy":"sell",
        executedAt=Head.Text("executed_at"),bookingFacts=new{implementation=Head.Text("booking_implementation"),labelCapturedAt=Head.Text("label_captured_at")},
        executionPrice=(string?)null,fixedPrice=Terms.Text("strike"),mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,
        settlement="unprocessed",measure=HeatRateCallPositions.Measure,measureLabel=HeatRateCallPositions.MeasureLabel,quantityPolicy="linear_capacity_v1",
        presentValue=(string?)null,unavailableReason=(string?)null
    },TradingBlotter.Json)!.AsObject();
}

internal static class HeatRateCallPositions
{
    internal const string Type="heat_rate_call",Measure="heat_rate_call_present_value",MeasureLabel="Heat-rate call option PV";
    private static MarketDataException Missing()=>new("position_facts","Every heat-rate call position needs its exact terms, exercises, party and booking metadata.");
    internal static async Task<ImmutableArray<HeatRateCallPosition>> Read(NpgsqlConnection connection,PreviewManifest manifest,
        ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null,ImmutableHashSet<Guid>? trades=null)
    {
        var books=await BookScope.Read(connection,manifest.CatalogId,ct);
        if(position?.TradeId is {} required&&trades is not null&&!trades.Contains(required))throw TradePositionSelection.Unavailable();
        var scope=position?.TradeId is {} one?ImmutableHashSet.Create(one):trades;
        object[] arguments=[manifest.CatalogId,bookIds.ToArray(),portfolioIds.ToArray(),(object?)scope?.ToArray()??DBNull.Value];
        var inventory=await PreviewDatabase.Rows(connection,"SELECT value::text FROM jsonb_array_elements(security.heat_rate_call_position_scope($1,$2::uuid[],$3::uuid[],$4::uuid[]))",arguments,ct);
        var selected=BookScope.Select(books,bookIds,portfolioIds).ToImmutableDictionary(b=>b.Id("id"));
        if(selected.IsEmpty)return [];
        var rows=await PreviewDatabase.Rows(connection,"SELECT value::text FROM jsonb_array_elements(security.read_heat_rate_call_positions($1,$2::uuid[],$3::uuid[],$4::uuid[]))",arguments,ct);
        var heads=rows.Select(r=>r.GetProperty("head")).ToImmutableArray();PositionInventory.Require(inventory,heads,Missing);
        if(position is not null)position.Require(heads);
        var result=rows.Select(row=>new HeatRateCallPosition(row.GetProperty("head"),selected[row.GetProperty("head").Id("book_id")],row.GetProperty("terms"),row.GetProperty("exercises"))).ToImmutableArray();
        foreach(var item in result)AsyncReadVisibility.Bundle(manifest with{RevisionId=item.Definition,ReferenceCatalogId=item.Reference.CatalogId,ReferenceRevisionId=item.Reference.RevisionId});
        return result;
    }
}
