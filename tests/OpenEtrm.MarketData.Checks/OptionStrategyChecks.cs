using System.Collections.Immutable;
using System.Diagnostics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;
using Market=FuturesOptionChecks.OptionMarket;

// A strategy of option legs on one expiry and one underlying future. The expiry payoff, its extremes and its breakevens
// are checked against cases worked by hand at a multiplier of 10,000; the dated curves against direct tree prices of each
// leg at the scenario futures price, instant and volatility.
internal static class OptionStrategyChecks
{
    private const int Call=11,Put=12,AtIntrinsic=13;
    private static readonly OptionNumericalPolicy Policy=OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein);
    private static readonly Market Fixture=new();

    public static void Run()
    {
        Check("a long call loses its premium at most and breaks even at strike plus premium", LongCall);
        Check("a long straddle has two breakevens and its largest loss at the strike", LongStraddle);
        Check("a short put's loss is bounded by a futures price of zero", ShortPut);
        Check("a call spread is bounded both ways", CallSpread);
        Check("an iron condor keeps its credit between the short strikes", IronCondor);
        Check("a butterfly peaks at its body", Butterfly);
        Check("a risk reversal for no premium breaks even across the whole gap between its strikes", RiskReversal);
        Check("a ratio spread has an unbounded loss and a bounded profit", RatioSpread);
        Check("a breakeven that is not a terminating decimal is rounded once at 18 places", RepeatingBreakeven);
        Check("a payoff that is zero from a price upward breaks even on an unbounded range", ZeroTail);
        Check("a payoff that only touches zero at a strike breaks even there and nowhere else", TouchesZero);
        Check("a payoff that is zero at a futures price of zero breaks even there", ZeroAtZero);
        Check("a put bought for nothing breaks even from its strike upward", FreePut);
        Check("the expiry curve is exact at each price point and at each strike inside the range", ExpiryCurvePoints);
        Check("price points are evenly spaced exact decimals, and any other range is refused", PriceGrid);
        Check("legs of another expiry or underlying future, repeated series and unsupported lots or entries are refused", LegRules);
        Check("curve instants run from the valuation instant to before expiry, in order", CurveInstants);
        Check("templates take listed strikes by offset from the at-the-money strike and never a nearer one", Templates);
        Check("dated curves equal direct tree prices of the legs at each price, instant and volatility", DatedEqualDirectTrees);
        Check("the curve at the valuation instant with no shift passes through the legs' base values", DatedBasePoint);
        Check("dated curves are refused when a leg is valued at intrinsic or did not calibrate", DatedNeedSolvedLegs);
        Check("a volatility shift the valuation refuses is the dated curves' refusal", DatedRefusedShift);
        Check("a curve asked for past the legs' instruction cutoff, or at too many prices, is an error and not a refusal", DatedAskedWrongly);
        Check("dated curves are reproducible", DatedReproducible);
        Check("a strategy of European legs is analysed by the formula and its legs declare no price step", EuropeanLegs);
    }

    // The time one request at a stated workload takes through the scenario layers, for the limits a read states.
    public static void Workload(int points,int curves)
    {
        var c=Prepare();var legs=Straddle(c);
        var prices=OptionStrategies.Prices(D("2.5"),D("2.5")+D("0.01")*D((points-1).ToString()),points);
        ImmutableArray<DateTimeOffset> instants=[..Enumerable.Range(0,curves).Select(n=>Market.At.AddHours(12*n))];
        var before=c.Bindings.Values.Sum(b=>b.TreePrices);var memory=GC.GetTotalAllocatedBytes(true);var elapsed=Stopwatch.StartNew();
        var dated=OptionStrategies.Dated(c.Prepared,c.Bindings,legs,prices,instants,D("0.01"),CancellationToken.None);
        elapsed.Stop();
        var trees=c.Bindings.Values.Sum(b=>b.TreePrices)-before;
        Console.WriteLine($"WORKLOAD legs={legs.Length} unique_series={legs.Length} points={points} curves={dated.Length} scenarios={points*curves} tree_prices={trees} "+
            $"elapsed_ms={elapsed.ElapsedMilliseconds} ms_per_tree_price={(double)elapsed.ElapsedMilliseconds/trees:F2} allocated_mb={(GC.GetTotalAllocatedBytes(true)-memory)/1048576.0:F0} "+
            $"processors={Environment.ProcessorCount} runtime={Environment.Version}");
    }

    // A series of the fixture's product at any strike: the payoff reads only its terms and multiplier.
    private static FuturesOptionInstrument Series(OptionRight right,string strike,DateTimeOffset? expiry=null,string underlying="ng_jan41")
    {
        var at=expiry??Market.Expiry;
        return new(new(Fixture.R($"on_{right}_{strike}_{at:yyyyMMdd}_{underlying}"),Fixture.R("on"),Fixture.R(underlying),new(right,D(strike),at,"american","exercise_into_future"),
            Market.Multiplier,Fixture.R("usd"),Fixture.R("on_contract"),D(Market.Tick),"synthetic_auto_exercise_v1",true,at.AddHours(-2),"premium_paid_up_front"));
    }
    private static OptionStrategyLeg Leg(OptionRight right,string strike,string lots,string entry)=>new(Series(right,strike),D(lots),D(entry));
    private static OptionStrategyExpiry Expiry(params OptionStrategyLeg[] legs)=>OptionStrategies.AtExpiry([..legs],[D("2"),D("3"),D("4")]);

    private static void LongCall()
    {
        var expiry=Expiry(Leg(OptionRight.Call,"3","1","0.08"));
        Equal(expiry.Rule,"option_expiry_exercise_value_v1");
        Equal(Text(expiry.NetPremium),"800");
        Extreme(expiry.MaximumProfit,null,"profit");Extreme(expiry.MaximumLoss,"-800","loss");
        Breakevens(expiry,"3.08");
        Curve(expiry,("2","-800"),("3","-800"),("4","9200"));
    }

    private static void LongStraddle()
    {
        var expiry=Expiry(Leg(OptionRight.Call,"3","1","0.08"),Leg(OptionRight.Put,"3","1","0.08"));
        Equal(Text(expiry.NetPremium),"1600");
        Extreme(expiry.MaximumProfit,null,"profit");Extreme(expiry.MaximumLoss,"-1600","loss");
        Breakevens(expiry,"2.84","3.16");
        Curve(expiry,("2","8400"),("3","-1600"),("4","8400"));
    }

    private static void ShortPut()
    {
        var expiry=Expiry(Leg(OptionRight.Put,"3","-1","0.08"));
        Equal(Text(expiry.NetPremium),"-800");
        // At a futures price of zero the put is worth its strike: 800 − 10,000 × 3.
        Extreme(expiry.MaximumProfit,"800","profit");Extreme(expiry.MaximumLoss,"-29200","loss");
        Breakevens(expiry,"2.92");
    }

    private static void CallSpread()
    {
        var expiry=Expiry(Leg(OptionRight.Call,"3","1","0.1"),Leg(OptionRight.Call,"3.2","-1","0.04"));
        Equal(Text(expiry.NetPremium),"600");
        Extreme(expiry.MaximumProfit,"1400","profit");Extreme(expiry.MaximumLoss,"-600","loss");
        Breakevens(expiry,"3.06");
    }

    private static void IronCondor()
    {
        var expiry=Expiry(Leg(OptionRight.Put,"2.8","-1","0.05"),Leg(OptionRight.Put,"2.6","1","0.02"),Leg(OptionRight.Call,"3.2","-1","0.05"),Leg(OptionRight.Call,"3.4","1","0.02"));
        Equal(Text(expiry.NetPremium),"-600");
        Extreme(expiry.MaximumProfit,"600","profit");Extreme(expiry.MaximumLoss,"-1400","loss");
        Breakevens(expiry,"2.74","3.26");
    }

    private static void Butterfly()
    {
        var expiry=Expiry(Leg(OptionRight.Call,"2.8","1","0.25"),Leg(OptionRight.Call,"3","-2","0.1"),Leg(OptionRight.Call,"3.2","1","0.03"));
        Equal(Text(expiry.NetPremium),"800");
        Extreme(expiry.MaximumProfit,"1200","profit");Extreme(expiry.MaximumLoss,"-800","loss");
        Breakevens(expiry,"2.88","3.12");
    }

    private static void RiskReversal()
    {
        var expiry=Expiry(Leg(OptionRight.Call,"3.2","1","0.05"),Leg(OptionRight.Put,"2.8","-1","0.05"));
        Equal(Text(expiry.NetPremium),"0");
        Extreme(expiry.MaximumProfit,null,"profit");Extreme(expiry.MaximumLoss,"-28000","loss");
        if(expiry.Breakevens is not [{Upper:{} upper} range]||Text(range.Lower)!="2.8"||Text(upper)!="3.2")throw new Exception($"Expected one breakeven range from 2.8 to 3.2; got {Ranges(expiry)}.");
    }

    private static void RatioSpread()
    {
        var expiry=Expiry(Leg(OptionRight.Call,"2.8","1","0.2"),Leg(OptionRight.Call,"3","-2","0.08"));
        Equal(Text(expiry.NetPremium),"400");
        Extreme(expiry.MaximumProfit,"1600","profit");Extreme(expiry.MaximumLoss,null,"loss");
        Breakevens(expiry,"2.84","3.16");
    }

    private static void RepeatingBreakeven()
    {
        // 3 calls at 3 for 0.10 against one call sold at 3.5 for 0.05: a debit of 2,500 recovered at 30,000 per 1.00.
        var expiry=Expiry(Leg(OptionRight.Call,"3","3","0.1"),Leg(OptionRight.Call,"3.5","-1","0.05"));
        Breakevens(expiry,"3.083333333333333333");
    }

    private static void ZeroTail()
    {
        var paid=Expiry(Leg(OptionRight.Call,"3","1","0.2"),Leg(OptionRight.Call,"3.2","-1","0"));
        if(paid.Breakevens is not [{Upper:null} tail]||Text(tail.Lower)!="3.2")throw new Exception($"Expected breakeven from 3.2 upward; got {Ranges(paid)}.");
        Extreme(paid.MaximumProfit,"0","profit");Extreme(paid.MaximumLoss,"-2000","loss");
        var free=Expiry(Leg(OptionRight.Call,"3","1","0"));
        if(free.Breakevens is not [{Upper:{} upper} range]||Text(range.Lower)!="0"||Text(upper)!="3")throw new Exception($"Expected breakeven from 0 to 3; got {Ranges(free)}.");
    }

    private static void TouchesZero()
    {
        // A call and a put sold at 3 for nothing: the payoff is −10,000 × |F − 3|, which is zero only at 3.
        var expiry=Expiry(Leg(OptionRight.Call,"3","-1","0"),Leg(OptionRight.Put,"3","-1","0"));
        Breakevens(expiry,"3");
        Extreme(expiry.MaximumProfit,"0","profit");Extreme(expiry.MaximumLoss,null,"loss");
        Curve(expiry,("2","-10000"),("3","0"),("4","-10000"));
    }

    private static void ZeroAtZero()
    {
        // A put sold at 3 for a premium of 3: 30,000 received, all of it owed back at a futures price of zero.
        var expiry=Expiry(Leg(OptionRight.Put,"3","-1","3"));
        Breakevens(expiry,"0");
        Extreme(expiry.MaximumProfit,"30000","profit");Extreme(expiry.MaximumLoss,"0","loss");
    }

    private static void FreePut()
    {
        var expiry=Expiry(Leg(OptionRight.Put,"3","1","0"));
        if(expiry.Breakevens is not [{Upper:null} tail]||Text(tail.Lower)!="3")throw new Exception($"Expected breakeven from 3 upward; got {Ranges(expiry)}.");
        Extreme(expiry.MaximumProfit,"30000","profit");Extreme(expiry.MaximumLoss,"0","loss");
    }

    private static void ExpiryCurvePoints()
    {
        var legs=ImmutableArray.Create(Leg(OptionRight.Call,"3","1","0.08"),Leg(OptionRight.Put,"3","1","0.08"),Leg(OptionRight.Call,"3.3","-1","0.01"));
        // The strike at 3 lies between two price points and joins the curve; the strike at 3.3 lies outside the range.
        var expiry=OptionStrategies.AtExpiry(legs,[D("2.95"),D("3.05"),D("3.15")]);
        Curve(expiry,("2.95","-1000"),("3","-1500"),("3.05","-1000"),("3.15","0"));
        // A strike below every price point adds no point; its breakeven is stated all the same.
        var deep=OptionStrategies.AtExpiry([Leg(OptionRight.Call,"2","1","0.5")],[D("3"),D("4")]);
        Curve(deep,("3","5000"),("4","15000"));Breakevens(deep,"2.5");
    }

    private static void PriceGrid()
    {
        Equal(string.Join(' ',OptionStrategies.Prices(D("2"),D("4"),5)),"2 2.5 3 3.5 4");
        Equal(string.Join(' ',OptionStrategies.Prices(D("2.999"),D("3.0005"),4)),"2.999 2.9995 3 3.0005");
        foreach(var (lower,upper,points) in new[]{("0","4",5),("-1","4",5),("3","3",2),("4","2",5),("2","4",1),("2","3",4),("0.000000000000000001","0.000000000000000002",3)})
            Reject("strategy_price_range",()=>OptionStrategies.Prices(D(lower),D(upper),points));
    }

    private static void LegRules()
    {
        var call=Leg(OptionRight.Call,"3","1","0.08");
        OptionStrategies.Require([call,Leg(OptionRight.Put,"3","-2","0")]);
        Reject("strategy_legs",()=>OptionStrategies.Require([]));
        Reject("strategy_legs",()=>OptionStrategies.Require([call,call with{Lots=D("-1")}]));
        Reject("strategy_leg_lots",()=>OptionStrategies.Require([call with{Lots=D("0")}]));
        Reject("strategy_leg_lots",()=>OptionStrategies.Require([call with{Lots=D("1.5")}]));
        Reject("strategy_leg_entry",()=>OptionStrategies.Require([call with{EntryPrice=D("-0.01")}]));
        Reject("strategy_expiry",()=>OptionStrategies.Require([call,new(Series(OptionRight.Put,"3",Market.Expiry.AddDays(30)),D("1"),D("0.1"))]));
        Reject("strategy_underlying",()=>OptionStrategies.Require([call,new(Series(OptionRight.Put,"3",underlying:"ng_feb41"),D("1"),D("0.1"))]));
        Reject("strategy_expiry",()=>OptionStrategies.AtExpiry([call,new(Series(OptionRight.Put,"3",Market.Expiry.AddDays(30)),D("1"),D("0.1"))],[D("3")]));
    }

    private static void CurveInstants()
    {
        OptionStrategies.RequireInstants([Market.At,Market.At.AddDays(2),Market.Expiry.AddSeconds(-1)],Market.At,Market.Expiry);
        OptionStrategies.RequireInstants([],Market.At,Market.Expiry);
        foreach(var instants in new[]{new[]{Market.At.AddSeconds(-1)},new[]{Market.Expiry},new[]{Market.At.AddDays(2),Market.At},new[]{Market.At,Market.At}})
            Reject("strategy_curve_instant",()=>OptionStrategies.RequireInstants([..instants],Market.At,Market.Expiry));
        // The fixture's series take instructions until two hours before expiry: curves stop before that cutoff while it is
        // ahead, and before expiry once it has passed or where a series has none.
        var series=Fixture.Series(Call);var cutoff=Market.Expiry.AddHours(-2);
        if(OptionStrategies.CurveHorizon([series],Market.At)!=cutoff||OptionStrategies.CurveHorizon([series],cutoff)!=Market.Expiry||
            OptionStrategies.CurveHorizon([series with{InstructionCutoffAt=null,ContraryInstructionsAllowed=false}],Market.At)!=Market.Expiry||
            OptionStrategies.CurveHorizon([series with{InstructionCutoffAt=null,ContraryInstructionsAllowed=false},series],Market.At)!=cutoff)
            throw new Exception("Curves stop before the earliest instruction cutoff still ahead, or else the expiry.");
    }

    private static void Templates()
    {
        var strikes=new[]{"2.6","2.7","2.8","2.9","3","3.1","3.2","3.3","3.4"};
        var listed=strikes.SelectMany(strike=>new[]{OptionRight.Call,OptionRight.Put}.Select(right=>new ListedOptionSeries($"{(right==OptionRight.Call?'c':'p')}{strike}",right,D(strike)))).ToArray();
        string Legs(string id,IEnumerable<ListedOptionSeries> series)=>string.Join(' ',OptionStrategyTemplates.Resolve(OptionStrategyTemplates.All.Single(t=>t.Id==id),D("3"),series).Select(l=>$"{l.Lots}×{l.Series}"));
        Equal(string.Join(' ',OptionStrategyTemplates.All.Select(t=>t.Id)),"call_spread put_spread straddle strangle iron_condor butterfly risk_reversal");
        Equal(Legs("call_spread",listed),"1×c3 -1×c3.2");
        Equal(Legs("put_spread",listed),"1×p3 -1×p2.8");
        Equal(Legs("straddle",listed),"1×c3 1×p3");
        Equal(Legs("strangle",listed),"1×c3.2 1×p2.8");
        Equal(Legs("iron_condor",listed),"-1×p2.8 1×p2.6 -1×c3.2 1×c3.4");
        Equal(Legs("butterfly",listed),"1×c2.8 -2×c3 1×c3.2");
        Equal(Legs("risk_reversal",listed),"1×c3.2 -1×p2.8");
        // Offsets count listed strikes, whatever their spacing.
        var uneven=listed.Where(s=>s.Strike!=D("3.1")).ToArray();
        Equal(Legs("call_spread",uneven),"1×c3 -1×c3.3");
        // Four strikes below the money there is none listed: nothing nearer stands in.
        var missing=Rejected("strategy_template_strike",()=>Legs("iron_condor",listed.Where(s=>s.Strike!=D("2.6")).ToArray()));
        if(!missing.Contains("4 listed strikes below"))throw new Exception($"The refusal does not name the missing strike: {missing}");
        // The strike is listed, but not the put there.
        var unlisted=Rejected("strategy_template_series",()=>Legs("put_spread",listed.Where(s=>s.Code!="p2.8").ToArray()));
        if(!unlisted.Contains("put")||!unlisted.Contains("2.8"))throw new Exception($"The refusal does not name the missing series: {unlisted}");
        Reject("strategy_template_strike",()=>Legs("straddle",listed.Where(s=>s.Strike!=D("3")).ToArray()));
    }

    private sealed record Case(PreparedConnectedPortfolioRisk Prepared,ImmutableDictionary<Guid,FuturesOptionMarketBinding> Bindings)
    {
        public LatticeDecimal Volatility(int contract)=>((SolvedVolatility)Bindings[Fixture.Option(contract).Id].Calibration).Volatility;
    }

    // The fixture's series calibrated at a unit discount; the prepared portfolio holds one contract of each solved leg.
    private static Case Prepare(params int[] contracts)=>Prepare([..(contracts.Length==0?[Call,Put]:contracts).Select(Fixture.Option)],Policy);
    private static Case Prepare(FuturesOptionInstrument[] options,OptionNumericalPolicy policy)
    {
        var source=Fixture.Snapshot();var model=Fixture.Model();
        var requests=options.Select(o=>FuturesOptionPricing.Requests(model,o.Series,FuturesOptionPricing.DailySettlement)).ToArray();
        var built=model.Build(source,[..requests.SelectMany(r=>new[]{r.Underlying,r.Settlement}).Distinct()]);
        var discount=new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted);
        var bindings=options.ToImmutableDictionary(o=>o.Id,o=>FuturesOptionPricing.Prepare(o,built,source,discount,FuturesOptionPricing.DailySettlement,policy,true,CancellationToken.None));
        var solved=options.Where(o=>bindings[o.Id].Calibration is SolvedVolatility).ToArray();
        var holdings=ValuationPortfolio.FromTrades(FuturesOptionSensitivities.UnitHoldings(solved),new(ValuationMeasure.PresentValue,
            ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){OptionBindings=bindings});
        return new(RiskEngine.PrepareConnected(built.Connected,built.PricingBindings,[holdings],built),bindings);
    }
    private static ImmutableArray<OptionStrategyLeg> Straddle(Case c)=>[new(Fixture.Option(Call),D("1"),D("0.08")),new(Fixture.Option(Put),D("1"),D("0.08"))];

    private static LatticeDecimal Tree(int contract,string futures,LatticeDecimal volatility,DateTimeOffset at)=>
        AmericanFuturesOptionModel.UnitPrice(Fixture.Option(contract).Series.Terms,L(futures),volatility,L("1"),at,Policy,CancellationToken.None);

    private static void DatedEqualDirectTrees()
    {
        var c=Prepare();var later=Market.At.AddDays(2);var shift=D("0.05");var multiplier=L(Market.Multiplier.ToString());
        // Two calls bought and three puts sold: the premium paid is 10,000 × (2 × 0.07 − 3 × 0.09).
        ImmutableArray<OptionStrategyLeg> legs=[new(Fixture.Option(Call),D("2"),D("0.07")),new(Fixture.Option(Put),D("-3"),D("0.09"))];
        var prices=new[]{"2.9","3","3.1"};
        var curves=OptionStrategies.Dated(c.Prepared,c.Bindings,legs,[..prices.Select(D)],[Market.At,later],shift,CancellationToken.None);
        if(curves.Length!=2||curves[0].At!=Market.At||curves[1].At!=later)throw new Exception("Expected one curve per requested instant, in order.");
        foreach(var curve in curves)
        {
            if(curve.Points.Length!=prices.Length)throw new Exception("Expected one value per price point.");
            for(var n=0;n<prices.Length;n++)
            {
                var call=Tree(Call,prices[n],c.Volatility(Call)+L("0.05"),curve.At);var put=Tree(Put,prices[n],c.Volatility(Put)+L("0.05"),curve.At);
                var value=multiplier*(L("2")*call-L("3")*put);
                Equal(curve.Points[n].Price.ToString(),prices[n]);
                Near(curve.Points[n].Value,value,$"value at {prices[n]} on {curve.At:O}");
                Near(curve.Points[n].ProfitAndLoss,value-multiplier*(L("0.14")-L("0.27")),$"profit and loss at {prices[n]} on {curve.At:O}");
            }
        }
        // The later curve of the bought calls and sold puts differs from the earlier one: the clock moved.
        if(curves[0].Points[1].Value==curves[1].Points[1].Value)throw new Exception("The later curve did not move with the clock.");
    }

    // The fixture's call and put stated as European and calibrated by the formula at a unit discount. Two calls bought and
    // three puts sold are worth 10,000 × (2 × call − 3 × put) by the Black-76 formula at each price, instant and volatility;
    // the payoff at expiry reads only the legs' terms, so it is the American legs' payoff.
    private static void EuropeanLegs()
    {
        var formula=OptionNumericalPolicies.Require(OptionNumericalPolicies.Black76);
        var (europeanCall,europeanPut)=(Fixture.EuropeanOption(Call),Fixture.EuropeanOption(Put));
        var c=Prepare([europeanCall,europeanPut],formula);var later=Market.At.AddDays(2);var multiplier=L(Market.Multiplier.ToString());
        ImmutableArray<OptionStrategyLeg> legs=[new(europeanCall,D("2"),D("0.07")),new(europeanPut,D("-3"),D("0.09"))];
        var prices=new[]{"2.9","3","3.1"};
        var curves=OptionStrategies.Dated(c.Prepared,c.Bindings,legs,[..prices.Select(D)],[Market.At,later],D("0.05"),CancellationToken.None);
        if(curves.Length!=2)throw new Exception("Expected one curve per requested instant.");
        LatticeDecimal Formula(FuturesOptionInstrument option,int contract,string futures,DateTimeOffset at)=>
            EuropeanFuturesOptionModel.UnitPrice(option.Series.Terms,L(futures),c.Volatility(contract)+L("0.05"),L("1"),at,formula);
        foreach(var curve in curves)
            for(var n=0;n<prices.Length;n++)
                Near(curve.Points[n].Value,multiplier*(L("2")*Formula(europeanCall,Call,prices[n],curve.At)-L("3")*Formula(europeanPut,Put,prices[n],curve.At)),
                    $"European value at {prices[n]} on {curve.At:O}");
        if(c.Prepared.DeclaredPriceSteps.Count!=0||c.Bindings.Values.Any(b=>b.TreePrices!=0||b.Policy.Lattice!=OptionLattice.ClosedForm))
            throw new Exception("European legs declare no price step and price no tree.");
        ImmutableArray<OptionStrategyLeg> american=[new(Fixture.Option(Call),D("2"),D("0.07")),new(Fixture.Option(Put),D("-3"),D("0.09"))];
        ImmutableArray<ExactDecimal> expiryPrices=[D("2"),D("3"),D("4")];
        if(!OptionStrategies.AtExpiry(legs,expiryPrices).Curve.SequenceEqual(OptionStrategies.AtExpiry(american,expiryPrices).Curve))
            throw new Exception("The payoff at expiry reads only the legs' terms.");
    }

    private static void DatedBasePoint()
    {
        var c=Prepare();var legs=Straddle(c);
        var curve=OptionStrategies.Dated(c.Prepared,c.Bindings,legs,[D("2.9"),D(Market.Futures),D("3.1")],[Market.At],default,CancellationToken.None).Single();
        var settled=L(Market.Multiplier.ToString())*(Tree(Call,Market.Futures,c.Volatility(Call),Market.At)+Tree(Put,Market.Futures,c.Volatility(Put),Market.At));
        Near(curve.Points[1].Value,settled,"base value");
        // Entered at the settlements the legs calibrate to, the strategy has made or lost nothing yet beyond each leg's
        // calibration residual, which is under half a price increment: 2 legs × 10,000 × 0.0005.
        Within(Lattice(curve.Points[1].ProfitAndLoss),L("0"),L("10"),"base profit and loss");
        Near(curve.Points[1].ProfitAndLoss,settled-L("1600"),"base profit and loss against the premium");
    }

    private static void DatedNeedSolvedLegs()
    {
        var c=Prepare(Call,AtIntrinsic);
        ImmutableArray<OptionStrategyLeg> legs=[new(Fixture.Option(Call),D("1"),D("0.08")),new(Fixture.Option(AtIntrinsic),D("-1"),D("1"))];
        Refuse("option_at_intrinsic",()=>OptionStrategies.Dated(c.Prepared,c.Bindings,legs,[D("3")],[Market.At],default,CancellationToken.None));
        var one=Prepare(Call);
        Refuse("strategy_leg_uncalibrated",()=>OptionStrategies.Dated(one.Prepared,one.Bindings,Straddle(one),[D("3")],[Market.At],default,CancellationToken.None));
        // The expiry payoff needs no volatility and is stated all the same.
        Equal(Text(OptionStrategies.AtExpiry(legs,[D("3")]).NetPremium),"-9200");
    }

    private static void DatedRefusedShift()
    {
        var c=Prepare();
        // A shift of 100 points down, the most a request may state, is more than the legs' held volatility: none is left to price at.
        if(c.Volatility(Call).CompareTo(L("1"))>=0)throw new Exception("The fixture's volatility is expected to be under 100 points.");
        Refuse("scenario_volatility",()=>OptionStrategies.Dated(c.Prepared,c.Bindings,Straddle(c),[D("3")],[Market.At],D("-1"),CancellationToken.None));
    }

    private static void DatedAskedWrongly()
    {
        var c=Prepare();var cutoff=Market.Expiry.AddHours(-2);
        void Wrong(string code,Action ask)
        {
            try{ask();}catch(OptionStrategyCurveRefusal refusal){throw new Exception($"{refusal.Code} was reported as a refusal of the curves.");}
            catch(MarketDataException e)when(e.Code==code){return;}
            throw new Exception($"Expected {code}.");
        }
        Wrong("strategy_curve_instant",()=>OptionStrategies.Dated(c.Prepared,c.Bindings,Straddle(c),[D("3")],[cutoff],default,CancellationToken.None));
        OptionStrategies.Dated(c.Prepared,c.Bindings,Straddle(c),[D("3")],[cutoff.AddSeconds(-1)],default,CancellationToken.None);
        Wrong("strategy_price_range",()=>OptionStrategies.Dated(c.Prepared,c.Bindings,Straddle(c),OptionStrategies.Prices(D("1"),D("1")+D("0.001")*D(RiskEngine.MaxJointScenarios.ToString()),RiskEngine.MaxJointScenarios+1),
            [Market.At],default,CancellationToken.None));
        Wrong("option_sensitivity_input",()=>OptionStrategies.Dated(null,c.Bindings,Straddle(c),[D("3")],[Market.At],default,CancellationToken.None));
    }

    private static void DatedReproducible()
    {
        var a=Prepare();var b=Prepare();ImmutableArray<ExactDecimal> prices=[D("2.9"),D("3.1")];ImmutableArray<DateTimeOffset> instants=[Market.At.AddDays(1)];
        var first=OptionStrategies.Dated(a.Prepared,a.Bindings,Straddle(a),prices,instants,D("0.01"),CancellationToken.None);
        var second=OptionStrategies.Dated(b.Prepared,b.Bindings,Straddle(b),prices,instants,D("0.01"),CancellationToken.None);
        if(!first.Single().Points.SequenceEqual(second.Single().Points))throw new Exception("Two calculations from the same inputs differ.");
    }

    private static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
    private static LatticeDecimal L(string value)=>LatticeDecimal.Parse(value);
    private static string Text(SignedRatio value)=>HalfEven18.Report(value);
    private static LatticeDecimal Lattice(SignedRatio value)=>L(Text(value));
    private static string Ranges(OptionStrategyExpiry expiry)=>string.Join(' ',expiry.Breakevens.Select(b=>$"[{Text(b.Lower)},{(b.Upper is {} upper?Text(upper):"∞")}]"));
    private static void Breakevens(OptionStrategyExpiry expiry,params string[] prices)
    {
        if(expiry.Breakevens.Any(b=>b.Upper!=b.Lower)||!expiry.Breakevens.Select(b=>Text(b.Lower)).SequenceEqual(prices))
            throw new Exception($"Expected breakevens at {string.Join(' ',prices)}; got {Ranges(expiry)}.");
    }
    private static void Extreme(OptionStrategyExtreme actual,string? amount,string label)
    {
        if(actual.Bounded!=(amount is not null)||(actual.Amount is {} stated?Text(stated):null)!=amount)
            throw new Exception($"Maximum {label}: expected {amount??"unbounded"}; got {(actual.Amount is {} got?Text(got):"unbounded")}.");
    }
    private static void Curve(OptionStrategyExpiry expiry,params (string Price,string ProfitAndLoss)[] points)
    {
        var actual=expiry.Curve.Select(p=>(p.Price.ToString(),Text(p.ProfitAndLoss))).ToArray();
        if(!actual.SequenceEqual(points))throw new Exception($"Expected the curve {string.Join(' ',points)}; got {string.Join(' ',actual)}.");
    }
    private static LatticeDecimal Abs(LatticeDecimal value)=>LatticeDecimal.Max(value,L("0")-value);
    // A leg's contract value is its unit price rounded once at 18 places, times the multiplier and lots.
    private static void Near(SignedRatio actual,LatticeDecimal expected,string label)=>Within(Lattice(actual),expected,L("0.000000001"),label);
    private static void Within(LatticeDecimal actual,LatticeDecimal expected,LatticeDecimal tolerance,string label)
    { if(Abs(actual-expected).CompareTo(tolerance)>0)throw new Exception($"{label}: expected {expected} within {tolerance}; got {actual}."); }
    private static void Check(string label,Action run){run();Console.WriteLine($"PASS {label}");}
    private static void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}; got {actual}.");}
    private static void Reject(string code,Action action)=>Rejected(code,action);
    private static string Rejected(string code,Action action)
    {
        try{action();}catch(MarketDataException e)when(e.Code==code){return e.Message;}
        throw new Exception($"Expected {code}.");
    }
    // A refusal of the dated curves with this code, as distinct from an error in what was asked.
    private static void Refuse(string code,Action action)
    {
        try{action();}catch(OptionStrategyCurveRefusal e)when(e.Code==code){return;}
        throw new Exception($"Expected the curves to be refused with {code}.");
    }
}
