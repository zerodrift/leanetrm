using OpenEtrm.Api;
using OpenEtrm.MarketData;

// What an adjustment of a position states and trades (D424), without a database: its direction and exact quantity are read as
// stated or refused, and it trades on the holding's own side to increase it and on the other side to reduce it, never past it.
internal static class PositionAdjustmentChecks
{
    internal static void Run()
    {
        var passed=0;
        void Check(string name,bool valid){if(!valid)throw new Exception("FAIL position adjustment: "+name);passed++;}
        void Refused(string name,Action act,params string[] reasons)
        {
            try{act();}
            catch(MarketDataException error){Check(name,error.Code=="close_out"&&reasons.All(r=>error.Message.Contains(r,StringComparison.Ordinal)));return;}
            throw new Exception("FAIL position adjustment: "+name+" was not refused");
        }
        string Traded(string held,string direction,string quantity)=>PositionAdjustment.From(direction,quantity).Traded(ExactDecimal.Parse(held),"lots").ToString();

        // The expected lots are the hand-worked trades of a long 40 and a short 15.
        Check("more of a long position is a purchase",Traded("40","increase","10")=="10");
        Check("less of a long position is a sale",Traded("40","reduce","10")=="-10");
        Check("all of a long position is its close-out",Traded("40","reduce","40")=="-40");
        Check("more of a short position is a sale",Traded("-15","increase","5")=="-5");
        Check("less of a short position is a purchase",Traded("-15","reduce","15")=="15");
        Check("an increase is not limited by what is held",Traded("40","increase","400")=="400");
        Check("a fractional quantity is traded as stated",Traded("40","reduce","0.5")=="-0.5");
        Refused("a reduction past a long position",()=>Traded("40","reduce","40.5"),"holds 40 lots","reducing it by 40.5","would reverse it");
        Refused("a reduction past a short position",()=>Traded("-15","reduce","20"),"holds 15 lots","would reverse it");
        Refused("an adjustment of nothing held",()=>Traded("0","increase","1"),"states no quantity");

        var stated=PositionAdjustment.From("reduce","10.50");
        Check("the quantity is echoed as it was stated",stated.Stated=="10.50"&&stated.Direction=="reduce"&&!stated.Increase);
        foreach(var quantity in new string?[]{null,"","0","0.0","000","-5","+5","1e3","1E3","1/3"," 10","10 ","10\n","1,000",".5","5.","0x10","١٠","NaN","-0","1.1234567890123456789","1"+new string('0',24)})
            Refused($"a quantity stated as '{quantity}'",()=>PositionAdjustment.From("increase",quantity),"quantity");
        foreach(var direction in new string?[]{null,"","sell","buy","Increase","increase ","close_out"})
            Refused($"a direction stated as '{direction}'",()=>PositionAdjustment.From(direction,"10"),"increases or reduces");
        Console.WriteLine($"PASS position adjustments: {passed} checks");
    }
}
