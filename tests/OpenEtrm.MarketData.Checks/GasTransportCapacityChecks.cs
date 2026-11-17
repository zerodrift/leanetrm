using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

internal static class GasTransportCapacityChecks
{
    static readonly ReferenceRevision Reference=new(Guid.Parse("b1111111-1111-1111-1111-111111111111"),Guid.Parse("b2222222-2222-2222-2222-222222222222"));
    static readonly ReferenceKey Unit=new(Reference,"synthetic_native_quantity"),A=new(Reference,"synthetic_receipt_a"),B=new(Reference,"synthetic_receipt_b"),D=new(Reference,"synthetic_delivery");
    static readonly Guid First=Guid.Parse("b3333333-3333-3333-3333-333333333333"),Second=Guid.Parse("b4444444-4444-4444-4444-444444444444");
    static readonly DateTimeOffset Start=TradingInstant.Parse("2032-01-15T00:00:00Z","synthetic_time");
    static Quantity Q(string value)=>new(ExactDecimal.Parse(value),Unit);
    static GasTransportCapacitySlice Slice(DateTimeOffset from,DateTimeOffset through,string maximum)=>new(from,through,Q(maximum));
    static GasTransportCapacityProfile Profile(DateTimeOffset through,string agreement="100",string receipt="80",string delivery="100",string route="60")=>new(
        Guid.NewGuid(),GasTransportCapacityProfile.Version,Unit,Start,through,GasTransportCapacityProfile.ElapsedHour,GasTransportCapacityProfile.UniformStages,
        GasTransportCapacityProfile.GrossReceipts,GasTransportCapacityProfile.GrossDeliveries,
        [new(First,A,D),new(Second,B,D)],[Slice(Start,through,agreement)],
        [new(A,[Slice(Start,through,receipt)]),new(B,[Slice(Start,through,receipt)])],
        [new(D,[Slice(Start,through,delivery)])],
        [new(First,[Slice(Start,through,route)]),new(Second,[Slice(Start,through,route)])]);
    static GasTransportCapacityUse Use(Guid route,ReferenceKey receipt,DateTimeOffset from,DateTimeOffset to,string quantity,
        DateTimeOffset? deliveryFrom=null,DateTimeOffset? deliveryTo=null,string? delivered=null)=>new(Guid.NewGuid(),route,receipt,D,from,to,Q(quantity),
            deliveryFrom??from,deliveryTo??to,Q(delivered??quantity));
    static void Invalid(Action action)
    {
        try{action();}catch(MarketDataException error)when(error.Code=="gas_transport_capacity_input"){return;}
        throw new Exception("Missing, conflicting or unsupported transport capacity inputs must refuse explicitly.");
    }

    internal static void Run()
    {
        var end=Start.AddHours(2);var rules=Profile(end);
        var shared=GasTransportCapacity.Assess(rules,[Use(First,A,Start,Start.AddHours(1),"60"),Use(Second,B,Start,Start.AddHours(1),"50")]);
        var exceeded=shared.Intervals.Where(i=>!i.WithinLimit).ToArray();
        if(shared.Feasible||exceeded.Length!=2||!exceeded.Any(i=>i.Axis=="agreement"&&i.UsedPerHour.Numerator=="110")||
            !exceeded.Any(i=>i.Axis=="delivery"&&i.Point==D&&i.UsedPerHour.Numerator=="110"))
            throw new Exception("Alternative paths consume one parent entitlement and their shared delivery-point capacity; their separate route limits cannot duplicate either.");
        try{shared.RequireFeasible();throw new Exception("A voluntary plan with an observed capacity breach cannot be admitted.");}
        catch(MarketDataException error)when(error.Code=="gas_transport_capacity_exceeded"){}
        var separate=GasTransportCapacity.Assess(rules,[Use(First,A,Start,Start.AddHours(1),"60"),Use(Second,B,Start.AddHours(1),end,"60")]);
        if(!separate.Feasible)throw new Exception("Consecutive uses do not overlap at a half-open boundary.");

        // A daily total can fit while its constant rate breaches a later dated limit.
        var changing=Profile(end,"100","100","100","100") with{Agreement=[Slice(Start,Start.AddHours(1),"100"),Slice(Start.AddHours(1),end,"50")]};
        var seasonal=GasTransportCapacity.Assess(changing,[Use(First,A,Start,end,"150")]);
        var later=seasonal.Intervals.Single(i=>i.Axis=="agreement"&&i.From==Start.AddHours(1));
        if(seasonal.Feasible||later.WithinLimit||later.UsedPerHour.Numerator!="75"||later.MaximumPerHour.Numerator!="50")
            throw new Exception("Dated capacity must hold throughout each operating interval, independently of a feasible period total.");

        var delayed=GasTransportCapacity.Assess(Profile(end,"60","60","60","60"),
            [Use(First,A,Start,Start.AddHours(1),"60",Start.AddHours(1),end),Use(Second,B,Start.AddHours(1),end,"60",Start,Start.AddHours(1))]);
        if(!delayed.Feasible||delayed.Intervals.Where(i=>i.Axis=="delivery").Any(i=>i.UsedPerHour.Numerator!="60"))
            throw new Exception("Explicit receipt and delivery clocks consume their own capacity intervals; no transit-time inference or gross netting applies.");
        var fuel=GasTransportCapacity.Assess(Profile(end,"100","100","98","100"),[Use(First,A,Start,Start.AddHours(1),"100",delivered:"98")]);
        if(!fuel.Feasible||fuel.Intervals.Single(i=>i.Axis=="delivery"&&i.From==Start).UsedPerHour.Numerator!="98")
            throw new Exception("Delivery capacity uses declared delivery quantity; the capacity assessment does not invent the independent fuel or loss law.");

        foreach(var hours in new[]{23,25})
        {
            var through=Start.AddHours(hours);var duration=Profile(through,"10","10","10","10");
            if(!GasTransportCapacity.Assess(duration,[Use(First,A,Start,through,(hours*10).ToString())]).Feasible)
                throw new Exception("A captured short or long operating day consumes its actual elapsed duration.");
        }
        if(GasTransportCapacity.Assess(Profile(Start.AddHours(23),"10","10","10","10"),[Use(First,A,Start,Start.AddHours(23),"240")]).Feasible)
            throw new Exception("A nominal 24-hour allowance cannot expand a 23-hour interval.");
        var micro=GasTransportCapacity.Assess(Profile(Start.AddMicroseconds(3),"1200000000","1200000000","1200000000","1200000000"),
            [Use(First,A,Start,Start.AddMicroseconds(3),"1")]);
        if(!micro.Feasible||micro.Intervals.Where(i=>i.Axis=="agreement").Any(i=>i.UsedPerHour.Numerator!="1200000000"||i.UsedPerHour.Denominator!="1"))
            throw new Exception("Subsecond capacity rates retain exact rational arithmetic without rounded durations.");
        var seventh=GasTransportCapacity.Assess(Profile(Start.AddSeconds(7),"514.285714285714285714","514.285714285714285714","514.285714285714285714","514.285714285714285714"),
            [Use(First,A,Start,Start.AddSeconds(7),"1")]);
        var exact=seventh.Intervals.Single(i=>i.Axis=="agreement");
        if(seventh.Feasible||exact.UsedPerHour.Numerator!="3600"||exact.UsedPerHour.Denominator!="7")
            throw new Exception("Rounding an exact repeating rate to the saved decimal limit cannot turn a genuine breach into admissible capacity.");

        // Each one-unit stage lasts more than one second, so 220 stages use
        // less than 220*3600=792000 per hour, below the explicit million limit.
        // Their distinct prime microsecond durations are coprime to 3600000000;
        // no prime cancels from the exact aggregate rate's denominator.
        static bool Prime(int value)
        {
            for(var divisor=2;divisor*divisor<=value;divisor++)if(value%divisor==0)return false;
            return true;
        }
        var primeDurations=Enumerable.Range(1_000_001,10_000).Where(Prime).Take(220).ToArray();
        var denominator=primeDurations.Aggregate(BigInteger.One,(product,duration)=>product*duration);
        if(primeDurations.Length!=220||primeDurations.Min()<=1_000_000||220*3600>=1_000_000||denominator.GetBitLength()<=4096)
            throw new Exception("The numerical-envelope oracle must be physically within capacity while requiring more than the shared exact-rational bound.");
        var envelope=Profile(Start.AddSeconds(2),"1000000","1000000","1000000","1000000");
        ImmutableArray<GasTransportCapacityUse> primeUses=[..primeDurations.Select((duration,index)=>
            Use(First,A,Start,Start.AddMicroseconds(duration),"1") with{Id=Guid.Parse($"b0000000-0000-0000-0000-{index+1:D12}")})];
        if(!GasTransportCapacity.Assess(envelope,[..primeUses.Take(200)]).Feasible)
            throw new Exception("The same prime-duration construction inside the numerical envelope must remain feasible.");
        try{GasTransportCapacity.Assess(envelope,primeUses);throw new Exception("An exact rate outside the supported envelope must refuse instead of rounding or returning partial capacity.");}
        catch(MarketDataException error)when(error.Code=="numeric_range"){}

        var use=Use(First,A,Start,end,"60");
        Invalid(()=>GasTransportCapacity.Assess(rules with{Quantity=default},[use]));
        Invalid(()=>GasTransportCapacity.Assess(rules with{RateBasis=""},[use]));
        Invalid(()=>GasTransportCapacity.Assess(rules with{Agreement=[Slice(Start,end,"-1")]},[use]));
        Invalid(()=>GasTransportCapacity.Assess(rules with{Deliveries=[]},[use]));
        Invalid(()=>GasTransportCapacity.Assess(rules with{Agreement=[Slice(Start,Start.AddHours(1),"100")]},[use]));
        Invalid(()=>GasTransportCapacity.Assess(rules with{Agreement=[Slice(Start,end,"100"),Slice(Start.AddHours(1),end,"100")]},[use]));
        Invalid(()=>GasTransportCapacity.Assess(rules,[use,use]));
        Invalid(()=>GasTransportCapacity.Assess(rules,[use with{ReceiptQuantity=Q("-1")} ]));
        Invalid(()=>GasTransportCapacity.Assess(rules,[use with{ReceiptPoint=D}]));
        Invalid(()=>GasTransportCapacity.Assess(rules,[use with{ReceiptFrom=Start.AddMicroseconds(-1)}]));
        Invalid(()=>GasTransportCapacity.Assess(rules,[use with{ReceiptQuantity=new(ExactDecimal.Parse("1"),new(Reference,"other_native_basis"))}]));
        Console.WriteLine("PASS transport capacity: shared parent/point/route limits, dated intervals, explicit stage clocks, gross quantities and exact short/long durations");
    }
}
