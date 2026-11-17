using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

internal static class GasParkCustodyPositionChecks
{
    internal static void Run()
    {
        var agreement=JsonNode.Parse(GasParkCustodyAdapterChecks.Agreement("provider").GetRawText())!.AsObject();
        agreement["tradeReference"]=1;agreement["instrumentReference"]=2;agreement["legalEntityId"]=Guid.NewGuid();agreement["legalEntityRevision"]=1;
        agreement["counterpartyId"]=Guid.NewGuid();agreement["counterpartyRevision"]=1;
        agreement["booking"]=JsonSerializer.SerializeToNode(new{booker_id=Guid.NewGuid(),booker_name="Synthetic Booker",booker_login="synthetic_booker",trade_date="2032-01-14",
            executed_at="2032-01-14T10:00:00Z",implementation="server_booking_utc_v1",label_captured_at="2032-01-14T10:00:00Z"});
        var row=JsonSerializer.SerializeToElement(agreement);
        var book=JsonSerializer.SerializeToElement(new{id=row.Id("bookId"),name="Synthetic Book",portfolio_id=Guid.NewGuid(),portfolio="Synthetic Portfolio"});
        var labels=JsonSerializer.SerializeToElement(new{legal_entity="Synthetic Entity",counterparty="Synthetic Party",point_name="Synthetic Custody Point",commodity_code="gas",commodity_name="Synthetic Gas",unit="energy",currency="SYN"});
        var position=new GasParkCustodyPosition(row,book,labels);var holding=position.Holding();var fact=position.Fact();
        if(fact.TradeId!=row.Id("tradeId")||fact.BindingId!=row.Id("instrumentId")||fact.BookedQuantity!="720"||
            holding["instrumentType"]!.GetValue<string>()!=GasParkCustodyTicket.Product||holding["contractIdentityKind"]!.GetValue<string>()!="gas_park_custody_agreement"||
            holding["priceNavigation"]!.GetValue<bool>()||holding["curve"]!.GetValue<string>()!=""||holding["projectionCurves"]!.AsArray().Count!=0||
            holding["marketLabel"]!.GetValue<string>()!="Synthetic Custody Point"||holding["mark"] is not null||holding["executionPrice"] is not null||
            holding["measure"]!.GetValue<string>()!=GasParkCustodyTicket.Measure||holding["presentValue"] is not null)
            throw new Exception("A custody row must retain its exact service owner and point without inventing a commodity mark, unit price or curve navigation.");
        Console.WriteLine("PASS custody position: exact service identity, retained booking labels and explicit absence of price navigation");
    }
}
