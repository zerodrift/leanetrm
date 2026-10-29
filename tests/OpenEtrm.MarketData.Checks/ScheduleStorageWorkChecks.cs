using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class ScheduleStorageWorkChecks
{
    internal static int Run()
    {
        var dated = new CommodityLookupFixture(2, monthly: true).Swap(monthly: true, monthOrdinal: 0);
        var work = ScheduleDefinitionCompiler.Estimate(dated.Fixed);
        if (work != new ScheduleDefinitionWork(9, 9, 0, 3, 0))
            throw new Exception("One fixed payment stores nine logical rows, nine dependencies and three named outputs.");
        var fixture = new PpaAcceptanceFixture();
        var side = new FixedSide(Guid.NewGuid(), fixture.Terms(fixture.Profile),
            new FlatPrice(new(ExactDecimal.Parse("40"), fixture.EnergyPrice)));
        work = ScheduleDefinitionCompiler.Estimate(side);
        // The synthetic DST month has721 cells. Profile, strike, amount, discount,
        // PV, guard, cell reduction and side sum are eight retained operators.
        if (work != new ScheduleDefinitionWork(8, 8, 0, 4, 0){Grids=1,GridCells=721})
            throw new Exception("Storage work must count compact operators separately from exact retained interval cells.");
        foreach (var member in new[] { fixture.Physical(), fixture.WithCertificate(), fixture.CapacityAgreement() }
            .SelectMany(instrument => instrument.Sides))
        {
            var predicted = ScheduleDefinitionCompiler.Estimate(member.Side);
            var stored = ScheduleDefinitionCompiler.Generate(member.Side);
            if (predicted != new ScheduleDefinitionWork(stored.Rows.Count, stored.Edges.Count,
                    stored.Bindings.Count, stored.Roots.Count, stored.Ports.Count){Grids=stored.Grids.Count,GridCells=stored.Grids.Sum(grid=>(long)grid.CellCount)})
                throw new Exception($"The storage preflight count differs from the complete {member.Side.GetType().Name} artifact.");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        RejectCancelled(() => ScheduleDefinitionCompiler.Estimate(side, cancelled.Token));
        RejectCancelled(() => ScheduleDefinitionCompiler.Generate(side, cancelled.Token));
        Console.WriteLine("PASS retained storage work: explicit fixed-payment and DST profile logical-row counts");
        return 2;
    }
    private static void RejectCancelled(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("An abandoned booking must stop before constructing or counting its retained pricing graph.");
    }
}
