using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class IndexFixingChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(string name, Action action) { action(); count++; Console.WriteLine("PASS index fixings: " + name); }
        void Equal<T>(T actual, T expected) { if (!Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}"); }
        void Reject(string code, Action action)
        { try { action(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
        var monthly = new FixingFixture(monthly: true);
        var daily = new FixingFixture();
        var at = daily.Fixings[1].FixingAt;
        var snapshot = daily.Snapshot(at, actuals: 2);
        var swap = daily.Swap();
        Check("obsolete projection-only floating terms refuse rather than acquiring fixing policy", () =>
        {
            var indexed=daily.Floating();
            Reject("index_fixing_definition",()=>new FloatingSide(indexed.Id,indexed.Settlement,indexed.Quote,indexed.UnitQuantity,
                [..indexed.Periods.Select(period=>period with{Fixings=[],ObservationImplementation="prefixing_index_projection_v1"})]));
        });
        Check("a raw row selector validates the complete fixing definition before consuming a selected value", () =>
        {
            var market = daily.Model.Build(snapshot, swap.DependenciesAt(snapshot));
            var context = new SideValuationContext(market, new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted));
            var term = ((SidePaymentSchedule)swap.Floating.GenerateSchedule()).Payments[0].Prices[0];
            var invalid = term.Lookup! with { Fixing = term.Lookup!.Fixing! with { Implementation = "unsupported" } };
            Reject("index_fixing_definition", () => context.Resolve(new SchedulePriceTerm(ExactDecimal.Parse("1"), null, invalid)));
        });
        InstrumentValuation Value(FixingFixture f, MarketSnapshot s, FinancialSwapInstrument? instrument = null)
        {
            instrument ??= f.Swap();
            return instrument.PV(new SideValuationContext(f.Model.Build(s, instrument.DependenciesAt(s)),
                new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted)));
        }
        Check("monthly actual replaces an expired projection at the exact explicit publication boundary", () =>
        {
            var s = monthly.Snapshot(monthly.Fixings[0].FixingAt, 1); var instrument = monthly.Swap();
            Equal(instrument.DependenciesAt(s).Length, 0);
            Equal(Value(monthly, s, instrument).PresentValue.ToMoney().Amount.ToString(), "-5");
        });
        Check("future monthly reset projects and does not require an actual", () =>
        {
            var s = monthly.Snapshot(monthly.Fixings[0].FixingAt.AddDays(-2));
            Equal(monthly.Swap().DependenciesAt(s).Length, 1);
            Equal(Value(monthly, s).PresentValue.ToMoney().Amount.ToString(), "-4");
        });
        Check("a fully fixed deal risk run prices its actuals once with no live quote scenarios", () =>
        {
            var s = monthly.Snapshot(monthly.Fixings[0].FixingAt, 1); var instrument = monthly.Swap();
            ExactMoney Price(CurveBuild market) => instrument.PV(new SideValuationContext(market,
                new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted))).PresentValue;
            var risk = DealRisk.Calculate(monthly.Model, s, instrument.DependenciesAt(s), Price, DealRisk.BumpPolicy, DealRisk.NumericalPolicy);
            Equal(risk.Base.ToMoney().Amount.ToString(), "-5"); Equal(risk.Factors.Length, 0); Equal(risk.ScenarioCount, 1);
            Reject("index_fixing_missing", () => DealRisk.Calculate(monthly.Model, monthly.Snapshot(monthly.Fixings[0].FixingAt), [],
                Price, DealRisk.BumpPolicy, DealRisk.NumericalPolicy));
            Reject("risk_policy", () => DealRisk.Calculate(monthly.Model, s, default, Price, DealRisk.BumpPolicy, DealRisk.NumericalPolicy));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { DealRisk.Calculate(monthly.Model, s, [], Price, DealRisk.BumpPolicy, DealRisk.NumericalPolicy, cancelled.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("A canceled fully fixed risk run returned success.");
        });
        Check("daily mixed actual and projected values retain the complete averaging denominator", () =>
        {
            var deps = swap.DependenciesAt(snapshot); Equal(deps.Length, 1); Equal(deps[0], daily.Projections[2].Market);
            var value = Value(daily, snapshot, swap);
            Equal(value.PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString(), "18");
            var resets = ((PaymentSideValuation)value.Sides[1].Value).Payments[0].Resets;
            Equal(string.Join(',', resets.Select(r => r.Price.State)), "Actual,Actual,Projected");
            Equal(string.Join(',', resets.Select(r => r.Price.Price.Value.ToString())), "10,20,33");
            Equal(resets[0].Price.Lineage.Single(), snapshot.Selections.Single(s => s.Input == new InputKey(daily.Series[0].Key, InputUse.FrozenFact)).Observation.Key);
        });
        Check("whole-book risk values shared indexed units with frozen actuals and shocks only unresolved resets", () =>
        {
            var book=Guid.NewGuid();
            RiskPosition Position(string quantity)=>new(Guid.NewGuid(),Guid.NewGuid(),book,swap.Id,
                new(ExactDecimal.Parse(quantity),swap.UnitQuantity.Specification),new(new Money(default,swap.Currency),swap.UnitQuantity.Specification),new(default,swap.Currency));
            var portfolio=new ValuationPortfolio([swap],[Position("100"),Position("250")],
                new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                    new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            var prepared=RiskEngine.Prepare(daily.Model,snapshot,[portfolio]);
            Equal(prepared.Factors.Length,1);
            Equal(prepared.Factors.Single().Input.Series,daily.Series[2].Key);
            var factor=prepared.Factors.Single();
            var run=RiskEngine.CalculateExact(prepared,[new(factor.Input,factor.Quote,factor.Unit,factor.TradingTick,QuoteBump.Convention)],RiskEngine.ExactNumericalPolicy);
            Equal(run.Groups.Single().BaseValues.Single().Value.ToMoney().Amount.ToString(),"2100");
            var risk=run.Groups.Single().Factors.Single().Totals.Single().Values;
            Equal(risk.Delta.Value.Numerator,"350");Equal(risk.Delta.Value.Denominator,"3");Equal(risk.Gamma.Value.Numerator,"0");
            Equal(run.Work.UniqueValuations,1);Equal(run.Work.UnitPricerCalls,3);Equal(run.Work.PositionCount,2);
            var fixedSnapshot=daily.Snapshot(daily.Fixings[2].FixingAt,3);
            var fixedPlan=RiskEngine.Prepare(daily.Model,fixedSnapshot,[portfolio]);
            Equal(fixedPlan.Factors.Length,0);
            var fixedRun=RiskEngine.CalculateExact(fixedPlan,[],RiskEngine.ExactNumericalPolicy);
            Equal(fixedRun.Groups.Single().BaseValues.Single().Value.ToMoney().Amount.ToString(),"1750");
            Equal(fixedRun.Groups.Single().Factors.Length,0);Equal(fixedRun.Work.ScenarioCount,1);Equal(fixedRun.Work.UnitPricerCalls,1);
            var missing=daily.Snapshot(at,1);
            Reject("index_fixing_missing",()=>RiskEngine.CalculateExact(RiskEngine.Prepare(daily.Model,missing,[portfolio]),[],RiskEngine.ExactNumericalPolicy));
        });
        Check("cancellation during the sole fully fixed repricing cannot return successful zero risk", () =>
        {
            var s = monthly.Snapshot(monthly.Fixings[0].FixingAt, 1); var instrument = monthly.Swap();
            using var cancelled = new CancellationTokenSource();
            ExactMoney Price(CurveBuild market)
            {
                var result = instrument.PV(new SideValuationContext(market, new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted))).PresentValue;
                cancelled.Cancel(); return result;
            }
            try { DealRisk.Calculate(monthly.Model, s, [], Price, DealRisk.BumpPolicy, DealRisk.NumericalPolicy, cancelled.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Canceled repricing returned a successful risk run.");
        });
        Check("fully fixed daily period values without building expired projections", () =>
        {
            var s = daily.Snapshot(daily.Fixings[2].FixingAt, 3);
            Equal(swap.DependenciesAt(s).Length, 0);
            Equal(Value(daily, s, swap).PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString(), "15");
        });
        Check("one instant before a fixing projects only that still-live reset", () =>
        {
            var s = daily.Snapshot(at.AddTicks(-10), 1);
            Equal(swap.DependenciesAt(s).Length, 2);
            Equal(Value(daily, s, swap).PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString(), "20");
        });
        Check("missing actual at the exact boundary never falls back to its forecast", () =>
            Reject("index_fixing_missing", () => Value(daily, daily.Snapshot(at, 1), swap)));
        MarketSnapshot ChangeActual(MarketSnapshot s, Func<MarketObservation, MarketObservation> change) => s with
        { Selections = s.Selections.Select(x => x.Input == new InputKey(daily.Series[0].Key, InputUse.FrozenFact) ? x with { Observation = change(x.Observation) } : x).ToImmutableArray() };
        Check("wrong actual source and role reject even when a compatible numeric value exists", () =>
        {
            Reject("index_fixing_selection", () => Value(daily, ChangeActual(snapshot, o => o with { Source = daily.Base.R("other_source") }), swap));
            Reject("index_fixing_selection", () => Value(daily, ChangeActual(snapshot, o => o with { Role = ObservationRole.FinalContractSettlement }), swap));
        });
        Check("actuals must be final and observed within their explicit publication and market cutoff", () =>
        {
            Reject("frozen_fact", () => Value(daily, ChangeActual(snapshot, o => o with { State = PublicationState.Preliminary }), swap));
            Reject("index_fixing_selection", () => Value(daily, ChangeActual(snapshot, o => o with { ObservedAt = daily.Fixings[0].FixingAt.AddTicks(-10) }), swap));
            Reject("observation_cutoff", () => Value(daily, ChangeActual(snapshot, o => o with { ObservedAt = at.AddTicks(10) }), swap));
        });
        Check("explicit old and corrected actual pins produce separate reproducible values", () =>
        {
            var revised = ChangeActual(snapshot, o => o with { Key = new(o.Key.CatalogId, Guid.NewGuid()), Value = ExactDecimal.Parse("13") });
            Equal(Value(daily, revised, swap).PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString(), "21");
            Equal(Value(daily, snapshot, swap).PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString(), "18");
        });
        Check("an actual is never a variable curve input or a direct curve recipe role", () =>
        {
            var wrong = snapshot with { Selections = snapshot.Selections.Where(s => s.Input != new InputKey(daily.Series[0].Key, InputUse.Variable))
                .Select(s => s.Input == new InputKey(daily.Series[0].Key, InputUse.FrozenFact) ? s with { Input = new(s.Input.Series, InputUse.Variable) } : s).ToImmutableArray() };
            Reject("index_fixing_use", () => daily.Model.Build(wrong, []));
            Reject("unsupported_role", () => new CurveModel(daily.Base.Revision, daily.Base.Reference, daily.Base.Quantities,
                [daily.Quote], daily.Projections.Select(p => p.Contract).ToImmutableArray(), [daily.Curve with { DirectRole = ObservationRole.IndexFixing }], [], [], daily.Series));
        });
        Check("actual series must belong to the exact observation period rather than another daily reset", () =>
        {
            var rules = daily.Fixings.SetItem(0, daily.Fixings[0] with { Series = daily.Series[1].Key })
                .SetItem(1, daily.Fixings[1] with { Series = daily.Series[0].Key });
            Reject("index_fixing_definition", () => Value(daily, snapshot, daily.Swap(daily.Floating(rules))));
        });
        Check("fixing schedule coverage, identities, quote and explicit implementation cannot be guessed", () =>
        {
            Reject("index_fixing_definition", () => daily.Floating([]));
            Reject("index_fixing_definition", () => daily.Floating(daily.Fixings.RemoveAt(0)));
            foreach (var rule in new[] { daily.Fixings[0] with { Index = daily.Base.R("other_index") },
                daily.Fixings[0] with { Quote = daily.Base.R("other_quote") }, daily.Fixings[0] with { To = daily.To },
                daily.Fixings[0] with { Implementation = "" }, daily.Fixings[0] with { Series = new(daily.Base.Revision, Guid.Empty) } })
                Reject("index_fixing_definition", () => daily.Floating(daily.Fixings.SetItem(0, rule)));
        });
        Check("a forged quote specification cannot reprice an actual under a retained quote key", () =>
        {
            var side = daily.Floating(quote: daily.Quote with { Multiplier = ExactDecimal.Parse("2") });
            Reject("swap_definition", () => Value(daily, snapshot, daily.Swap(side)));
        });
        Check("shared market shocks change future resets and leave actual values and lineage unchanged", () =>
        {
            var build = daily.Model.Build(snapshot, swap.DependenciesAt(snapshot));
            var bump = new QuoteBump(new(daily.Series[2].Key, InputUse.Variable), daily.Quote.Key, PriceUnit.FromQuote(daily.Quote), ExactDecimal.Parse("0.1"), QuoteBump.Convention);
            var pair = daily.Model.Scenarios(build, [bump]).Single();
            InstrumentValuation Price(CurveBuild market) => swap.PV(new SideValuationContext(market, new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted)));
            Equal(Price(pair.Up).PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString(), "18.1");
            Equal(Price(pair.Down).PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString(), "17.9");
            var actual = ((PaymentSideValuation)Price(pair.Up).Sides[1].Value).Payments[0].Prices[0];
            Equal(actual.Price.Value.ToString(), "10");
            Equal(actual.Lineage.Single(), snapshot.Selections.Single(s => s.Input == new InputKey(daily.Series[0].Key, InputUse.FrozenFact)).Observation.Key);
            Reject("risk_factor", () => daily.Model.Scenarios(build, [bump with { Input = new(daily.Series[0].Key, InputUse.FrozenFact) }]).ToArray());
        });
        Check("due payments retain lifecycle rejection", () =>
        {
            Reject("side_payment_due", () => Value(daily, daily.Snapshot(daily.Payment, 3), swap));
            Equal(DefinitionIdentity.For(swap.Floating).Version, "FloatingSide_v8");
        });
        Check("new indexed economic identity includes the qualified schedule but excludes selected actual values", () =>
        {
            var original = DefinitionIdentity.For(swap.Floating);
            var changed = daily.Floating(daily.Fixings.SetItem(0, daily.Fixings[0] with { Source = daily.Base.R("other_source") }));
            if (DefinitionIdentity.For(changed) == original) throw new Exception("Actual source was omitted from identity.");
            if (DefinitionIdentity.For(daily.Floating(daily.Fixings.SetItem(1, daily.Fixings[1] with { FixingAt = at.AddHours(1) }))) == original)
                throw new Exception("An independently dated fixing was omitted from identity.");
            Equal(DefinitionIdentity.For(swap.Floating), original);
        });
        Check("one period selector preserves explicit target and override prices across live, expiry and fixing states", () =>
        {
            var f = new FixingFixture(monthly:true,delayedMonthlyPublication:true);
            var contract = f.Projections[0].Contract;
            var target = PriceSettlementSelection.CurveTarget(f.Base.R("index"));
            var alternate = new PriceSettlementSelection(PriceSettlementMode.Override,target.SelectedIndex,f.Base.R("alternate_index"),PriceSettlementSelection.ForwardCurveNoBasis);
            var alternateContract = contract with { Key=new(f.Base.Revision,Guid.NewGuid()) };
            var alternateSeries = new MarketSeries(new(f.Base.Revision,Guid.NewGuid()),alternateContract.Key,f.Quote.Key,null);
            var alternateFixing = f.Fixings[0] with { Index=alternate.SelectedIndex,Series=alternateSeries.Key };
            var model = new CurveModel(f.Base.Revision,f.Base.Reference,f.Base.Quantities,[f.Quote],[contract,alternateContract],[f.Curve],[],[],f.Series.Add(alternateSeries));
            IndexPriceLookup Lookup(PriceSettlementSelection selection) => new(selection,f.Quote,f.Projections[0].Market,contract,null,
                selection.Mode==PriceSettlementMode.Override?alternateFixing:f.Fixings[0]);
            var forwardSnapshot = f.Snapshot(contract.Expiry.AddTicks(-10));
            var forward = model.Build(forwardSnapshot,[f.Projections[0].Market]);
            var live = PeriodPriceSelector.Admit(forward,Lookup(alternate));
            Equal(live.Projection,f.Projections[0].Market);Equal(live.FixingPrice,null);
            var graph=new ScheduleGraphBuilder(forward.Graph,forwardSnapshot,new(64,128,128,1024));
            var source=graph.Price(forward.Graph.Plan.Points.Values.Single(p=>p.Key.Curve==f.Curve.Key).Key);
            var selectorNode=graph.PeriodPrice(source,live);var selectorBase=graph.Compile().Base();
            Equal(selectorBase.Price(selectorNode).ToExactString(),"11");
            var shifted=selectorBase.WithMarketShifts([new(OpenEtrm.MarketData.Calculation.CurveShockCoordinate.MarketQuote,
                new OpenEtrm.MarketData.Calculation.ParallelCurveTarget(f.Curve.Key),PriceUnit.FromQuote(f.Quote),ExactDecimal.Parse("2"),OpenEtrm.MarketData.Calculation.CurveShift.ParentAdditive)]);
            Equal(shifted.Price(selectorNode).ToExactString(),"13");Equal(selectorBase.Price(selectorNode).ToExactString(),"11");
            var context = new SideValuationContext(forward,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
            Equal(context.Resolve(new SchedulePriceTerm(ExactDecimal.Parse("1"),null,Lookup(alternate))).Price.Value.ToString(),"11");
            Reject("source_expired",()=>PeriodPriceSelector.ProjectionAt(Lookup(alternate),f.Snapshot(contract.Expiry)));
            Reject("price_settlement_selection",()=>PeriodPriceSelector.ProjectionAt(Lookup(alternate) with { Fixing=null },forwardSnapshot));
            Reject("price_settlement_selection",()=>new PriceSettlementSelection(PriceSettlementMode.Override,target.SelectedIndex,target.SelectedIndex,PriceSettlementSelection.ForwardCurveNoBasis));
            var due = f.Snapshot(alternateFixing.FixingAt,1);
            due = due with { Selections=due.Selections.Add(new(new(alternateSeries.Key,InputUse.FrozenFact),
                new(new(f.Base.Revision.CatalogId,Guid.NewGuid()),alternateSeries.Key,alternateFixing.Source,ObservationRole.IndexFixing,PublicationState.Final,alternateFixing.FixingAt,ExactDecimal.Parse("17")))) };
            var fixedMarket = model.Build(due,[]);
            Equal(PeriodPriceSelector.Admit(fixedMarket,Lookup(target)).FixingPrice!.ToExactString(),"10");
            var alternateActual = PeriodPriceSelector.Admit(fixedMarket,Lookup(alternate));
            Equal(alternateActual.FixingPrice!.ToExactString(),"17");Equal(alternateActual.Projection,null);
            var period = new OpenEtrm.MarketData.Power.PeriodContractCurve(f.Curve.Key,PriceUnit.FromQuote(f.Quote),target.SelectedIndex,[contract],[f.Fixings[0],alternateFixing]);
            var prepared = OpenEtrm.MarketData.Power.PreparedCurve.Prepare(period,[(Fixture.Utc("2041-01-01T00:00:00Z"),Fixture.Utc("2041-02-01T00:00:00Z"))],[],[],due.ValuationAt,due.MarketCutoff);
            var resolver = new OpenEtrm.MarketData.Power.IntervalPriceResolver(fixedMarket,new Dictionary<CurveKey,OpenEtrm.MarketData.Power.PreparedCurve>{{period.Key,prepared}});
            Equal(resolver.Prices(period.Key,new(2041,1,1),target).Single().Price!.ToExactString(),"10");
            Equal(resolver.Prices(period.Key,new(2041,1,1),alternate).Single().Price!.ToExactString(),"17");
            Equal(resolver.Prices(period.Key,new(2041,1,1),alternate).Single().PeriodSelection!.Settlement,alternate);
            Equal(prepared.Dependencies(new(2041,1,1),alternate).Length,0);
            var laterTarget=period with { Fixings=[f.Fixings[0] with { FixingAt=due.ValuationAt.AddDays(1) },alternateFixing] };
            var overridePrepared=OpenEtrm.MarketData.Power.PreparedCurve.Prepare(laterTarget,
                [(Fixture.Utc("2041-01-01T00:00:00Z"),Fixture.Utc("2041-02-01T00:00:00Z"))],[],[],due.ValuationAt,due.MarketCutoff);
            Equal(overridePrepared.Dependencies(new(2041,1,1),alternate).Length,0);
            var overrideResolver=new OpenEtrm.MarketData.Power.IntervalPriceResolver(fixedMarket,
                new Dictionary<CurveKey,OpenEtrm.MarketData.Power.PreparedCurve>{{period.Key,overridePrepared}});
            Equal(overrideResolver.Prices(period.Key,new(2041,1,1),alternate).Single().Price!.ToExactString(),"17");
            Reject("source_expired",()=>overridePrepared.Dependencies(new(2041,1,1),target));
        });
        return count;
    }

    internal sealed class FixingFixture
    {
        public Fixture Base { get; } = new();
        public QuoteSpecification Quote { get; }
        public CurveModel Model { get; }
        public ImmutableArray<SwapProjection> Projections { get; }
        public ImmutableArray<IndexFixing> Fixings { get; }
        public ImmutableArray<MarketSeries> Series { get; }
        public Curve Curve { get; }
        public DateOnly From { get; } = new(2041, 1, 1);
        public DateOnly To { get; }
        public DateTimeOffset Payment { get; } = Fixture.Utc("2041-02-18T00:00:00Z");
        public FixingFixture(bool monthly = false,bool delayedMonthlyPublication=false)
        {
            Quote = Base.Quotes[0];
            To = monthly ? From.AddMonths(1) : From.AddDays(3);
            var contracts = Enumerable.Range(0, monthly ? 1 : 3).Select(n =>
                new MarketContract(new(Base.Revision, Guid.NewGuid()), ContractKind.Forward,
                    From.AddDays(n), monthly ? To : From.AddDays(n + 1),
                    monthly ? Fixture.Utc(delayedMonthlyPublication?"2041-02-02T00:00:00Z":"2040-12-31T00:00:00Z") : Fixture.Utc("2041-01-01T12:00:00Z").AddDays(n), null)).ToImmutableArray();
            Series = contracts.Select(c => new MarketSeries(new(Base.Revision, Guid.NewGuid()), c.Key, Quote.Key, null)).ToImmutableArray();
            var curve = Curve = new Curve(Base.C("fixing_projection"), Quote.Key, Base.R("location"), Base.R("uniform"),
                new(SettlementKind.Index, Base.R("index"), null), CurveRule.Direct,
                contracts.Select(c => c.Key).ToImmutableArray(), [], ObservationRole.TraderMark, Base.R("source"), null);
            Model = new(Base.Revision, Base.Reference, Base.Quantities, [Quote], contracts, [curve], [], [], Series);
            Projections = contracts.Select(c => new SwapProjection(new(curve.Key, c.Key), c)).ToImmutableArray();
            Fixings = contracts.Select((c, n) => new IndexFixing(Base.R("index"), Quote.Key, c.DeliveryFrom, c.DeliveryTo,
                Fixture.Utc(delayedMonthlyPublication?"2041-02-02T12:00:00Z":"2041-01-01T12:00:00Z").AddDays(n), Series[n].Key, Base.R("actual_source"), IndexFixing.SupportedImplementation)).ToImmutableArray();
        }
        public MarketSnapshot Snapshot(DateTimeOffset at, int actuals = 0) => Base.Snapshot() with
        {
            Id = Guid.NewGuid(), ValuationAt = at, MarketCutoff = at,
            Selections = Series.Select((s, n) => new SelectedObservation(new(s.Key, InputUse.Variable),
                new(new(Base.Revision.CatalogId, Guid.NewGuid()), s.Key, Base.R("source"), ObservationRole.TraderMark,
                    PublicationState.Final, Fixture.Utc("2040-12-30T00:00:00Z"), ExactDecimal.Parse(((n + 1) * 11).ToString()))))
                .Concat(Series.Take(actuals).Select((s, n) => new SelectedObservation(new(s.Key, InputUse.FrozenFact),
                    new(new(Base.Revision.CatalogId, Guid.NewGuid()), s.Key, Base.R("actual_source"), ObservationRole.IndexFixing,
                        PublicationState.Final, Fixings[n].FixingAt, ExactDecimal.Parse(((n + 1) * 10).ToString()))))).ToImmutableArray()
        };
        public FloatingSide Floating(ImmutableArray<IndexFixing>? fixings = null, QuoteSpecification? quote = null) => new(Guid.NewGuid(), PriceSettlementSelection.CurveTarget(Base.R("index")), quote ?? Quote,
            new(ExactDecimal.Parse("1"), Quote.Quantity), From, To, fixings ?? Fixings, Payment,
            Projections.Length == 1 ? FloatingAveraging.MonthlyIndexPrice : FloatingAveraging.CalendarDayArithmetic,
            Projections, FloatingSide.IndexedImplementation);
        public FinancialSwapInstrument Swap(FloatingSide? floating = null) => new(Guid.NewGuid(),
            [new(new FixedSide(Guid.NewGuid(), new(ExactDecimal.Parse("15"), Quote), new(ExactDecimal.Parse("1"), Quote.Quantity),
                From, To, Payment, FixedSide.Implementation, Quote), ExactDecimal.Parse("-1")), new(floating ?? Floating(), ExactDecimal.Parse("1"))],
            FinancialSwapInstrument.Implementation, false);
    }
}
