using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Physical;

public sealed record PhysicalDeliveryInterval(string Root,int Instruction,int Grid,int Cell,DateOnly OperatingDate,
    DateTimeOffset Start,DateTimeOffset End,ExactQuantity Quantity);

// A projection of booked quantity outputs, never a replacement calendar or a PV
// evaluation. The adapter explicitly identifies physical memberships. Dynamic
// quantity outputs require their own resolved-input adapter, not a literal fallback.
public static class PhysicalDeliverySchedule
{
    public static ImmutableArray<PhysicalDeliveryInterval> ReadLiteralQuantities(BookedSideProgram program,ExactRatio scale)
    {
        if(program is null||scale is null||scale.Numerator<=0)throw Invalid("Supply a retained program and its positive absolute booked membership scale.");
        var rows=ImmutableArray.CreateBuilder<PhysicalDeliveryInterval>();
        foreach(var root in program.Document.Roots.Where(r=>r.Measure=="schedule_quantity"))
        {
            var output=program.Document.Instructions[root.Row].Output;
            if(output.Operation!="quantity_literal"||output.Grid is not {} grid||output.Unit is not {} unit)
                throw Invalid("This delivery adapter requires explicit retained literal quantity outputs; dynamic inputs need a resolved adapter.");
            var quantity=new ExactQuantity(BigInteger.Parse(output.Parameters["numerator"]),BigInteger.Parse(output.Parameters["denominator"]),unit).Scale(scale);
            if(BigInteger.Parse(quantity.Numerator)<0)throw Invalid("Physical delivery quantities are absolute totals with a separate direction.");
            var geometry=program.Document.Grids[grid];
            for(var cell=0;cell<geometry.CellCount;cell++)
            {
                var interval=geometry.Interval(cell);
                rows.Add(new(root.Name,root.Row,grid,cell,interval.OperatingDate,interval.Start,interval.End,quantity));
            }
        }
        if(rows.Count==0||rows.GroupBy(r=>(r.Start,r.End)).Any(g=>g.Count()!=1))
            throw Invalid("A physical membership requires distinct explicit retained delivery intervals.");
        return [..rows.OrderBy(r=>r.Start)];
    }
    private static MarketDataException Invalid(string message)=>new("physical_delivery_schedule",message);
}
