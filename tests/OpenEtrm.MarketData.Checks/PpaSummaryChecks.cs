using System.Collections;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class PpaSummaryChecks
{
    public static int Run()
    {
        var f=new PpaAcceptanceFixture();var instrument=f.Physical();var market=f.Market(f.From.AddDays(-1));
        var inputs=f.Prepare(market,[instrument]);var value=inputs.Session(market.Build).Value(instrument,new(f.From,f.To),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        var measures=ValueMeasures.From(value).Scale(ExactDecimal.Parse("80"));
        var property=typeof(ValueMeasureSet).GetProperty("Periods")??throw new Exception("Missing bounded per-side period summaries.");
        var periods=((IEnumerable)property.GetValue(measures)!).Cast<object>().ToArray();
        if(periods.Length!=2)throw new Exception("November physical and strike require two gross side summaries.");
        ExactMoney Amount(object row)=>(ExactMoney)row.GetType().GetProperty("Amount")!.GetValue(row)!;
        var amounts=periods.Select(Amount).Select(m=>m.Numerator+"/"+m.Denominator).Order().ToArray();
        // November has 721 elapsed native hours including the fall-back hour; 80 MW scales once.
        if(!amounts.SequenceEqual(new[]{"-2307200/1","2422560/1"}))throw new Exception("Gross monthly summaries must preserve exact signed amount and DST hours.");
        Console.WriteLine("PASS PPA period summaries: exact gross sides, membership signs, DST and MW scaling");return 1;
    }
}
