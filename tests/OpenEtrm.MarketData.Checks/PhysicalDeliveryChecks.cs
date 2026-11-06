using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class PhysicalDeliveryChecks
{
    internal static void Contract(BookedSideProgram program,ExactRatio scale,int count,string quantity,DateTimeOffset start,DateTimeOffset end)
    {
        var rows=PhysicalDeliverySchedule.ReadLiteralQuantities(program,scale);
        if(rows.Length!=count||rows[0].Start!=start||rows[^1].End!=end||rows.Any(r=>r.Quantity.ToExactString()!=quantity)||
            rows.Select(r=>(r.Root,r.Grid,r.Cell)).Distinct().Count()!=count)
            throw new Exception("Physical delivery must retain exact schedule geometry, source addresses and one absolute quantity scale.");
        if(rows.Any(r=>r.Quantity.Specification!=program.Document.Instructions[r.Instruction].Output.Unit))
            throw new Exception("A delivery quantity lost its exact retained specification.");
        Console.WriteLine($"PASS physical delivery projection: {program.Document.Family}, {count} retained intervals and exact booked quantities");
    }
}
