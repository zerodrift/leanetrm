using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Temporal;

internal static class ObservationScheduleChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action action){action();count++;Console.WriteLine("PASS observation schedules: "+name);}
        void Equal<T>(T actual,T expected){if(!Equals(actual,expected))throw new Exception($"Expected {expected}, got {actual}");}
        void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="swap_averaging"){return;}throw new Exception("Expected swap_averaging");}
        var f=new Fixture();var quote=f.Quotes[0];var one=new Quantity(ExactDecimal.Parse("1"),quote.Quantity);
        var from=new DateOnly(2048,4,3);var to=from.AddDays(7);
        var calendar=new SwapObservationCalendar(f.R("observation_calendar"),DateSet.Create(from.AddDays(-2),to.AddDays(2),[from,from.AddDays(4),from.AddDays(6)]));
        FloatingObservationPlan Plan(FloatingAveraging mode,SwapObservationCalendar? dates=null,ImmutableArray<FloatingObservation> quantities=default)=>
            FloatingObservationPlan.Generate(from,to,mode,quote,dates,quantities.IsDefault?[]:quantities);
        Check("pure six-mode generation retains every calendar-day application before aggregation",()=>
        {
            var observed=Plan(FloatingAveraging.ObservationArithmetic,calendar);
            Equal(string.Join(',',observed.Weights.Select(w=>w.ToString())),"1,1,1");Equal(observed.Applications.Length,3);
            var previous=Plan(FloatingAveraging.CalendarDaysPrevious,calendar);
            Equal(string.Join(',',previous.Weights.Select(w=>w.ToString())),"4,2,1");Equal(previous.Applications.Length,7);
            Equal(string.Join(',',previous.Applications.Select(a=>a.ObservationOrdinal)),"0,0,0,0,1,1,2");
            var next=Plan(FloatingAveraging.CalendarDaysNext,calendar);
            Equal(string.Join(',',next.Weights.Select(w=>w.ToString())),"1,4,2");Equal(next.Applications.Length,7);
            Equal(string.Join(',',next.Applications.Select(a=>a.ObservationOrdinal)),"0,1,1,1,1,2,2");
            Equal(previous.Applications[3].TargetFrom,from.AddDays(3));Equal(previous.Applications[3].TargetTo,from.AddDays(4));
            if(previous.MappingRule==next.MappingRule)throw new Exception("Distinct mapping rules were omitted");
        });
        Check("preceding and following preserve explicit outside-period guards without clamping",()=>
        {
            var start=from.AddDays(1);var end=from.AddDays(4);
            var previous=FloatingObservationPlan.Generate(start,end,FloatingAveraging.CalendarDaysPrevious,quote,calendar,[]);
            Equal(previous.Observations.Single().From,from);Equal(previous.Weights.Single().ToString(),"3");
            var next=FloatingObservationPlan.Generate(start,end,FloatingAveraging.CalendarDaysNext,quote,calendar,[]);
            Equal(next.Observations.Single().From,end);Equal(next.Weights.Single().ToString(),"3");
            Reject(()=>FloatingObservationPlan.Generate(start,end,FloatingAveraging.CalendarDaysPrevious,quote,
                new(f.R("calendar"),DateSet.Create(start,end.AddDays(2),[end])),[]));
            Reject(()=>FloatingObservationPlan.Generate(start,end,FloatingAveraging.CalendarDaysNext,quote,
                new(f.R("calendar"),DateSet.Create(from,end,[from])),[]));
        });
        Check("calendar and positive typed quantity requirements reject missing conflicting or uncovered inputs",()=>
        {
            foreach(var mode in new[]{FloatingAveraging.ObservationArithmetic,FloatingAveraging.CalendarDaysPrevious,FloatingAveraging.CalendarDaysNext})Reject(()=>Plan(mode));
            Reject(()=>Plan(FloatingAveraging.CalendarDayArithmetic,calendar));
            Reject(()=>Plan(FloatingAveraging.ObservationArithmetic,new(f.R("calendar"),DateSet.Create(from.AddDays(1),to,[]))));
            Reject(()=>Plan(FloatingAveraging.QuantityWeighted));
            var quantities=Enumerable.Range(0,7).Select(n=>new FloatingObservation(from.AddDays(n),from.AddDays(n+1),one)).ToImmutableArray();
            Equal(Plan(FloatingAveraging.QuantityWeighted,quantities:quantities).Observations.Length,7);
            foreach(var amount in new[]{"0","-1"})Reject(()=>Plan(FloatingAveraging.QuantityWeighted,quantities:quantities.SetItem(0,quantities[0] with{AveragingQuantity=new(ExactDecimal.Parse(amount),quote.Quantity)})));
            Reject(()=>Plan(FloatingAveraging.QuantityWeighted,quantities:quantities.SetItem(0,quantities[0] with{AveragingQuantity=new(ExactDecimal.Parse("1"),f.R("wrong"))})));
            Reject(()=>Plan(FloatingAveraging.QuantityWeighted,quantities:quantities.SetItem(1,quantities[0])));
            Reject(()=>Plan(FloatingAveraging.QuantityWeighted,quantities:quantities.Reverse().ToImmutableArray()));
            var required=FloatingObservationPlan.RequiredObservations(from,to,FloatingAveraging.QuantityWeighted,quote,null);
            Equal(required.Length,7);if(required.Any(o=>o.AveragingQuantity is not null))throw new Exception("Entry requirements silently supplied quantity weights");
            Reject(()=>FloatingObservationPlan.Generate(new(9999,1,1),new(9999,1,2),FloatingAveraging.CalendarDayArithmetic,quote,null,[]));
        });
        Check("standalone side maps exact applications to the same evaluated payment and price rows",()=>
        {
            var fixture=new ObservationFixture(from,["17","999","999","999","29","999","23"]);
            Reject(()=>fixture.Side(FloatingAveraging.CalendarDayArithmetic,fixture.Projections.Reverse().ToImmutableArray()));
            RejectCode("swap_definition",()=>fixture.Side(FloatingAveraging.CalendarDayArithmetic,fixture.Projections.SetItem(1,fixture.Projections[0])));
            foreach(var (mode,numerator,denominator) in new[]{(FloatingAveraging.ObservationArithmetic,"23","1"),(FloatingAveraging.CalendarDaysPrevious,"149","7"),(FloatingAveraging.CalendarDaysNext,"179","7")})
            {
                var side=fixture.Side(mode,[fixture.Projections[0],fixture.Projections[4],fixture.Projections[6]],new(fixture.Base.R("observation_calendar"),calendar.BusinessDays));
                var schedule=side.GenerateSchedule();var value=schedule.Evaluate(fixture.Context(side));
                if(!ReferenceEquals(value.Schedule,schedule))throw new Exception("Another schedule was evaluated");
                Equal(value.PresentValue.Numerator,numerator);Equal(value.PresentValue.Denominator,denominator);
                var plan=schedule.Payments.Single();var payment=value.Payments.Single();
                Equal(payment.Applications.Length,mode==FloatingAveraging.ObservationArithmetic?3:7);
                foreach(var row in payment.Applications)
                {
                    Equal(row.Application.Payment,plan.Key);
                    if(!ReferenceEquals(row.Application.Price,plan.Prices[row.Application.PriceOrdinal])||!ReferenceEquals(row.Price,payment.Prices[row.Application.PriceOrdinal]))throw new Exception("Applications are display-only copies");
                }
                Equal(payment.Applications.Select(a=>a.CashContribution).Aggregate((a,b)=>a.Add(b)),payment.CashAmount);
            }
        });
        Check("quantity weights retain exact numerator denominator and external scaling",()=>
        {
            var fixture=new ObservationFixture(from,["17","29","23","11"]);
            var quantities=new[]{"1.5","2.5","0.5","3.5"};
            var selected=fixture.Projections.Select((p,n)=>p with{AveragingQuantity=new(ExactDecimal.Parse(quantities[n]),fixture.Quote.Quantity)}).ToImmutableArray();
            var side=fixture.Side(FloatingAveraging.QuantityWeighted,selected);
            var value=side.GenerateSchedule().Evaluate(fixture.Context(side));
            Equal(value.PresentValue.Numerator,"37");Equal(value.PresentValue.Denominator,"2");
            Equal(value.PresentValue.Scale(ExactDecimal.Parse("8")).ToMoney().Amount.ToString(),"148");
            Equal(string.Join(',',value.Payments.Single().Applications.Select(a=>a.Application.Weight.ToString())),"1.5,2.5,0.5,3.5");
            Equal(value.Payments.Single().ObservationWeightTotal.ToString(),"8");
        });
        Check("partial actual weights retain the full denominator while shared scenarios shock only projections",()=>
        {
            var fixture=new ObservationFixture(from,["17","29","23","11"]);
            var at=Fixture.Utc("2048-04-07T16:45:00Z");
            var quantities=new[]{"1.5","2.5","0.5","3.5"};
            var projections=fixture.Projections.Select((p,n)=>p with{AveragingQuantity=new(ExactDecimal.Parse(quantities[n]),fixture.Quote.Quantity)}).ToImmutableArray();
            var fixings=fixture.Projections.Select((p,n)=>new IndexFixing(fixture.Base.R("index"),fixture.Quote.Key,p.Contract.DeliveryFrom,p.Contract.DeliveryTo,
                n is 0 or 2?at:at.AddDays(1),fixture.Series[n].Key,fixture.Base.R("actual_source"),IndexFixing.SupportedImplementation)).ToImmutableArray();
            var side=new FloatingSide(Guid.NewGuid(),fixture.Base.R("index"),fixture.Quote,fixture.Unit,fixture.From,fixture.To,
                fixings,fixture.Payment,FloatingAveraging.QuantityWeighted,projections,FloatingSide.IndexedImplementation);
            var actuals=new[]{0,2}.Select(n=>new SelectedObservation(new(fixture.Series[n].Key,InputUse.FrozenFact),new(new(fixture.Base.Revision.CatalogId,Guid.NewGuid()),
                fixture.Series[n].Key,fixings[n].Source,ObservationRole.IndexFixing,PublicationState.Final,at,ExactDecimal.Parse(n==0?"17":"23")))).ToImmutableArray();
            var snapshot=fixture.Snapshot with{Id=Guid.NewGuid(),ValuationAt=at,MarketCutoff=at,Selections=fixture.Snapshot.Selections.AddRange(actuals)};
            var dependencies=side.DependenciesAt(snapshot);Equal(dependencies.Length,2);
            var market=fixture.Model.Build(snapshot,dependencies);
            PaymentSideValuation Value(CurveBuild build)=>side.GenerateSchedule().Evaluate(new(build,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            var baseline=Value(market);Equal(baseline.PresentValue.Scale(ExactDecimal.Parse("8")).ToMoney().Amount.ToString(),"148");
            var shifts=new[]{1,3}.Select(n=>new QuoteShift(new(fixture.Series[n].Key,InputUse.Variable),fixture.Quote.Key,PriceUnit.FromQuote(fixture.Quote),ExactDecimal.Parse("2.5"),QuoteShift.Convention)).ToImmutableArray();
            var up=Value(fixture.Model.WithShifts(market,shifts));
            Equal(up.PresentValue.Scale(ExactDecimal.Parse("8")).ToMoney().Amount.ToString(),"163");Equal(up.Payments.Single().ObservationWeightTotal.ToString(),"8");
            foreach(var n in new[]{0,2})
            {
                Equal(up.Payments.Single().Prices[n].ExactPrice,baseline.Payments.Single().Prices[n].ExactPrice);
                if(!up.Payments.Single().Prices[n].Lineage.SetEquals(baseline.Payments.Single().Prices[n].Lineage))throw new Exception("A shared scenario changed an actual pin");
            }
            var risk=DealRisk.Calculate(fixture.Model,snapshot,dependencies,build=>Value(build).PresentValue.Scale(ExactDecimal.Parse("8")),DealRisk.BumpPolicy,DealRisk.NumericalPolicy);
            Equal(risk.Factors.Length,2);Equal(risk.ScenarioCount,5);
            foreach(var n in new[]{1,3})
            {
                var factor=risk.Factors.Single(r=>r.Factor.Input.Series==fixture.Series[n].Key);
                Equal(factor.Values.Delta.Value.ToMoney().Amount.ToString(),quantities[n]);Equal(factor.Values.Gamma.Value.Numerator,"0");
            }
            var missing=snapshot with{Selections=snapshot.Selections.Remove(actuals[0])};
            RejectCode("index_fixing_missing",()=>Value(fixture.Model.Build(missing,side.DependenciesAt(missing))));
        });
        Check("inspection retains a large exact weight total without narrowing a valid weighted mean",()=>
        {
            var fixture=new ObservationFixture(from,["1","3"]);
            var projections=fixture.Projections.Select(p=>p with{AveragingQuantity=new(ExactDecimal.Parse("900000000000000000000000"),fixture.Quote.Quantity)}).ToImmutableArray();
            var side=fixture.Side(FloatingAveraging.QuantityWeighted,projections);var value=side.GenerateSchedule().Evaluate(fixture.Context(side));
            Equal(value.PresentValue.ToMoney().Amount.ToString(),"2");
            Equal(value.Payments.Single().ObservationWeightTotal.ToString(),"1800000000000000000000000");
        });
        Check("exact publication boundary preserves actual source finality and known-cutoff guards",()=>
        {
            var fixture=new ObservationFixture(from,["29.5"]);var at=Fixture.Utc("2048-04-07T16:45:00Z");
            var fixing=new IndexFixing(fixture.Base.R("index"),fixture.Quote.Key,from,from.AddDays(1),at,fixture.Series[0].Key,fixture.Base.R("actual_source"),IndexFixing.SupportedImplementation);
            var side=new FloatingSide(Guid.NewGuid(),fixture.Base.R("index"),fixture.Quote,fixture.Unit,from,from.AddDays(1),[fixing],fixture.Payment,FloatingAveraging.CalendarDayArithmetic,fixture.Projections,FloatingSide.IndexedImplementation);
            var actual=new SelectedObservation(new(fixture.Series[0].Key,InputUse.FrozenFact),new(new(fixture.Base.Revision.CatalogId,Guid.NewGuid()),fixture.Series[0].Key,fixing.Source,ObservationRole.IndexFixing,PublicationState.Final,at,ExactDecimal.Parse("28.75")));
            var due=fixture.Snapshot with{Id=Guid.NewGuid(),ValuationAt=at,MarketCutoff=at,Selections=fixture.Snapshot.Selections.Add(actual)};
            PaymentSideValuation Value(MarketSnapshot snapshot)=>side.GenerateSchedule().Evaluate(new(fixture.Model.Build(snapshot,side.DependenciesAt(snapshot)),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            var before=due with{ValuationAt=at.AddTicks(-10),MarketCutoff=at.AddTicks(-10),Selections=fixture.Snapshot.Selections};
            Equal(Value(before).PresentValue.ToMoney().Amount.ToString(),"29.5");Equal(Value(due).PresentValue.ToMoney().Amount.ToString(),"28.75");
            MarketSnapshot Change(Func<MarketObservation,MarketObservation> change)=>due with{Selections=fixture.Snapshot.Selections.Add(actual with{Observation=change(actual.Observation)})};
            RejectCode("index_fixing_selection",()=>Value(Change(o=>o with{Source=fixture.Base.R("other_source")})));
            RejectCode("frozen_fact",()=>Value(Change(o=>o with{State=PublicationState.Preliminary})));
            RejectCode("observation_cutoff",()=>Value(Change(o=>o with{ObservedAt=at.AddTicks(10)})));
        });
        Check("one unchanged side serves two permitted instrument compositions with only membership signs changed",()=>
        {
            var fixture=new ObservationFixture(from,["7","9","9"]);var side=fixture.Side(FloatingAveraging.CalendarDayArithmetic,fixture.Projections);
            var fixedSide=new FixedSide(Guid.NewGuid(),new(ExactDecimal.Parse("7.5"),fixture.Quote),fixture.Unit,from,from.AddDays(3),fixture.Payment,FixedSide.Implementation,fixture.Quote);
            var receive=new FinancialSwapInstrument(Guid.NewGuid(),[new(fixedSide,ExactDecimal.Parse("-1")),new(side,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,false);
            var pay=new FinancialSwapInstrument(Guid.NewGuid(),[new(fixedSide,ExactDecimal.Parse("1")),new(side,ExactDecimal.Parse("-1"))],FinancialSwapInstrument.Implementation,false);
            var context=fixture.Context(side);var before=DefinitionIdentity.For(side);var standalone=side.PV(context);
            Equal(standalone.PresentValue.Numerator,"25");Equal(standalone.PresentValue.Denominator,"3");
            Equal(standalone.PresentValue.Scale(ExactDecimal.Parse("6")).ToMoney().Amount.ToString(),"50");
            Equal(receive.PV(context).PresentValue.Scale(ExactDecimal.Parse("6")).ToMoney().Amount.ToString(),"5");
            Equal(pay.PV(context).PresentValue.Scale(ExactDecimal.Parse("6")).ToMoney().Amount.ToString(),"-5");
            Equal(DefinitionIdentity.For(side),before);Equal(side.PV(context).PresentValue,standalone.PresentValue);
            Equal(receive.Sides[1].Side,pay.Sides[1].Side);
            var negative=new ObservationFixture(from,["-7","9","9"]);var negativeSide=negative.Side(FloatingAveraging.CalendarDayArithmetic,negative.Projections);
            var negativeValue=negativeSide.GenerateSchedule().Evaluate(negative.Context(negativeSide));
            Equal(negativeValue.PresentValue.Numerator,"11");Equal(negativeValue.PresentValue.Denominator,"3");
        });
        Check("monthly fixed price stays one monthly observation rather than a daily average",()=>
        {
            var monthly=new IndexFixingChecks.FixingFixture(monthly:true);var snapshot=monthly.Snapshot(monthly.Fixings[0].FixingAt.AddDays(-2));
            snapshot=snapshot with{Selections=snapshot.Selections.Select(s=>s with{Observation=s.Observation with{Value=ExactDecimal.Parse("47.125")}}).ToImmutableArray()};
            var side=monthly.Floating();var value=side.GenerateSchedule().Evaluate(new(monthly.Model.Build(snapshot,side.DependenciesAt(snapshot)),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            Equal(value.PresentValue.ToMoney().Amount.ToString(),"47.125");Equal(value.Payments.Single().Applications.Length,1);
            var daily=new ObservationFixture(from,["41","52","45","58"]);var dailySide=daily.Side(FloatingAveraging.CalendarDayArithmetic,daily.Projections);
            Equal(dailySide.PV(daily.Context(dailySide)).PresentValue.ToMoney().Amount.ToString(),"49");
        });
        return count;
    }

    private static void RejectCode(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}

    private sealed class ObservationFixture
    {
        public Fixture Base {get;}=new();
        public QuoteSpecification Quote=>Base.Quotes[0];
        public Quantity Unit=>new(ExactDecimal.Parse("1"),Quote.Quantity);
        public DateOnly From{get;} public DateOnly To{get;}
        public DateTimeOffset Payment=>Fixture.Utc("2048-06-18T00:00:00Z");
        public ImmutableArray<SwapProjection> Projections{get;}
        public ImmutableArray<MarketSeries> Series{get;}
        public CurveModel Model{get;}public MarketSnapshot Snapshot{get;}
        public ObservationFixture(DateOnly from,string[] prices)
        {
            From=from;To=from.AddDays(prices.Length);var curve=Base.C("standalone_observations");
            var contracts=prices.Select((_,n)=>new MarketContract(new(Base.Revision,Guid.NewGuid()),ContractKind.Forward,from.AddDays(n),from.AddDays(n+1),Payment,null)).ToImmutableArray();
            var series=contracts.Select(c=>new MarketSeries(new(Base.Revision,Guid.NewGuid()),c.Key,Quote.Key,null)).ToImmutableArray();Series=series;
            var definition=new Curve(curve,Quote.Key,Base.R("location"),Base.R("uniform"),new(SettlementKind.Index,Base.R("index"),null),CurveRule.Direct,contracts.Select(c=>c.Key).ToImmutableArray(),[],ObservationRole.TraderMark,Base.R("source"),null);
            Model=new(Base.Revision,Base.Reference,Base.Quantities,[Quote],contracts,[definition],[],[],series);
            Snapshot=Base.Snapshot() with{Selections=series.Select((s,n)=>new SelectedObservation(new(s.Key,InputUse.Variable),new(new(Base.Revision.CatalogId,Guid.NewGuid()),s.Key,Base.R("source"),ObservationRole.TraderMark,PublicationState.Final,Fixture.Utc("2040-12-18T00:00:00Z"),ExactDecimal.Parse(prices[n])))).ToImmutableArray()};
            Projections=contracts.Select(c=>new SwapProjection(new(curve,c.Key),c)).ToImmutableArray();
        }
        public FloatingSide Side(FloatingAveraging mode,ImmutableArray<SwapProjection> projections,SwapObservationCalendar? calendar=null)=>new(Guid.NewGuid(),Base.R("index"),Quote,Unit,From,To,Fixture.Utc("2048-05-01T00:00:00Z"),Payment,mode,projections,FloatingSide.Implementation,calendar);
        public SideValuationContext Context(FloatingSide side)=>new(Model.Build(Snapshot,side.DependenciesAt(Snapshot)),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
    }
}
