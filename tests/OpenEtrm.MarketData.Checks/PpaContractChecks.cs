using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class PpaContractChecks
{
    public static int Run()
    {
        var from=DateTimeOffset.Parse("2031-11-01T04:00:00Z");var to=DateTimeOffset.Parse("2031-12-01T05:00:00Z");
        var coverage=new DeliveryCoverage(from,to);
        if(!coverage.Contains(from,from.AddHours(1)) || coverage.Contains(to,to.AddHours(1)))throw new Exception("Coverage must be half open and retain whole native rows.");
        Reject("ppa_segment_coverage",()=>new DeliveryCoverage(to,from));
        Reject("ppa_segment_coverage",()=>new DeliveryCoverage(from.ToOffset(TimeSpan.FromHours(-4)),to));
        var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        var unit=new PriceUnit(new(reference,"usd"),new(reference,"energy"),ExactDecimal.Parse("1"));
        var steps=new SteppedPrice([new(new(2031,1,1),new(ExactDecimal.Parse("40"),unit)),new(new(2031,11,15),new(ExactDecimal.Parse("50"),unit))]);
        if(steps.At(new(2031,11,14)).Numerator!="40" || steps.At(new(2031,11,15)).Numerator!="50")throw new Exception("Strike step switches exactly on its explicit date.");
        Reject("ppa_price_rule",()=>steps.At(new(2030,12,31)));
        var fixture=new PpaAcceptanceFixture();var original=fixture.Physical();
        PpaInstrument WithOrdinal(int ordinal)
        {
            var deliverySlot=new SideSlot(PpaRole.EnergyDelivery,ordinal);
            var cash=(PpaFixedSide)original.Memberships[1].Side;
            var members=original.Memberships.SetItem(0,original.Memberships[0] with {Slot=deliverySlot})
                .SetItem(1,original.Memberships[1] with {Side=new PpaFixedSide(Guid.NewGuid(),cash.Terms with {Quantity=new SlotQuantity([deliverySlot])},cash.Price)});
            return new(Guid.NewGuid(),original.Definitions,original.Reference,members,original.NormalizationAssets,false,PpaInstrument.Implementation);
        }
        if(WithOrdinal(7).Capability!="fixed_physical")throw new Exception("The highest stored slot ordinal must remain supported.");
        Reject("ppa_side",()=>WithOrdinal(8));
        Console.WriteLine("PASS PPA contracts: native slot ordinals match durable storage bounds");
        Console.WriteLine("PASS PPA contracts: coverage identity and explicit strike steps");return 2;
    }
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
