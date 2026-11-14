using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class GasOwnedHoldingsChecks
{
    public static void Run()
    {
        var f=new Fixture();var quote=f.Quotes[0];var at=f.Snapshot().ValuationAt;
        var account=Guid.NewGuid();var evidence=Guid.NewGuid();
        var lot=new GasOwnedLot(Guid.NewGuid(),account,new(ExactDecimal.Parse("100"),quote.Quantity),at.AddDays(-2),at.AddDays(-1),at.AddDays(-2),evidence);
        var reservation=new GasStockReservation(Guid.NewGuid(),lot.Id,new(ExactDecimal.Parse("60"),quote.Quantity),at.AddHours(-2),at.AddHours(-2),null);
        var movement=new GasStockConsumption(Guid.NewGuid(),lot.Id,new(ExactDecimal.Parse("25"),quote.Quantity),GasStockUse.Delivery,
            at.AddHours(-1),at.AddHours(-1),Guid.NewGuid(),reservation.Id);
        var balance=GasOwnedLedger.Balance(lot,[reservation],[movement],at,at);
        Equal(balance.Owned.Amount,"75");Equal(balance.Reserved.Amount,"35");Equal(balance.Available.Amount,"40");
        var awaiting=GasOwnedLedger.Balance(lot,[],[],at.AddHours(-25),at);
        Equal(awaiting.Owned.Amount,"100");Equal(awaiting.Available.Amount,"0");
        var historical=GasOwnedLedger.Balance(lot,[reservation],[movement],at,at.AddHours(-3));
        Equal(historical.Owned.Amount,"100");Equal(historical.Reserved.Amount,"0");
        var later=movement with{KnownAt=at.AddHours(1)};
        Equal(GasOwnedLedger.Balance(lot,[reservation],[later],at,at).Owned.Amount,"100");
        Reject("gas_stock_capacity",()=>GasOwnedLedger.Balance(lot,[reservation],[movement with{Quantity=new(ExactDecimal.Parse("101"),quote.Quantity)}],at,at));
        Reject("gas_stock_reservation",()=>GasOwnedLedger.Balance(lot,[reservation],[movement with{Quantity=new(ExactDecimal.Parse("61"),quote.Quantity)}],at,at));
        Reject("gas_stock_availability",()=>GasOwnedLedger.Balance(lot,[],[movement with{At=at.AddDays(-3),KnownAt=at.AddDays(-3),ReservationId=null}],at,at));
        var loan=new GasCommodityLoan(Guid.NewGuid(),account,lot.Id,new(ExactDecimal.Parse("100"),quote.Quantity),at.AddDays(-1),at.AddDays(5),at.AddDays(-1),
            GasCommodityLoan.TitleTransfer,Guid.NewGuid());
        var repayment=new GasLoanRepayment(Guid.NewGuid(),loan.Id,lot.Id,new(ExactDecimal.Parse("30"),quote.Quantity),at,at,Guid.NewGuid());
        Equal(GasOwnedLedger.ReturnOutstanding(loan,[repayment],at,at).Amount,"70");
        Equal(GasOwnedLedger.ReturnOutstanding(loan,[],at.AddDays(6),at.AddDays(6)).Amount,"100");
        Reject("gas_loan_return",()=>GasOwnedLedger.ReturnOutstanding(loan,[repayment with{Quantity=new(ExactDecimal.Parse("101"),quote.Quantity)}],at,at));
        // A loan transfers title to fungible gas. The stock and return obligation
        // have opposite signed quantities; selling stock never extinguishes debt.
        var mark=new CurveRequest(f.C("future"),f.K(1));
        var stock=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,null,GasHoldingCarrying.Nominal);
        var debt=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,at.AddDays(5),GasHoldingCarrying.Nominal);
        var owner=Guid.NewGuid();var book=Guid.NewGuid();
        var held=new Trade(owner,book,[new(Guid.NewGuid(),stock,new(ExactDecimal.Parse("100"),quote.Quantity)),
            new(Guid.NewGuid(),debt,new(ExactDecimal.Parse("-100"),quote.Quantity))]);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null);
        var prepared=RiskEngine.Prepare(f.Model(),f.Snapshot(),[ValuationPortfolio.FromTrades([held],setup)]);
        var values=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy);
        Equal(values.Groups.Single().BaseValues.Single().Value,"0");
        var remaining=new Trade(owner,book,[new(Guid.NewGuid(),debt,new(ExactDecimal.Parse("-100"),quote.Quantity))]);
        var risk=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),f.Snapshot(),[ValuationPortfolio.FromTrades([remaining],setup)]),[],RiskEngine.ExactNumericalPolicy);
        if(risk.Groups.Single().BaseValues.Single().Value.Numerator=="0")throw new Exception("A consumed borrowed lot must retain its return liability.");
        var delivery=new GasPhysicalDeliveryTerms(Guid.NewGuid(),series:f.R("gas_future"),quantity:quote.Quantity,currency:quote.Currency,
            location:f.R("location"),from:at,to:at.AddDays(1),cashDueAt:at.AddDays(2),mark:mark,carrying:GasHoldingCarrying.Nominal,
            implementation:GasPhysicalDeliveryTerms.Version);
        var title=GasPhysicalFinalHandoff.Title(delivery,new(ExactDecimal.Parse("10"),quote.Quantity),
            new(ExactDecimal.Parse("10"),quote.Quantity),ExactUnitPrice.Parse("12",PriceUnit.FromQuote(quote)),at,default);
        Equal(title.StockChange.Amount,"10");Equal(title.Consideration,"-120");Equal(title.Remaining.Amount,"0");
        // At an unchanged delivery mark of 15 the pre-title obligation is 30;
        // post-title stock 150 plus consideration -120 remains exactly 30.
        Equal(ExactMoney.Parse("150",quote.Currency).Add(title.Consideration),"30");
        Reject("gas_delivery_supply",()=>GasPhysicalFinalHandoff.Title(delivery,new(ExactDecimal.Parse("-10"),quote.Quantity),
            new(ExactDecimal.Parse("10"),quote.Quantity),ExactUnitPrice.Parse("12",PriceUnit.FromQuote(quote)),at,default));
        Console.WriteLine("PASS gas owned holdings: exact title/availability, reservations, no negative stock, bitemporal facts, surviving loan return and ordinary PV conservation");
    }
    static void Equal(ExactDecimal actual,string expected){if(actual!=ExactDecimal.Parse(expected))throw new Exception($"Expected {expected}, got {actual}.");}
    static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    static void Reject(string code,Action action){try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
