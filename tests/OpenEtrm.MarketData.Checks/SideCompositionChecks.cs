using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Gas;
using System.Collections.Immutable;

internal static class SideCompositionChecks
{
    internal static int Run()
    {
        var exceptions = new[] { typeof(FuturesContract), typeof(FuturesOptionInstrument) };
        var leaves = typeof(Instrument).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(Instrument).IsAssignableFrom(type)).ToArray();
        // This independently owned workload covers power/REC and the common
        // retained-program family and the current whole Transport-rights owner.
        // Other legacy commodity/cash migrations are separate
        // work; they do not become permanent architectural exceptions.
        var workload=leaves.Where(type=>(type.Namespace==typeof(Instrument).Namespace&&!typeof(CashPaymentInstrument).IsAssignableFrom(type))||
            type.Namespace?.StartsWith("OpenEtrm.MarketData.Power",StringComparison.Ordinal)==true||
            type==typeof(GasTransportDirectRightsInstrument)||
            Ancestors(type).Any(parent=>parent.IsGenericType&&parent.GetGenericTypeDefinition()==typeof(RetainedProgramInstrument<>)));
        var bypasses = workload.Where(type => !exceptions.Contains(type)&&!typeof(SidedInstrument).IsAssignableFrom(type)).ToArray();
        if (bypasses.Length != 0)
            throw new Exception("Every non-listed instrument must compose concrete Sides: " + string.Join(", ", bypasses.Select(type => type.Name)));
        if (exceptions.Any(type => !leaves.Contains(type)))
            throw new Exception("The two explicit exchange-traded leaf exceptions must remain Instrument implementations.");
        ConcurrentAssembly();
        if(!Enum.GetNames<OperatingOutputKind>().Contains("GuaranteedFuelInput")||!Enum.GetNames<OperatingOutputKind>().Contains("ConsumptionFuelInput"))
            throw new Exception("Guaranteed and consumption fuel must occupy independent explicitly named operating quantity ports.");
        FuelPorts();
        Console.WriteLine("PASS instrument composition: power/REC and shared retained-program instruments compose concrete Sides");
        return 1;
    }
    private static IEnumerable<Type> Ancestors(Type type)
    {for(var parent=type.BaseType;parent is not null;parent=parent.BaseType)yield return parent;}
    private static void FuelPorts()
    {
        var f=new Fixture();var plan=new OperatingPlanReference(f.Revision,Guid.NewGuid(),1);var resource=new AssetKey(f.Revision,"synthetic_resource");
        var guaranteed=new OperatingOutputPort(plan,resource,OperatingOutputKind.GuaranteedFuelInput,f.R("gas_quantity"));
        var consumption=new OperatingOutputPort(plan,resource,OperatingOutputKind.ConsumptionFuelInput,f.R("gas_quantity"));
        var interval=new DeliveryInterval(Fixture.Utc("2041-01-01T00:00:00Z"),Fixture.Utc("2041-01-01T01:00:00Z"),new(2041,1,1),0,0,1);
        var inputs=OperatingQuantityInputs.Scenario(f.Snapshot(),[new(guaranteed,interval,ExactQuantity.FromQuantity(new(ExactDecimal.Parse("35"),guaranteed.Quantity))),
            new(consumption,interval,ExactQuantity.FromQuantity(new(ExactDecimal.Parse("43"),consumption.Quantity)))],OperatingQuantityInputs.ScenarioImplementation);
        if(inputs.Resolve(guaranteed,interval).Quantity.ToExactString()!="35"||inputs.Resolve(consumption,interval).Quantity.ToExactString()!="43"||
            (int)OperatingOutputKind.Starts!=7||(int)OperatingOutputKind.GuaranteedFuelInput!=8||(int)OperatingOutputKind.ConsumptionFuelInput!=9)
            throw new Exception("Separate guaranteed/consumption quantities must resolve independently without changing retained port codes.");
    }
    private static void ConcurrentAssembly()
    {
        var f=new Fixture();var quote=f.Quotes[0];var contract=f.Contracts[1];
        var side=new FixedSide(Guid.NewGuid(),new UnitPrice(ExactDecimal.Parse("3"),quote),new(ExactDecimal.Parse("1"),quote.Quantity),
            contract.DeliveryFrom,contract.DeliveryTo,Fixture.Utc("2041-02-02T00:00:00Z"),FixedSide.Implementation,quote,SyntheticSideCalendars.Dated(false));
        using var start=new Barrier(2);using var entered=new Barrier(2);
        var instrument=new DeferredInstrument(side,entered);
        Task<Side> Read()=>Task.Factory.StartNew(()=>{start.SignalAndWait();return instrument.Sides.Single().Side;},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        var first=Read();var second=Read();Task.WaitAll(first,second);
        if(!ReferenceEquals(first.Result,second.Result)||instrument.Assemblies!=1)
            throw new Exception("Concurrent reads must publish one immutable concrete Side assembly without races.");
        var authored=new AuthoredInstrument(side);
        void Refuse(Action action)
        {try{action();}catch(MarketDataException error)when(error.Code=="instrument_definition"){return;}throw new Exception("An authored clone cannot omit its concrete Side membership.");}
        Refuse(()=>{_=authored with{Sides=[]};});
        Refuse(()=>{_=authored with{Sides=[null!]};});
    }
    private sealed record DeferredInstrument(FixedSide Side,Barrier Entry):SidedInstrument(Guid.NewGuid())
    {
        private int assemblies;
        public int Assemblies=>assemblies;
        public override ReferenceKey Currency=>Side.Price.Unit.Currency;
        public override Quantity UnitQuantity=>Side.UnitQuantity;
        protected override ImmutableArray<SideMembership> ComposeSides()
        {
            Interlocked.Increment(ref assemblies);
            Entry.SignalAndWait(TimeSpan.FromMilliseconds(500));
            return [new(Side,ExactDecimal.Parse("1"))];
        }
    }
    private sealed record AuthoredInstrument(FixedSide Side):SidedInstrument(Guid.NewGuid(),[new(Side,ExactDecimal.Parse("1"))])
    {
        public override ReferenceKey Currency=>Side.Price.Unit.Currency;
        public override Quantity UnitQuantity=>Side.UnitQuantity;
    }
}
