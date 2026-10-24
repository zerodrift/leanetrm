using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

internal static class ExactCurvePriceChecks
{
    public static int Run()
    {
        var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        var currency=new ReferenceKey(revision,"currency");
        var quantity=new ReferenceKey(revision,"quantity");
        var unit=new PriceUnit(currency,quantity,ExactDecimal.Parse("100"));
        var third=ExactUnitPrice.FromPrice(new(ExactDecimal.Parse("1"),unit)).Divide(3);
        void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}; got {actual}.");}
        void Reject(string code,Action run)
        {try{run();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception($"Expected {code}.");}
        var value=third.Scale(ExactDecimal.Parse("-0.5")).ExactTimes(new(ExactDecimal.Parse("3"),quantity));
        Equal(value.Numerator,"-50");Equal(value.Denominator,"1");
        var quotient=third.Divide(ExactDecimal.Parse("0.5"));
        Equal(quotient.Numerator,"2");Equal(quotient.Denominator,"3");
        Equal(quotient.Scale(ExactDecimal.Parse("3")).ToUnitPriceExact().Value.ToString(),"2");
        Reject("precision_loss",()=>third.ToUnitPriceExact());
        Reject("numeric_division",()=>third.Divide(default(ExactDecimal)));
        Reject("numeric_division",()=>third.Divide(ExactDecimal.Parse("-1")));
        Reject("quantity_mismatch",()=>third.ExactTimes(new(ExactDecimal.Parse("1"),new(revision,"other_quantity"))));
        Console.WriteLine("PASS exact curve prices: rational scaling, units and explicit decimal projection");
        return 1;
    }
}
