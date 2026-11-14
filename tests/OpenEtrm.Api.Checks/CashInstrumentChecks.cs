using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class CashInstrumentChecks
{
    public static void Run()
    {
        var currency=new ReferenceKey(new(Guid.Parse("10000000-0000-0000-0000-000000000001"),Guid.Parse("20000000-0000-0000-0000-000000000001")),"usd");
        var due=TradingInstant.Parse("2031-01-01T13:02:03.123456Z","payment_state");
        var dated=new CashPayment(currency,new(2031,1,1));var timed=new TimedCashPayment(currency,due);
        JsonElement Row(Guid id,string? date,string? at)=>JsonSerializer.SerializeToElement(new{
            instrument_id=id,reference_catalog_id=currency.Revision.CatalogId,reference_revision_id=currency.Revision.RevisionId,
            currency_code=currency.Code,payment_date=date,payment_at=at});
        void Same(CashPaymentInstrument actual,CashPaymentInstrument expected)
        {if(actual.EconomicIdentity!=expected.EconomicIdentity||actual.PaymentAt!=expected.PaymentAt)throw new Exception("Native cash materialization changed its economic definition.");}
        Same(CashPaymentStore.Materialize(Row(dated.Id,"2031-01-01",null)),dated);
        Same(CashPaymentStore.Materialize(Row(timed.Id,null,"2031-01-01T13:02:03.123456Z")),timed);
        if(timed.Id!=Guid.Parse("c38a72b6-25ee-ab30-3d34-288fd04201e6"))throw new Exception("The managed instant identity differs from its independent/native vector.");
        foreach(var invalid in new[]{Row(timed.Id,null,null),Row(timed.Id,"2031-01-01","2031-01-01T13:02:03.123456Z"),
            Row(timed.Id,"2031-01-01",null),Row(dated.Id,null,"2031-01-01T13:02:03.123456Z"),Row(timed.Id,null,"2031-01-01T13:02:03.123457Z")})
            Reject(()=>CashPaymentStore.Materialize(invalid));
        Console.WriteLine("PASS native cash materialization: date and exact-instant definitions, independent identity vector and invalid/ambiguous header refusal");
    }
    static void Reject(Action action)
    {try{action();}catch(MarketDataException e)when(e.Code=="payment_state"){return;}throw new Exception("Expected payment_state.");}
}
