using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class RetainedScheduleLimitChecks
{
    internal static void Run()
    {
        RetainedScheduleLimits.Require(1_500_000);
        Refuse(()=>RetainedScheduleLimits.Require(1_500_001));
        Refuse(()=>RetainedScheduleLimits.Require(0));
        var fixture=new PpaAcceptanceFixture();var side=new FixedSide(Guid.NewGuid(),fixture.Terms(fixture.Profile),
            new FlatPrice(new(ExactDecimal.Parse("40"),fixture.EnergyPrice)));
        var single=new Probe([new(side,ExactDecimal.Parse("1"))]);
        var repeated=new Probe([new(side,ExactDecimal.Parse("1")),new(side,ExactDecimal.Parse("-1"))]);
        // One 721-hour DST month:8 operators,8 edges,4 roots and1 grid.
        if(RetainedScheduleLimits.Require(single,default)!=21||
            RetainedScheduleLimits.Require(repeated,default)!=21)
            throw new Exception("The booking bound counts compact operators and geometry headers once per reusable side.");
        var delivery=fixture.Delivery with {To=fixture.Month.AddMonths(40)};
        var terms=fixture.Terms(fixture.Profile) with {Delivery=delivery,
            Schedule=new(delivery,fixture.Clock,IntervalScheduleCadence.NativeIntervals,fixture.Profile.Hours)};
        var large=new Probe([..new[]{"40","41","42","43"}.Select(strike=>new SideMembership(
            new FixedSide(Guid.NewGuid(),terms,new FlatPrice(new(ExactDecimal.Parse(strike),fixture.EnergyPrice))),ExactDecimal.Parse("1")))]);
        var retained=new RetainedScheduleTerms(fixture.Definitions,fixture.Reference,large).Generate(default);
        if(retained.Schedules.Count!=4||retained.Schedules.Any(s=>s.Rows.Count!=242||s.Grids.Count!=40)||
            RetainedScheduleLimits.Require(retained.Schedules)!=2892)
            throw new Exception("A forty-month term must retain monthly operator blocks rather than an expanded interval DAG.");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try {RetainedScheduleLimits.Require(single,cancelled.Token);}
        catch(OperationCanceledException) {Console.WriteLine("PASS retained booking limit: complete typed work, independent DST counts, shared sides and early cancellation");return;}
        throw new Exception("An abandoned booking must stop its storage preflight.");
    }
    private static void Refuse(Action action)
    {
        try {action();}
        catch(MarketDataException error) when(error.Code=="side_schedule_limit"){return;}
        throw new Exception("Booking must refuse outside the qualified typed-entity bound before allocating its graph.");
    }
    private sealed record Probe: SidedInstrument
    {
        internal Probe(ImmutableArray<SideMembership> sides):base(Guid.NewGuid(),sides) { }
        public override ReferenceKey Currency=>Sides[0].Side.Terms.Currency;
        public override Quantity UnitQuantity=>Sides[0].Side.Terms.UnitQuantity;
    }
}
