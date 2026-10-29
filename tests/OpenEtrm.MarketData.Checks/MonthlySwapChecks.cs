using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class MonthlySwapChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(string name, Action action) { action(); count++; Console.WriteLine("PASS monthly swaps: " + name); }
        static void Equal(string actual, string expected)
        { if (actual != expected) throw new Exception($"Expected {expected}, got {actual}"); }
        static void Reject(string code, Action action)
        { try { action(); } catch (MarketDataException error) when (error.Code == code) { return; } throw new Exception("Expected " + code); }
        var ten = new StripFixture(120);
        Check("whole-term monthly obligations use the shared fixed and floating families", () =>
        {
            var swap = ten.Swap(MonthlyQuantityBasis.PerMonth);
            if (swap.Sides[0].Side.GetType() != typeof(FixedSide) || swap.Sides[1].Side.GetType() != typeof(FloatingSide))
                throw new Exception("A monthly swap must hold one whole-term FixedSide and one whole-term FloatingSide.");
        });
        Check("whole-term fixed schedules retain stepped price references and exact dated values", () =>
        {
            var three=new StripFixture(3);var original=three.Swap(MonthlyQuantityBasis.TotalEqualMonths);
            var first=original.Fixed.Periods[0].DeliveryFrom;
            var price=new SteppedPrice([new(first,new(ExactDecimal.Parse("4"),three.Market.Quote)),
                new(first.AddMonths(1),new(ExactDecimal.Parse("5"),three.Market.Quote)),new(first.AddMonths(2),new(ExactDecimal.Parse("6"),three.Market.Quote))]);
            var side=new FixedSide(Guid.NewGuid(),price,three.Market.Quote,original.UnitQuantity,original.Fixed.Periods);
            var schedule=side.GenerateSchedule();
            if(schedule.Rows.Count!=3 || schedule.Rows.Any(row=>!ReferenceEquals(row.Prices.Single().FixedPrice,price)))
                throw new Exception("Each generated row must retain the contractual price-term reference.");
            Equal(side.PV(new(three.Market.Snapshot,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))).PresentValue.ToExactString(),"5");
        });
        Check("anniversary prices and nonterminating allocations remain exact through the whole term", () =>
        {
            var thirteen=new StripFixture(13);var original=thirteen.Swap(MonthlyQuantityBasis.TotalEqualMonths);
            var price=new EscalatedPrice(new(ExactDecimal.Parse("4"),thirteen.Market.Quote),ExactDecimal.Parse("0.1"),original.Fixed.DeliveryFrom,
                OpenEtrm.Shared.Temporal.ContractYearBasis.AnniversaryStep);
            var side=new FixedSide(Guid.NewGuid(),price,thirteen.Market.Quote,original.UnitQuantity,original.Fixed.Periods);
            var value=side.PV(new(thirteen.Market.Snapshot,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))).PresentValue;
            Equal(value.Numerator,"262");Equal(value.Denominator,"65");
        });
        Check("ten-year monthly quantities preserve all one-hundred-twenty obligations", () =>
        {
            // Sum(-0.75+i/1000), i=0..119 = -90+7.14.
            var swap = ten.Swap(MonthlyQuantityBasis.PerMonth);
            var result = ten.Value(swap);
            Equal(result.PresentValue.Scale(ExactDecimal.Parse("10")).ToMoney().Amount.ToString(), "-828.6");
            if (result.Sides.Any(s => ((PaymentSideValuation)s.Value).Payments.Length != 120) ||
                result.Sides.Any(s => ((PaymentSideValuation)s.Value).Payments.Select((p,n) => p.Plan.Key.SideId == s.SideId && p.Plan.Key.Ordinal == n).Any(valid => !valid)))
                throw new Exception("Monthly payments did not retain their leg identity and ordinal.");
            foreach(var side in result.Sides)
            foreach(var payment in ((PaymentSideValuation)side.Value).Payments)
            foreach(var application in payment.Applications)
                if(application.Application.Payment!=payment.Plan.Key||application.Application.Payment.SideId!=side.SideId||
                    !ReferenceEquals(application.Application.Price,payment.Plan.Prices[application.Application.PriceOrdinal])||
                    !ReferenceEquals(application.Price,payment.Prices[application.Application.PriceOrdinal]))
                    throw new Exception("Monthly composition failed to rebase the exact valued application to its side and payment group.");
        });
        Check("total equal-month quantity is allocated exactly rather than repeated", () =>
        {
            var result = ten.Value(ten.Swap(MonthlyQuantityBasis.TotalEqualMonths));
            Equal(result.PresentValue.Scale(ExactDecimal.Parse("120")).ToMoney().Amount.ToString(), "-82.86");
            Equal(((PaymentSideValuation)result.Sides[0].Value).Payments[0].Plan.AllocatedQuantity.Numerator, "1");
            Equal(((PaymentSideValuation)result.Sides[0].Value).Payments[0].Plan.AllocatedQuantity.Denominator, "120");
        });
        Check("calendar-day allocation accounts for leap years without changing price averaging", () =>
        {
            // 2041..2050 contain3652days. Independently sum days_in_month*(-750+i)/1000.
            var result = ten.Value(ten.Swap(MonthlyQuantityBasis.TotalCalendarDays));
            Equal(result.PresentValue.Scale(ExactDecimal.Parse("3652")).ToMoney().Amount.ToString(), "-2521.608");
        });
        Check("thirty-year strip consumes every independent payment factor", () =>
        {
            var thirty = new StripFixture(360); var swap = thirty.Swap(MonthlyQuantityBasis.PerMonth, discounted: true);
            // Even month differences sum-102.78; odd sum-102.6 with DF0.5 gives-154.08.
            Equal(thirty.Value(swap, new AlternatingDiscounting()).PresentValue.ToMoney().Amount.ToString(), "-154.08");
        });
        Check("one-third payment allocations retain rational cash before final aggregation", () =>
        {
            var three = new StripFixture(3); var value = three.Value(three.Swap(MonthlyQuantityBasis.TotalEqualMonths));
            var payment = ((PaymentSideValuation)value.Sides[0].Value).Payments[0];
            Equal(payment.CashAmount.Numerator, "4"); Equal(payment.CashAmount.Denominator, "3");
            Equal(value.PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString(), "-2.247");
        });
        Check("missing, repeated, unordered and partial-month periods reject", () =>
        {
            Reject("swap_side_periods", () => ten.Leg(ten.Fixed.RemoveAt(1), MonthlyQuantityBasis.PerMonth));
            Reject("swap_side_periods", () => ten.Leg(ten.Fixed.SetItem(1, ten.Fixed[0]), MonthlyQuantityBasis.PerMonth));
            Reject("swap_side_periods", () => ten.Leg(ten.Fixed.Reverse().ToImmutableArray(), MonthlyQuantityBasis.PerMonth));
            var first = (FixedSide)ten.Fixed[0];
            var stub = new FixedSide(Guid.NewGuid(), first.Price, ten.Market.Quote, first.UnitQuantity,[first.Periods[0] with{DeliveryFrom=first.DeliveryFrom.AddDays(1)}]);
            Reject("swap_side_periods", () => ten.Leg(ten.Fixed.SetItem(0, stub), MonthlyQuantityBasis.PerMonth));
            Reject("swap_side_periods", () => ten.Leg([], MonthlyQuantityBasis.PerMonth));
            Reject("swap_side_periods", () => ten.Leg(Enumerable.Repeat(ten.Fixed[0], 1201).ToImmutableArray(), MonthlyQuantityBasis.PerMonth));
            Reject("monthly_quantity_basis", () => ten.Leg(ten.Fixed, (MonthlyQuantityBasis)0));
        });
        Check("mixed reference or quote units and mismatched legs reject", () =>
        {
            var first = (FixedSide)ten.Fixed[0]; var otherQuote = ten.Market.Quote with { Key = ten.Market.Quote.Key with { Code = "other_quote" } };
            var other = new FixedSide(Guid.NewGuid(), first.Price, otherQuote, first.UnitQuantity,first.Periods);
            Reject("swap_side_periods", () => ten.Leg(ten.Fixed.SetItem(0, other), MonthlyQuantityBasis.PerMonth));
            Reject("monthly_swap_instrument", () => new MonthlyFinancialSwap(Guid.NewGuid(),
                [new(ten.Leg(ten.Fixed, MonthlyQuantityBasis.PerMonth), ExactDecimal.Parse("-1")),
                 new(ten.Leg(ten.Floating, MonthlyQuantityBasis.TotalEqualMonths), ExactDecimal.Parse("1"))], MonthlyFinancialSwap.Implementation, false));
        });
        Check("due monthly payments preserve the explicit lifecycle rejection", () =>
        {
            var swap = ten.Swap(MonthlyQuantityBasis.PerMonth);
            var snapshot = ten.Market.Snapshot with { ValuationAt = ((FixedSide)ten.Fixed[0]).PaymentAt };
            // Do not build expired projections to prove a due fixed leg's lifecycle boundary.
            Reject("side_payment_due", () => swap.Fixed.PV(new(snapshot, new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted))));
        });
        Check("a dated quote shock reaches every trade referencing the same monthly strip", () =>
        {
            var swap = ten.Swap(MonthlyQuantityBasis.PerMonth); var bookA = Guid.NewGuid(); var bookB = Guid.NewGuid();
            RiskPosition Position(Guid book, string quantity) => new(Guid.NewGuid(), Guid.NewGuid(), book, swap.Id,
                new(ExactDecimal.Parse(quantity), swap.UnitQuantity.Specification),
                new(new Money(default, swap.Currency), swap.UnitQuantity.Specification), new(default, swap.Currency));
            var portfolio = new ValuationPortfolio([swap], [Position(bookA,"2"),Position(bookA,"-1"),Position(bookB,"3")],
                new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                    new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted)));
            var factor = ten.Market.Model.QuoteFactors(ten.Market.Snapshot).Single(f => f.Input == ten.Market.Selection(119,false).Input);
            var bump = new QuoteBump(factor.Input, factor.Quote, factor.Unit, ExactDecimal.Parse("0.1"), QuoteBump.Convention);
            var run = RiskEngine.Calculate(ten.Market.Model, ten.Market.Snapshot, portfolio, [bump], RiskEngine.NumericalPolicy);
            foreach (var row in run.Factors[0].Positions)
            {
                var position = portfolio.Positions.Single(p => p.BindingId == row.BindingId);
                if (row.Delta.Value != position.Quantity.Amount || row.UpPnl.Amount != position.Quantity.Amount * ExactDecimal.Parse("0.1"))
                    throw new Exception("Monthly quote sensitivity lost an external signed holding.");
            }
            if (run.Work.UnitPricerCalls != 3) throw new Exception("One strip should price once per shared scenario.");
        });
        Check("independent whole-term side identities cannot collide", () =>
        {
            var original=ten.Swap(MonthlyQuantityBasis.PerMonth);
            var colliding=new FloatingSide(original.Fixed.Id,original.Floating.Settlement,original.Floating.Quote,original.UnitQuantity,original.Floating.Periods);
            Reject("monthly_swap_instrument",()=>new MonthlyFinancialSwap(Guid.NewGuid(),
                [new(original.Fixed,ExactDecimal.Parse("-1")),new(colliding,ExactDecimal.Parse("1"))],MonthlyFinancialSwap.Implementation,false));
        });
        Check("maximum supported strip preserves exact total allocations", () =>
        {
            var hundred = new StripFixture(1200);
            foreach (var basis in new[] { MonthlyQuantityBasis.TotalEqualMonths, MonthlyQuantityBasis.TotalCalendarDays })
            {
                var payments = hundred.Swap(basis).Fixed.GenerateDatedSchedule().Payments;
                var total = payments.Select(p => p.AllocatedQuantity).Aggregate((a,b) => a.Add(b));
                Equal(total.Numerator, "1"); Equal(total.Denominator, "1");
            }
        });
        Check("monthly actuals are frozen and create no phantom live factors", () =>
        {
            var three = new StripFixture(3);
            var periods = Enumerable.Range(0,3).Select(n => three.Market.Swap(indexed:true,monthly:true,monthOrdinal:n)).ToArray();
            var indexed = new MonthlyFinancialSwap(Guid.NewGuid(),
                [new(three.Leg(periods.Select(p => (Side)p.Fixed).ToImmutableArray(),MonthlyQuantityBasis.PerMonth),ExactDecimal.Parse("-1")),
                 new(three.Leg(periods.Select(p => (Side)p.Floating).ToImmutableArray(),MonthlyQuantityBasis.PerMonth),ExactDecimal.Parse("1"))],
                MonthlyFinancialSwap.Implementation,false);
            var actuals = Enumerable.Range(0,2).Select(n => {
                var fixing = periods[n].Floating.Fixings[0];
                return new SelectedObservation(new(fixing.Series,InputUse.FrozenFact),new(new(fixing.Series.Revision.CatalogId,Guid.NewGuid()),
                    fixing.Series,fixing.Source,ObservationRole.IndexFixing,PublicationState.Final,fixing.FixingAt,ExactDecimal.Parse(n==0?"10":"20")));
            }).ToImmutableArray();
            var at = periods[1].Floating.Fixings[0].FixingAt;
            var snapshot = three.Market.Snapshot with {Id=Guid.NewGuid(),ValuationAt=at,MarketCutoff=at,
                Selections=three.Market.Snapshot.Selections.AddRange(actuals)};
            var market = three.Market.Model.Build(snapshot,indexed.DependenciesAt(snapshot));
            var value = indexed.PV(new SideValuationContext(market,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            Equal(value.PresentValue.ToMoney().Amount.ToString(),"21.252");
            if(indexed.DependenciesAt(snapshot).Length!=1)throw new Exception("Resolved months remained live dependencies.");
            var resets=((PaymentSideValuation)value.Sides[1].Value).Payments.SelectMany(p=>p.Resets).ToArray();
            if(resets[0].Price.State!=SchedulePriceState.Actual||resets[1].Price.State!=SchedulePriceState.Actual||resets[2].Price.State!=SchedulePriceState.Projected)
                throw new Exception("Monthly actual/projected states changed.");
            var held=new RiskPosition(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),indexed.Id,new(ExactDecimal.Parse("1"),indexed.UnitQuantity.Specification),
                new(new Money(default,indexed.Currency),indexed.UnitQuantity.Specification),new(default,indexed.Currency));
            var portfolio=new ValuationPortfolio([indexed],[held],new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            if(portfolio.RiskFactors(three.Market.Model,snapshot).Single().Factors.Length!=2)
                throw new Exception("Actual months gained live quote factors.");
            var missing=snapshot with{Selections=snapshot.Selections.Remove(actuals[0])};
            Reject("index_fixing_missing",()=>indexed.PV(new SideValuationContext(three.Market.Model.Build(missing,indexed.DependenciesAt(missing)),
                new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))));
        });
        Check("retained whole-term identities and pricing bindings cannot be reinterpreted", () =>
        {
            var three=new StripFixture(3);var original=three.Swap(MonthlyQuantityBasis.PerMonth);
            var map=new InstrumentIdentityMap(Guid.NewGuid());map.Intern(original);
            var changed=new FixedSide(original.Fixed.Id,new FlatPrice(new(ExactDecimal.Parse("9"),three.Market.Quote)),three.Market.Quote,
                original.UnitQuantity,original.Fixed.Periods);
            var conflicting=new MonthlyFinancialSwap(Guid.NewGuid(),[new(changed,ExactDecimal.Parse("-1")),new(original.Floating,ExactDecimal.Parse("1"))],MonthlyFinancialSwap.Implementation,false);
            Reject("side_identity",()=>map.Intern(conflicting));
            var period=original.Floating.Periods[0];var projection=period.Projections[0];
            var contract=projection.Contract with{Key=projection.Contract.Key with{Id=Guid.NewGuid()}};
            var rebound=new FloatingSide(Guid.NewGuid(),original.Floating.Settlement,original.Floating.Quote,original.UnitQuantity,
                original.Floating.Periods.SetItem(0,period with{Projections=[new(new(projection.Market.Curve,contract.Key),contract)]}));
            var otherBinding=new MonthlyFinancialSwap(Guid.NewGuid(),[new(original.Fixed,ExactDecimal.Parse("-1")),new(rebound,ExactDecimal.Parse("1"))],MonthlyFinancialSwap.Implementation,false);
            Reject("valuation_binding",()=>map.Intern(otherBinding));
        });
        Check("every payment needs its own exact discount context and explicit calendar", () =>
        {
            var swap=ten.Swap(MonthlyQuantityBasis.PerMonth,discounted:true);
            Reject("side_payment_factors",()=>ten.Value(swap,new BrokenDiscounting()));
            Reject("swap_valuation",()=>ten.Value(swap));
            Reject("payment_rule",()=>new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,2,null,
                PaymentDayBasis.BusinessDays,null,OpenEtrm.Shared.Temporal.DateRoll.Unadjusted,null,PaymentRule.Implementation));
        });
        return count;
    }

    private sealed class StripFixture
    {
        public CommodityLookupFixture Market { get; }
        public ImmutableArray<Side> Fixed { get; }
        public ImmutableArray<Side> Floating { get; }
        public StripFixture(int months)
        {
            Market = new(months, monthly: true);
            var periods = Enumerable.Range(0, months).Select(n => Market.Swap(monthly: true, monthOrdinal: n)).ToArray();
            Fixed = periods.Select(p => (Side)p.Fixed).ToImmutableArray();
            Floating = periods.Select(p => (Side)p.Floating).ToImmutableArray();
        }
        public Side Leg(ImmutableArray<Side> periods, MonthlyQuantityBasis basis) => WholeTermSideFixtures.Monthly(periods,Market.Quote,basis);
        public MonthlyFinancialSwap Swap(MonthlyQuantityBasis basis, bool discounted = false) => new(Guid.NewGuid(),
            [new(Leg(Fixed,basis),ExactDecimal.Parse("-1")),new(Leg(Floating,basis),ExactDecimal.Parse("1"))], MonthlyFinancialSwap.Implementation, discounted);
        public InstrumentValuation Value(MonthlyFinancialSwap swap, ISideDiscounting? discounting = null) => swap.PV(new SideValuationContext(
            Market.Model.Build(Market.Snapshot, swap.DependenciesAt(Market.Snapshot)), discounting ?? new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
    }
    private sealed class BrokenDiscounting : ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(SidePaymentSchedule schedule,MarketSnapshot snapshot) => schedule.Payments.Skip(1).Select(p=>
            new SidePaymentFactor(p.Key,ExactDecimal.Parse("1"),snapshot,DiscountFactorSource.Explicit)).ToImmutableArray();
    }
    private sealed class AlternatingDiscounting : ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(SidePaymentSchedule schedule, MarketSnapshot snapshot) => schedule.Payments.Select(p =>
            new SidePaymentFactor(p.Key, ExactDecimal.Parse(p.Key.Ordinal % 2 == 0 ? "1" : "0.5"), snapshot, DiscountFactorSource.Explicit)).ToImmutableArray();
    }
}
