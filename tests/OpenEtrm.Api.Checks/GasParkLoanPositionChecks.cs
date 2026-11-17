using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasParkLoanPositionChecks
{
    internal static void Run()
    {
        var trade=Guid.NewGuid();var instrument=Guid.NewGuid();var book=Guid.NewGuid();var portfolio=Guid.NewGuid();var catalog=Guid.NewGuid();var reference=Guid.NewGuid();
        var agreement=JsonSerializer.SerializeToElement(new{implementation="physical_gas_park_loan_v1",tradeId=trade,instrumentId=instrument,revision=1,tradeReference=1,instrumentReference=2,
            quantity="720",legalEntityId=Guid.NewGuid(),legalEntityRevision=1,counterpartyId=Guid.NewGuid(),counterpartyRevision=2,
            booking=new{booker_id=Guid.NewGuid(),booker_name="Synthetic Booker",booker_login="synthetic_booker",trade_date="2040-12-18",
                executed_at="2040-12-18T00:00:00Z",implementation="server_booking_utc_v1",label_captured_at="2040-12-18T00:00:00Z"},
            terms=new{definitionRevisionId=Guid.NewGuid(),referenceCatalogId=catalog,referenceRevisionId=reference,quantity="synthetic_energy",currency="synthetic_currency",
                markCurve="synthetic_mark",advanceAt="2040-12-19T00:00:00Z",returnAt="2040-12-24T00:00:00Z",serviceKind="park",perspective="provider"}});
        var bookRow=JsonSerializer.SerializeToElement(new{id=book,name="Synthetic Book",portfolio_id=portfolio,portfolio="Synthetic Portfolio"});
        var metadata=JsonSerializer.SerializeToElement(new{legal_entity="Synthetic Entity",counterparty="Synthetic Party",curve_name="Synthetic Mark",
            commodity_code="gas",commodity_name="Synthetic Gas",unit="energy",currency="SYN",multiplier="1"});
        var position=new GasParkLoanPosition(agreement,bookRow,metadata);var fact=position.Fact();var holding=position.Holding();
        if(fact.TradeId!=trade||fact.BindingId!=instrument||fact.BookedQuantity!="720"||holding["contractIdentityKind"]!.GetValue<string>()!="gas_park_loan_agreement"||
            holding["instrumentType"]!.GetValue<string>()!=GasParkLoanTicket.Product||holding["quantity"]!.GetValue<string>()!="720"||
            holding["trader"]!.GetValue<string>()!="Synthetic Booker"||holding["direction"]!.GetValue<string>()!="multi_side"||
            holding["presentValue"] is not null||holding["executionPnl"] is not null||holding["measure"]!.GetValue<string>()!=GasParkLoanTicket.Measure)
            throw new Exception("A service holding must retain its native owner, positive principal, actual booking labels and independent planned-value measure.");
        var selected=JsonNode.Parse(agreement.GetRawText())!.AsObject();selected["implementation"]="physical_gas_park_loan_balance_time_v1";
        var balance=new GasParkLoanPosition(JsonSerializer.SerializeToElement(selected),bookRow,metadata).Holding();
        if(balance["quantityPolicy"]!.GetValue<string>()!="physical_gas_park_loan_balance_time_v1"||
            balance["measureLabel"]!.GetValue<string>()!="Remaining service plan PV"||!balance["ownershipNote"]!.GetValue<string>().Contains("independent native holdings",StringComparison.Ordinal))
            throw new Exception("A selected trade's remaining service plan must identify its fee profile and independent ownership boundary.");
        Console.WriteLine("PASS Park and Loan positions: exact primary binding, native booking labels and explicit service valuation scope");
    }
}
