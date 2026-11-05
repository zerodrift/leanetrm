using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Trading;

internal static class CurveProjectionShapeChecks
{
    internal static void Run()
    {
        var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(revision,code);
        var unit=new PriceUnit(R("usd"),R("mmbtu"),ExactDecimal.Parse("1"));
        var other=new PriceUnit(R("eur"),R("mmbtu"),ExactDecimal.Parse("1"));
        var projection=new CustomCurveProjectionInput(Guid.NewGuid(),Guid.NewGuid(),1,"output",null,
            CustomCurveProjectionBinding.ContainingDeliverySelection,CustomCurveProjectionBinding.SupportedImplementation);
        var from=new DateOnly(2041,1,1);var to=new DateOnly(2041,2,1);
        if(CustomCurves.ProjectionShapeMatches([new(1,from,to,unit),new(2,from,to,other)],projection,unit,[new(from,from.AddDays(1))]))
            throw new Exception("An all-fixed curve binding hid an ambiguous output by filtering another currency before resolving coverage.");
        if(!CustomCurves.ProjectionShapeMatches([new(1,from,to,unit)],projection,unit,[new(from,from.AddDays(1))]))
            throw new Exception("A compatible whole-interval curve shape was refused.");
        Console.WriteLine("PASS fixed curve qualification: complete-grid ambiguity before unit checking, independent of observation demand");
    }
}
