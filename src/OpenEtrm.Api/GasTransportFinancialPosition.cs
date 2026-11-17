using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal sealed record GasTransportFinancialPosition(JsonElement Agreement,JsonElement Book,JsonElement Metadata)
{
    internal JsonElement Terms=>Agreement.GetProperty("terms");
    private JsonElement Booking=>Agreement.GetProperty("booking");
    internal Guid Trade=>Agreement.Id("tradeId");
    internal Guid Binding=>Agreement.Id("instrumentId");
    internal Guid Definition=>Terms.Id("definitionRevisionId");
    internal ReferenceRevision Reference=>new(Terms.Id("valuationReferenceCatalogId"),Terms.Id("valuationReferenceRevisionId"));
    private ReferenceKey Quantity=>new(Reference,Terms.Text("packageQuantity"));
    private ReferenceKey Currency=>new(Reference,Terms.Text("currency"));
    private string Label=>"Transport · "+Agreement.Text("businessKey");
    internal TradePositionFact Fact()=>new(Trade,Binding,1,TradingReference.Trade(Agreement.GetProperty("tradeReference").GetInt32()),
        Book.Id("id"),Book.Text("name"),GasTransportFinancialTicket.Product,Label,Terms.OptionalText("from"),Terms.OptionalText("through"),"1",Metadata.Text("unit"));

    internal JsonObject Holding()=>JsonSerializer.SerializeToNode(new{
        booking=new{tradeDate=Booking.Text("trade_date"),legalEntityId=Agreement.Id("legalEntityId"),legalEntityRevision=Agreement.GetProperty("legalEntityRevision").GetInt32(),
            legalEntity=Metadata.Text("legal_entity"),counterpartyId=Agreement.Id("counterpartyId"),counterpartyRevision=Agreement.GetProperty("counterpartyRevision").GetInt32(),counterparty=Metadata.Text("counterparty")},
        traderId=Booking.Id("booker_id"),trader=Booking.Text("booker_name"),traderCode=Booking.Text("booker_login"),memo=(string?)null,
        tradeId=Trade,bindingId=Binding,instrumentId=Binding,revision="1",tradeReference=TradingReference.Trade(Agreement.GetProperty("tradeReference").GetInt32()),
        instrumentReference=TradingReference.Instrument(Agreement,"instrumentReference"),bookId=Book.Id("id"),book=Book.Text("name"),portfolioId=Book.Id("portfolio_id"),portfolio=Book.Text("portfolio"),
        commodity=new{code=Metadata.Text("commodity_code"),name=Metadata.Text("commodity_name")},
        instrumentType=GasTransportFinancialTicket.Product,contractIdentityKind="gas_transport_agreement",contractId=Binding,definitionRevisionId=Definition,
        curve="",projectionCurves=Array.Empty<string>(),priceNavigation=false,sideSchedules=false,marketKey=JsonSerializer.Serialize(new[]{"transport",Trade.ToString("D")}),marketLabel=Label,
        direction="multi_side",perspective=Terms.Text("perspective"),deliveryFrom=Terms.OptionalText("from"),deliveryTo=Terms.OptionalText("through"),
        lots=(string?)null,quantity="1",unit=Metadata.Text("unit"),quantitySpecification=Quantity,currency=Metadata.Text("currency"),currencyReference=Currency,quoteMultiplier=(string?)null,
        executedAt=Booking.Text("executed_at"),bookingFacts=new{implementation=Booking.Text("implementation"),labelCapturedAt=Booking.Text("label_captured_at")},
        executionPrice=(string?)null,fixedPrice=(string?)null,mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,settlement="unprocessed",
        measure=GasTransportFinancialTicket.Measure,measureLabel=GasTransportFinancialTicketStore.MeasureLabel,quantityPolicy=Agreement.Text("implementation"),
        ownershipNote=GasTransportFinancialTicketStore.OwnershipNote,presentValue=(string?)null,unavailableReason=Agreement.OptionalText("unpricedReason")
    },TradingBlotter.Json)!.AsObject();
}
