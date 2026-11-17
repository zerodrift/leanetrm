using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;

internal static class StorageFacilityPresentationChecks
{
    internal static void Run()
    {
        var retained=Guid.NewGuid();var current=Guid.NewGuid();var from=DateTimeOffset.Parse("2032-03-14T08:00:00Z");
        var quantity=new ReferenceKey(new(Guid.NewGuid(),Guid.NewGuid()),"synthetic_quantity");
        Quantity Q(string value)=>new(ExactDecimal.Parse(value),quantity);
        var rules=new StorageFacilityRules(retained,StorageFacilityRules.Version,quantity,[Guid.NewGuid()],Q("0"),Q("1000"),
            [new(Q("0"),Q("1000"),Q("25"),Q("25"))],
            StorageFacilityRules.TotalPhysical,StorageFacilityRules.ElapsedHour,StorageFacilityRules.SeparateDirections,StorageFacilityRules.NoLoss);
        var coverage=new StorageFacilityCoverage(Guid.NewGuid(),from,from.AddHours(1),"measured",StorageFacilityCoverage.CompleteActual);
        var assessment=StorageFacilityOperations.Assess(rules,new(Guid.NewGuid(),from,Q("100")),from,from.AddHours(1),
            [new(Guid.NewGuid(),rules.Ports[0],from,from.AddHours(1),"inject",Q("50"))],coverage);
        var oldBand=new{lower="0",upper="1000",injectionPerHour="25",withdrawalPerHour="25"};
        var facts=JsonSerializer.SerializeToElement(new{
            version=new{id=current,revision=2,quantity_code="synthetic_quantity",minimum="0",maximum="1000",clock_code="synthetic_clock"},
            ratchets=new[]{new{lower="0",upper="1000",injectionPerHour="1000",withdrawalPerHour="1000"}},
            versionBundles=new object[]{new{version=new{id=retained,revision=1},ports=Array.Empty<object>(),ratchets=new[]{oldBand}}},
            openings=Array.Empty<object>(),activities=Array.Empty<object>(),planActivities=Array.Empty<object>(),coverages=Array.Empty<object>(),
            planAssessment=(object?)null,plan=(object?)null,requiresReassessment=false,linkedSourceCorrection=false});
        var tables=JsonSerializer.SerializeToElement(StorageFacilityStore.ReadTables(facts,assessment),GasOwnedOperations.Json);
        var pinned=tables.EnumerateArray().SingleOrDefault(t=>t.GetProperty("label").GetString()=="Measured-window ratchets · revision 1");
        if(pinned.ValueKind!=JsonValueKind.Object||pinned.GetProperty("rows")[0][2].GetString()!="25")
            throw new Exception("A measured result must display its exact retained25/hour rule rather than the current1000/hour rule.");
        if(!tables.EnumerateArray().Any(t=>t.GetProperty("label").GetString()=="Current-definition ratchets · revision 2"))
            throw new Exception("Current-definition ratchets need a distinct version label.");
        var window=tables.EnumerateArray().SingleOrDefault(t=>t.GetProperty("label").GetString()=="Measured inventory window");
        if(window.ValueKind!=JsonValueKind.Object||window.GetProperty("rows")[0][2].GetString()!="150"||
            !window.GetProperty("instantColumns").EnumerateArray().Select(v=>v.GetInt32()).SequenceEqual(new[]{0,1}))
            throw new Exception("Measured closing150 must retain its exact dated window and local-time presentation metadata.");
        var intervals=JsonSerializer.SerializeToElement(StorageFacilityStore.AssessmentTables(assessment),GasOwnedOperations.Json);
        if(intervals[0].GetProperty("rows")[0][6].GetString()!="Injection rate exceeded")
            throw new Exception("An assessed physical breach must have a readable operating explanation.");
    }
}
