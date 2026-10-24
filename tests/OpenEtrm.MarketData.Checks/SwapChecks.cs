using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

internal static class SwapChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action action){action();count++;Console.WriteLine("PASS swaps: "+name);}
        void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}, got {actual}");}
        void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var f=new Fixture();var quote=f.Quotes[0];var unit=new Quantity(ExactDecimal.Parse("1"),quote.Quantity);
        var contract=f.Contracts[1];var at=f.Snapshot().ValuationAt;
        var fixing=Fixture.Utc("2040-12-19T00:00:00Z");var payment=Fixture.Utc("2041-02-02T12:00:00Z");
        var fixedSide=new FixedSide(Guid.NewGuid(),new UnitPrice(ExactDecimal.Parse("3"),quote),unit,contract.DeliveryFrom,contract.DeliveryTo,payment,FixedSide.Implementation,quote);
        var floating=new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,contract.DeliveryFrom,contract.DeliveryTo,fixing,payment,
            FloatingAveraging.MonthlyIndexPrice,[new(new(f.C("base_forward"),contract.Key),contract)],FloatingSide.Implementation);
        var swap=new FinancialSwapInstrument(Guid.NewGuid(),[new(fixedSide,ExactDecimal.Parse("-1")),new(floating,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation, false);
        var snapshot=f.Snapshot();var market=f.Model().Build(snapshot,[new(f.C("base_forward"),contract.Key)]);
        Check("financial swap inherits instrument and retains distinct reusable fixed and floating sides",()=>{
            SidedInstrument common=swap;Side a=fixedSide,b=floating;
            if(common.Sides.Length!=2||common.Sides[0].Side!=a||common.Sides[1].Side!=b)throw new Exception("Lost memberships");
            if(fixedSide.Dependencies.Length!=0||floating.Dependencies.Length!=1)throw new Exception("Wrong dependencies");
        });
        Check("pay fixed receive floating sums signed side values then scales physical quantity once",()=>{
            var result=swap.Price(market,new Quantity(ExactDecimal.Parse("100"),quote.Quantity),ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted);
            Equal(result.Amount.ToString(),"10");
            var reversed=new FinancialSwapInstrument(Guid.NewGuid(),[new(fixedSide,ExactDecimal.Parse("1")),new(floating,ExactDecimal.Parse("-1"))],FinancialSwapInstrument.Implementation, false);
            Equal(reversed.Price(market,new Quantity(ExactDecimal.Parse("100"),quote.Quantity),ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted).Amount.ToString(),"-10");
        });
        Check("common explicit factor values each side independently before signed aggregation",()=>Equal(new FinancialSwapInstrument(Guid.NewGuid(),swap.Sides,FinancialSwapInstrument.Implementation,true).Price(market,new Quantity(ExactDecimal.Parse("100"),quote.Quantity),ExactDecimal.Parse("0.95"),FinancialSwapInstrument.Discounted).Amount.ToString(),"9.5"));
        Check("missing pricing policy quantity unit or discount rule cannot become a value",()=>{
            Reject("swap_valuation",()=>swap.Price(market,unit,ExactDecimal.Parse("1"),""));
            Reject("swap_valuation",()=>swap.Price(market,unit,ExactDecimal.Parse("0.95"),FinancialSwapInstrument.Undiscounted));
            Reject("swap_quantity",()=>swap.Price(market,new Quantity(ExactDecimal.Parse("100"),f.R("wrong_quantity")),ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted));
            Reject("swap_quantity",()=>swap.Price(market,new Quantity(ExactDecimal.Parse("-1"),quote.Quantity),ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted));
        });
        Check("instrument PV and explicit pricing reject conflicting discount policies even at factor one",()=>{
            Instrument plain=swap;
            Instrument discounted=new FinancialSwapInstrument(Guid.NewGuid(),swap.Sides,FinancialSwapInstrument.Implementation,true);
            var explicitOne=new SideValuationContext(market,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Explicit));
            var none=new SideValuationContext(market,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
            Reject("swap_valuation",()=>plain.PV(explicitOne));
            Reject("swap_valuation",()=>discounted.PV(none));
            Reject("swap_valuation",()=>swap.Price(market,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Discounted));
            Equal(discounted.PV(explicitOne).PresentValue.ToMoney().Amount.ToString(),"0.1");
        });
        Check("unprocessed monthly swap cannot silently cross its declared first fixing",()=>{
            var later=snapshot with {Id=Guid.NewGuid(),ValuationAt=fixing};
            // A live monthly forecast is not evidence that the fixing occurred.
            var build=f.Model().Build(later,[new(f.C("base_forward"),contract.Key)]);
            Reject("swap_fixing_required",()=>swap.Price(build,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted));
        });
        Check("calendar-day average requires exact daily coverage instead of inventing daily prices from monthly marks",()=>Reject("swap_averaging",()=>new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,contract.DeliveryFrom,contract.DeliveryTo,fixing,payment,FloatingAveraging.CalendarDayArithmetic,[new(new(f.C("base_forward"),contract.Key),contract)],FloatingSide.Implementation)));
        Check("daily average is not rounded before trade quantity makes the net result representable",()=>{
            var daily=DailyFixture.Create(f,quote,["1","2","2"]);
            var floatDaily=new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,daily.From,daily.To,fixing,payment,FloatingAveraging.CalendarDayArithmetic,daily.Projections,FloatingSide.Implementation);
            var fixedDaily=new FixedSide(Guid.NewGuid(),new UnitPrice(ExactDecimal.Parse("1"),quote),unit,daily.From,daily.To,payment,FixedSide.Implementation,quote);
            var instrument=new FinancialSwapInstrument(Guid.NewGuid(),[new(fixedDaily,ExactDecimal.Parse("-1")),new(floatDaily,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation, false);
            Equal(instrument.Price(daily.Market,new Quantity(ExactDecimal.Parse("3"),quote.Quantity),ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted).Amount.ToString(),"2");
            Reject("precision_loss",()=>instrument.Price(daily.Market,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted));
        });
        Check("averaging distinguishes observation days from previous and next calendar-day weighting",()=>{
            var daily=DailyFixture.Create(f,quote,["1","2","4"]);
            var calendar=new SwapObservationCalendar(f.R("calendar"),DateSet.Create(daily.From,daily.To,[daily.From,daily.From.AddDays(2)]));
            FinancialSwapInstrument Make(FloatingAveraging rule,ImmutableArray<SwapProjection> selected,SwapObservationCalendar? dates)=>new(Guid.NewGuid(),[
                new(new FixedSide(Guid.NewGuid(),new UnitPrice(default,quote),unit,daily.From,daily.To,payment,FixedSide.Implementation,quote),ExactDecimal.Parse("-1")),
                new(new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,daily.From,daily.To,fixing,payment,rule,selected,FloatingSide.Implementation,dates),ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation, false);
            var selected=ImmutableArray.Create(daily.Projections[0],daily.Projections[2]);
            Equal(Make(FloatingAveraging.ObservationArithmetic,selected,calendar).Price(daily.Market,new Quantity(ExactDecimal.Parse("6"),quote.Quantity),ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted).Amount.ToString(),"15");
            Equal(Make(FloatingAveraging.CalendarDaysPrevious,selected,calendar).Price(daily.Market,new Quantity(ExactDecimal.Parse("6"),quote.Quantity),ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted).Amount.ToString(),"12");
            Equal(Make(FloatingAveraging.CalendarDaysNext,selected,calendar).Price(daily.Market,new Quantity(ExactDecimal.Parse("6"),quote.Quantity),ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted).Amount.ToString(),"18");
            Reject("swap_averaging",()=>Make(FloatingAveraging.ObservationArithmetic,selected,null));
            Reject("swap_averaging",()=>Make(FloatingAveraging.CalendarDayArithmetic,selected,calendar));
            Reject("swap_averaging",()=>Make(FloatingAveraging.ObservationArithmetic,[daily.Projections[0]],calendar));
            Reject("swap_averaging",()=>Make(FloatingAveraging.CalendarDaysPrevious,selected,new(f.R("calendar"),DateSet.Create(daily.From,daily.To,[daily.From.AddDays(2)]))));
        });
        Check("quantity weighting requires explicit compatible positive quantities on every daily member",()=>{
            var daily=DailyFixture.Create(f,quote,["1","2","4"]);
            FloatingSide Floating(ImmutableArray<SwapProjection> projections)=>new(Guid.NewGuid(),f.R("index"),quote,unit,daily.From,daily.To,fixing,payment,FloatingAveraging.QuantityWeighted,projections,FloatingSide.Implementation);
            var selected=daily.Projections.Select((p,n)=>p with {AveragingQuantity=new Quantity(ExactDecimal.Parse(n==2?"2":"1"),quote.Quantity)}).ToImmutableArray();
            var instrument=new FinancialSwapInstrument(Guid.NewGuid(),[new(new FixedSide(Guid.NewGuid(),new UnitPrice(default,quote),unit,daily.From,daily.To,payment,FixedSide.Implementation,quote),ExactDecimal.Parse("-1")),new(Floating(selected),ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation, false);
            Equal(instrument.Price(daily.Market,new Quantity(ExactDecimal.Parse("4"),quote.Quantity),ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted).Amount.ToString(),"11");
            Reject("swap_averaging",()=>Floating(daily.Projections));
            Reject("swap_averaging",()=>Floating(selected.SetItem(0,selected[0] with {AveragingQuantity=new Quantity(ExactDecimal.Parse("1"),f.R("wrong"))})));
            Reject("swap_averaging",()=>Floating(selected.SetItem(0,selected[0] with {AveragingQuantity=new Quantity(default,quote.Quantity)})));
        });
        Check("daily averaging rejects unsupported delivery spans before allocating a calendar",()=>Reject("swap_definition",()=>new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,new(2041,1,1),new(2043,1,1),fixing,Fixture.Utc("2043-02-01T00:00:00Z"),FloatingAveraging.CalendarDayArithmetic,[new(new(f.C("base_forward"),contract.Key),contract)],FloatingSide.Implementation)));
        Check("model-owned index contracts and quote definitions cannot be reinterpreted under retained keys",()=>{
            FinancialSwapInstrument With(FloatingSide side)=>new(Guid.NewGuid(),[new(fixedSide,ExactDecimal.Parse("-1")),new(side,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation, false);
            FloatingSide Side(ReferenceKey index,QuoteSpecification q,MarketContract c)=>new(Guid.NewGuid(),index,q,unit,c.DeliveryFrom,c.DeliveryTo,fixing,payment,FloatingAveraging.MonthlyIndexPrice,[new(new(f.C("base_forward"),c.Key),c)],FloatingSide.Implementation);
            Reject("swap_definition",()=>With(Side(f.R("other_index"),quote,contract)).Price(market,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted));
            Reject("swap_definition",()=>With(Side(f.R("index"),quote with {TradingTick=ExactDecimal.Parse("0.001")},contract)).Price(market,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted));
            Reject("swap_definition",()=>With(Side(f.R("index"),quote,contract with {Expiry=contract.Expiry.AddDays(1)})).Price(market,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted));
            Reject("swap_context",()=>swap.Price(null!,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted));
        });
        Check("negative prices quote scaling and factors above one preserve explicit signed economics",()=>{
            var scaled=quote with {Multiplier=ExactDecimal.Parse("0.01")};var daily=DailyFixture.Create(f,scaled,["-2"]);
            var floatSide=new FloatingSide(Guid.NewGuid(),f.R("index"),scaled,unit,daily.From,daily.To,fixing,payment,FloatingAveraging.CalendarDayArithmetic,daily.Projections,FloatingSide.Implementation);
            var fixedNegative=new FixedSide(Guid.NewGuid(),new UnitPrice(ExactDecimal.Parse("-3"),PriceUnit.FromQuote(scaled)),unit,daily.From,daily.To,payment,FixedSide.Implementation,scaled);
            FinancialSwapInstrument Make(string sign)=>new(Guid.NewGuid(),[new(fixedNegative,ExactDecimal.Parse(sign)),new(floatSide,ExactDecimal.Parse(sign=="-1"?"1":"-1"))],FinancialSwapInstrument.Implementation, true);
            Equal(Make("-1").Price(daily.Market,new Quantity(ExactDecimal.Parse("100"),quote.Quantity),ExactDecimal.Parse("1.1"),FinancialSwapInstrument.Discounted).Amount.ToString(),"1.1");
            Equal(Make("1").Price(daily.Market,new Quantity(ExactDecimal.Parse("100"),quote.Quantity),ExactDecimal.Parse("1.1"),FinancialSwapInstrument.Discounted).Amount.ToString(),"-1.1");
            Reject("swap_valuation",()=>Make("1").Price(daily.Market,unit,default,FinancialSwapInstrument.Discounted));
            Reject("swap_valuation",()=>Make("1").Price(daily.Market,unit,ExactDecimal.Parse("-1"),FinancialSwapInstrument.Discounted));
        });
        Check("large opposing legs net before quantity and discount bounds are checked",()=>{
            var wide=quote with {Minimum=ExactDecimal.Parse("-999999999999999999999999"),Maximum=ExactDecimal.Parse("999999999999999999999999")};
            var daily=DailyFixture.Create(f,wide,["999999999999999999999999"]);
            var a=new FixedSide(Guid.NewGuid(),new UnitPrice(ExactDecimal.Parse("999999999999999999999998"),wide),unit,daily.From,daily.To,payment,FixedSide.Implementation,quote);
            var b=new FloatingSide(Guid.NewGuid(),f.R("index"),wide,unit,daily.From,daily.To,fixing,payment,FloatingAveraging.CalendarDayArithmetic,daily.Projections,FloatingSide.Implementation);
            var instrument=new FinancialSwapInstrument(Guid.NewGuid(),[new(a,ExactDecimal.Parse("-1")),new(b,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation, true);
            Equal(instrument.Price(daily.Market,new Quantity(ExactDecimal.Parse("100"),quote.Quantity),ExactDecimal.Parse("0.95"),FinancialSwapInstrument.Discounted).Amount.ToString(),"95");
        });
        Check("quantity-weighted repeating prices retain rational netting through quantity and discounting",()=>{
            var daily=DailyFixture.Create(f,quote,["1","2"]);
            var projections=daily.Projections.Select((p,n)=>p with {AveragingQuantity=new Quantity(ExactDecimal.Parse(n==0?"1":"2"),quote.Quantity)}).ToImmutableArray();
            var a=new FixedSide(Guid.NewGuid(),new UnitPrice(ExactDecimal.Parse("1"),quote),unit,daily.From,daily.To,payment,FixedSide.Implementation,quote);
            var b=new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,daily.From,daily.To,fixing,payment,FloatingAveraging.QuantityWeighted,projections,FloatingSide.Implementation);
            var instrument=new FinancialSwapInstrument(Guid.NewGuid(),[new(a,ExactDecimal.Parse("-1")),new(b,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation, true);
            Equal(instrument.Price(daily.Market,new Quantity(ExactDecimal.Parse("3"),quote.Quantity),ExactDecimal.Parse("0.5"),FinancialSwapInstrument.Discounted).Amount.ToString(),"1");
            Reject("precision_loss",()=>instrument.Price(daily.Market,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Discounted));
        });
        Check("calendar weighting can use covered observations outside delivery without inventing an endpoint price",()=>{
            var daily=DailyFixture.Create(f,quote,["1","2","3","4"]);
            var from=daily.From.AddDays(1);var to=daily.From.AddDays(3);
            var calendar=new SwapObservationCalendar(f.R("calendar"),DateSet.Create(daily.From,daily.To,[daily.From,daily.From.AddDays(3)]));
            FinancialSwapInstrument Make(FloatingAveraging rule,int n)=>new(Guid.NewGuid(),[
                new(new FixedSide(Guid.NewGuid(),new UnitPrice(default,quote),unit,from,to,payment,FixedSide.Implementation,quote),ExactDecimal.Parse("-1")),
                new(new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,from,to,fixing,payment,rule,[daily.Projections[n]],FloatingSide.Implementation,calendar),ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation, false);
            Equal(Make(FloatingAveraging.CalendarDaysPrevious,0).Price(daily.Market,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted).Amount.ToString(),"1");
            Equal(Make(FloatingAveraging.CalendarDaysNext,3).Price(daily.Market,unit,ExactDecimal.Parse("1"),FinancialSwapInstrument.Undiscounted).Amount.ToString(),"4");
        });
        Check("schedule retains rational cash before discounting instead of rounding a repeating payment",()=>{
            var daily=DailyFixture.Create(f,quote,["1","2","2"]);
            var a=new FixedSide(Guid.NewGuid(),new UnitPrice(ExactDecimal.Parse("1"),quote),unit,daily.From,daily.To,payment,FixedSide.Implementation,quote);
            var b=new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,daily.From,daily.To,fixing,payment,FloatingAveraging.CalendarDayArithmetic,daily.Projections,FloatingSide.Implementation);
            var instrument=new FinancialSwapInstrument(Guid.NewGuid(),[new(a,ExactDecimal.Parse("-1")),new(b,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation, true);
            var context=new SideValuationContext(daily.Market,new ExplicitDiscounting(ExactDecimal.Parse("0.6"),DiscountFactorSource.Explicit));
            var floatValue=b.PV(context);var fixedValue=a.PV(context);
            var row=floatValue.Payments.Single();
            if(row.Resets.Length!=3)throw new Exception("Daily resets were collapsed into a payment");
            Equal(row.Resets[0].CashContribution.Numerator,"1");Equal(row.Resets[0].CashContribution.Denominator,"3");
            Equal(row.Resets[1].CashContribution.Numerator,"2");Equal(row.Resets[1].CashContribution.Denominator,"3");
            if(row.Resets.Select(r=>r.CashContribution).Aggregate((x,y)=>x.Add(y))!=row.CashAmount||
                row.Resets[2].Price.Term.Lookup!.Contract.DeliveryFrom!=daily.From.AddDays(2))throw new Exception("Reset allocations lost exact dated payment linkage");
            Equal(row.CashAmount.Numerator,"5");Equal(row.CashAmount.Denominator,"3");
            Reject("precision_loss",()=>row.CashAmount.ToMoney());
            Equal(floatValue.PresentValue.ToMoney().Amount.ToString(),"1");
            Equal(fixedValue.PresentValue.ToMoney().Amount.ToString(),"0.6");
            Equal(instrument.PV(context).PresentValue.ToMoney().Amount.ToString(),"0.4");
            Equal(instrument.Price(daily.Market,unit,ExactDecimal.Parse("0.6"),FinancialSwapInstrument.Discounted).Amount.ToString(),"0.4");
        });
        return count;
    }
}

internal sealed record DailyFixture(DateOnly From,DateOnly To,ImmutableArray<SwapProjection> Projections,CurveBuild Market)
{
    public static DailyFixture Create(Fixture f,QuoteSpecification quote,string[] prices)
    {
        var from=new DateOnly(2041,1,1);var curve=f.C("daily_index");
        var contracts=prices.Select((_,n)=>new MarketContract(new(f.Revision,Guid.NewGuid()),ContractKind.Forward,from.AddDays(n),from.AddDays(n+1),Fixture.Utc("2041-02-01T00:00:00Z"),null)).ToImmutableArray();
        var series=contracts.Select(c=>new MarketSeries(new(f.Revision,Guid.NewGuid()),c.Key,quote.Key,null)).ToImmutableArray();
        var definition=new Curve(curve,quote.Key,f.R("location"),f.R("uniform"),new(SettlementKind.Index,f.R("index"),null),CurveRule.Direct,contracts.Select(c=>c.Key).ToImmutableArray(),[],ObservationRole.TraderMark,f.R("source"),null);
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,[quote],contracts,[definition],[],[],series);
        var snapshot=f.Snapshot() with {Selections=series.Select((s,n)=>new SelectedObservation(new(s.Key,InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),s.Key,f.R("source"),ObservationRole.TraderMark,PublicationState.Final,Fixture.Utc("2040-12-18T00:00:00Z"),ExactDecimal.Parse(prices[n])))).ToImmutableArray()};
        var projections=contracts.Select(c=>new SwapProjection(new(curve,c.Key),c)).ToImmutableArray();
        return new(from,from.AddDays(prices.Length),projections,model.Build(snapshot,projections.Select(p=>p.Market).ToImmutableArray()));
    }
}
