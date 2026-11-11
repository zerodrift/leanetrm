using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class PowerAtcShapingChecks
{
    internal static void Run()
    {
        var f=new PpaAcceptanceFixture(month:new(2020,12,1));
        ExactDecimal D(string v)=>ExactDecimal.Parse(v);
        void Money(ExactMoney value,string expected){if(value.ToMoney().Amount.ToString()!=expected)throw new Exception($"Expected {expected}, got {value.ToMoney().Amount}.");}
        var friday=PpaAcceptanceFixture.At("2020-12-18T16:00:00Z");var monday=PpaAcceptanceFixture.At("2020-12-21T16:00:00Z");
        var known=PpaAcceptanceFixture.At("2026-11-11T00:00:00Z");
        var factors=Enumerable.Range(0,24).Select(h=>D(h is >=7 and <15?"2":"1")).ToImmutableArray();
        var instrument=f.Physical(hourlyFactors:factors);
        instrument=RetainedSideSchedules.Attach(instrument,instrument.Memberships.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side)));
        var quote=new QuoteSpecification(f.R("synthetic_energy_quote"),QuoteKind.Outright,f.EnergyPrice.Currency,f.EnergyPrice.Quantity,D("1"),D("0.01"),D("0.01"),D("-10000"),D("10000"));
        var first=new DateOnly(2020,12,19);
        var contracts=Enumerable.Range(0,26).Select(i=>new MarketContract(new(f.Definitions,Guid.NewGuid()),ContractKind.Forward,
            first.AddDays(i/2),first.AddDays(i/2+1),f.Clock.StartOfDay(first.AddDays(i/2)).ToUniversalTime(),null)).ToImmutableArray();
        var series=contracts.Select(c=>new MarketSeries(new(f.Definitions,Guid.NewGuid()),c.Key,quote.Key,null)).ToImmutableArray();
        var curves=Enumerable.Range(0,2).Select(n=>new Curve(f.C(n==0?"synthetic_daily_peak":"synthetic_daily_off"),quote.Key,f.Asset.Location,f.R("synthetic_all_hours"),
            new(SettlementKind.Index,f.R("synthetic_energy_index"),null),CurveRule.Direct,[..contracts.Where((_,i)=>i%2==n).Select(c=>c.Key)],[],ObservationRole.TraderMark,f.R("synthetic_source"),null)).ToImmutableArray();
        var blocks=curves.Select((c,i)=>new IntervalBlockDefinition(c.Key,i==0?BlockMembership.DailyPeak:BlockMembership.DailyOffPeak,null,
            [..contracts.Where((_,n)=>n%2==i)])).ToImmutableArray();
        var grid=new DeliveryGrid(f.Clock,60,[new(2020,12,25)]);
        var curve=new FlatIntervalCurve(f.C("synthetic_energy"),f.EnergyPrice,grid,blocks,FlatIntervalCurve.ReplaceActuals)
        {
            ActualRule=new("interval_end",0,IntervalActualRule.PjmDayAheadReplay){ReleaseClock=f.Clock},
            ActualPrefix=new(f.From,f.Clock.StartOfDay(first).ToUniversalTime()),
            PriceShape=new("synthetic_energy",ShapeMethod.Additive,BlockShaping.CalibrationPolicy,BlockShaping.OperatorVersion,Guid.NewGuid(),grid,blocks)
        };
        var model=new CurveModel(f.Definitions,f.Reference,[f.Energy,f.Capacity],[quote],contracts,curves,[],[],series);
        var shape=f.Hours.Where(h=>h.OperatingDate>=first).Select(h=>new SeriesCell(h.Start,h.End,D(h.HourBeginning is >=7 and <15?"4":"-4"),1)).ToArray();
        ValueMeasureSet Value(DateTimeOffset at,string mark,bool corrected=false,bool flatQuantity=false,int shockSeries=-1,int shockDirection=0)
        {
            var snapshot=new MarketSnapshot(Guid.NewGuid(),f.Definitions,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                [..series.Select(s=>new SelectedObservation(new(s.Key,InputUse.Variable),new(new(f.Definitions.CatalogId,Guid.NewGuid()),s.Key,f.R("synthetic_source"),ObservationRole.TraderMark,PublicationState.Final,at,D(mark))))])
                .WithKnowledge(f.QuantityKnowledge,known);
            var actuals=f.Hours.Select(h=>new IntervalActual(h.Start,h.End,new(D(h.OperatingDate==new DateOnly(2020,12,18)&&h.HourBeginning is >=7 and <23
                ?corrected&&h.HourBeginning==7?"21":"20":"30"),f.EnergyPrice),known,Guid.NewGuid(),corrected?2:1)).ToArray();
            var prepared=PreparedCurve.Prepare(curve,[(f.From,f.To)],shape,actuals,at,known);
            var dependencies=prepared.Dependencies(f.Month);
            var baseBuild=model.Build(snapshot,dependencies);var build=baseBuild;
            if(shockSeries>=0)
            {
                var input=new InputKey(series[shockSeries].Key,InputUse.Variable);
                var bump=new OpenEtrm.MarketData.Risk.QuoteBump(input,quote.Key,PriceUnit.FromQuote(quote),D("1"),OpenEtrm.MarketData.Risk.QuoteBump.Convention);
                var scenario=model.BuildScenarios(snapshot,dependencies,[bump]).Factors.Single();
                build=shockDirection>0?scenario.Up:scenario.Down;
            }
            var market=new PpaAcceptanceFixture.MarketInputs(model,snapshot,baseBuild,new Dictionary<CurveKey,PreparedCurve>{{curve.Key,prepared}},ImmutableDictionary<string,OpenEtrm.MarketData.Risk.QuoteBump>.Empty);
            var selected=flatQuantity?f.Physical():instrument;
            return ValueMeasures.From(f.Prepare(market,[selected]).Session(build).Value(selected,new(f.From,f.To),new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted)));
        }
        var a=Value(friday,"52");var b=Value(monday,"53");
        Money(a.ValueExcludingOverdue,"-592");Money(b.ValueExcludingOverdue,"-2480");
        var change=PpaContractualChange.Calculate(a,b);
        Money(change.Change,"-1888");Money(change.Fixing,"-2208");Money(change.ForwardMarks,"320");Money(change.CaptureCorrections,"0");
        if(change.OpeningActualIntervals!=432||change.ClosingActualIntervals!=504||change.FixingIntervals!=72||change.RemainingForwardIntervals!=240)throw new Exception("ATC includes Saturday and Sunday fixing.");
        foreach(var value in new[]{a,b})
        {
            var cash=value.Rows.Where(r=>r.Kind==ComponentKind.Payment).ToArray();
            if(cash.Length!=744||cash.Aggregate(new ExactQuantity(0,1,f.Energy.Key),(sum,r)=>sum.Add(r.Quantity)).ToQuantity().Amount.ToString()!="992")throw new Exception("Linked consideration lost shaped volume.");
            Money(cash.Aggregate(ExactMoney.FromMoney(new(default,f.EnergyPrice.Currency)),(sum,r)=>sum.Add(r.Amount)),"-39680");
        }
        var correction=PpaContractualChange.Calculate(a,Value(monday,"53",true));
        Money(correction.Change,"-1886");Money(correction.CaptureCorrections,"2");
        // Shaping prices preserves every source's statistic, independent of the
        // consuming quantity. A flat ATC quantity has no shape premium.
        var flat=Value(friday,"52",flatQuantity:true);Money(flat.ValueExcludingOverdue,"-736");
        foreach(var(index,delta)in new[]{(6,"24"),(7,"8")})
        {
            var up=Value(monday,"53",shockSeries:index,shockDirection:1);
            var down=Value(monday,"53",shockSeries:index,shockDirection:-1);
            if(up.ValueExcludingOverdue.Subtract(b.ValueExcludingOverdue).ToMoney().Amount!=D(delta)||
               b.ValueExcludingOverdue.Subtract(down.ValueExcludingOverdue).ToMoney().Amount!=D(delta))
                throw new Exception("Generic Side scenarios lost shaped quantity exposure.");
        }
        Money(Value(monday,"54").ValueExcludingOverdue.Subtract(b.ValueExcludingOverdue),"320");
        Console.WriteLine("PASS shaped ATC: retained992MWh, cash39680, Fri-592/Mon-2480, change-1888=-2208fixing+320marks, correction+2 and flat-quantity calibration");
    }
}
