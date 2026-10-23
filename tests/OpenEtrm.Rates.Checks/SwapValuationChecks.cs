using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Calculation;

internal static class SwapValuationChecks
{
    public static int Run()
    {
        var revision = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
        var reference = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        ReferenceKey R(string code) => new(reference, code);
        var quote = new QuoteSpecification(R("outright"), QuoteKind.Outright, R("currency"), R("energy"),
            ExactDecimal.Parse("1"), ExactDecimal.Parse("0.01"), ExactDecimal.Parse("0.01"),
            ExactDecimal.Parse("-1000"), ExactDecimal.Parse("1000"));
        var contract = new MarketContract(new(revision, Guid.NewGuid()), ContractKind.Forward,
            new(2021, 1, 1), new(2021, 2, 1), At("2021-02-01T00:00:00Z"), null);
        var curve = new Curve(new(revision, "monthly"), quote.Key, R("location"), R("uniform"),
            new(SettlementKind.Index, R("index"), null), CurveRule.Direct, [contract.Key], [],
            ObservationRole.TraderMark, R("source"), null);
        var commodity = new MarketSeries(new(revision, Guid.NewGuid()), contract.Key, quote.Key, null);
        var left = new SeriesKey(revision, Guid.NewGuid()); var right = new SeriesKey(revision, Guid.NewGuid());
        var model = new CurveModel(revision, reference,
            [new(R("energy"), R("gas"), R("energy_basis"), R("energy_unit"), R("energy_dimension"))],
            [quote], [contract], [curve], [],
            [new(R("rate"), NumericKind.Dimensionless, null, null, ExactDecimal.Parse("-0.1"), ExactDecimal.Parse("1"))],
            [commodity, new(left, null, null, R("rate")), new(right, null, null, R("rate"))]);
        SelectedObservation Selection(SeriesKey key, string price, ObservationRole role) =>
            new(new(key, InputUse.Variable), new(new(revision.CatalogId, Guid.NewGuid()), key, R("source"),
                role, PublicationState.Final, At("2020-12-18T12:00:00Z"), ExactDecimal.Parse(price)));
        var snapshot = new MarketSnapshot(Guid.NewGuid(), revision, At("2020-12-18T16:00:00Z"),
            At("2020-12-18T16:00:00Z"), InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [Selection(commodity.Key, "3.1", ObservationRole.TraderMark), Selection(left, "0.1", ObservationRole.Parameter),
                Selection(right, "0.2", ObservationRole.Parameter)]);
        var payment = At("2021-02-18T12:00:00Z"); var unit = new Quantity(ExactDecimal.Parse("1"), quote.Quantity);
        var instrument = new FinancialSwapInstrument(Guid.NewGuid(), [
            new(new FixedSide(Guid.NewGuid(), new UnitPrice(ExactDecimal.Parse("3"), quote), unit,
                contract.DeliveryFrom, contract.DeliveryTo, payment, FixedSide.Implementation,quote), ExactDecimal.Parse("-1")),
            new(new FloatingSide(Guid.NewGuid(), R("index"), quote, unit, contract.DeliveryFrom, contract.DeliveryTo,
                At("2020-12-31T12:00:00Z"), payment, FloatingAveraging.MonthlyIndexPrice,
                [new(new(curve.Key, contract.Key), contract)], FloatingSide.Implementation), ExactDecimal.Parse("1"))], FinancialSwapInstrument.Implementation, true);
        var mapping = new CurrencyDiscountCurve(Guid.NewGuid(), quote.Currency,
            new(new(revision, "discount"), R("simple_rates"), quote.Currency, "actual_365_fixed", "simple",
                RateConventions.Implementation, "valuation_utc_date", R("source"), [new(left, new(2021, 1, 18)), new(right, new(2021, 3, 18))]), DiscountInterpolation.Policy);
        FinancialSwapValue Price(MarketSnapshot context, bool discounting, CurrencyDiscountCurve? selected) =>
            FinancialSwapValuation.Price(new FinancialSwapInstrument(Guid.NewGuid(), instrument.Sides, FinancialSwapInstrument.Implementation, discounting), model.Build(context, instrument.Dependencies),
                new(ExactDecimal.Parse("100"), quote.Quantity), discounting, selected);
        var passed = 0;
        void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS independent side rates: " + name); }
        void Reject(string code, Action test)
        { try { test(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
        var fixedSide = (FixedSide)instrument.Sides[0].Side;
        var floating = (FloatingSide)instrument.Sides[1].Side;
        Check("fixed/floating preserves its published single-side pricing binding identity",()=>
        {
            if(instrument.PricingBindingIdentity!=instrument.Floating.PricingBindingIdentity)throw new Exception("Existing fixed/floating binding changed.");
        });
        Check("a connected custom monthly projection keeps rational schedule cash and discount evidence",()=>
        {
            var fix=new IndexFixing(R("index"),quote.Key,contract.DeliveryFrom,contract.DeliveryTo,At("2021-01-01T12:00:00Z"),commodity.Key,R("source"),IndexFixing.SupportedImplementation);
            var custom=new FloatingSide(Guid.NewGuid(),R("index"),quote,unit,contract.DeliveryFrom,contract.DeliveryTo,[fix],payment,
                FloatingAveraging.MonthlyIndexPrice,[new(new(curve.Key,contract.Key),contract)],FloatingSide.IndexedImplementation)
                .WithCustomProjection(new(revision.CatalogId,Guid.NewGuid(),1,"mean",0,"single_output_position_v1","published_custom_curve_projection_v1"));
            var swap=new FinancialSwapInstrument(Guid.NewGuid(),[new(fixedSide,ExactDecimal.Parse("-1")),new(custom,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,false);
            var market=model.Build(snapshot,[new(curve.Key,contract.Key)]);
            var context=new ResolvedCalculationContext(snapshot.Id,"synthetic-connected-swap",snapshot.ValuationAt,snapshot.MarketCutoff,snapshot.MarketCutoff,snapshot.MarketCutoff);
            var sourcePoint=new ConnectedCurvePoint(new(curve.Key,Guid.NewGuid()),contract.DeliveryFrom,contract.DeliveryTo,PriceUnit.FromQuote(quote),
                new ConnectedSource(new(curve.Key,contract.Key),new(commodity.Key,InputUse.Variable),snapshot.Selections[0].Observation,quote,commodity),contract);
            // 3.1 / 3 = 31/30, so receive 31/30 and pay 3 gives -59/30.
            var customCurve=new CurveKey(revision,"synthetic_custom_monthly");
            var zeroKey=new CurveKey(revision,"synthetic_zero");
            var zero=new ConnectedCurvePoint(new(zeroKey,Guid.NewGuid()),contract.DeliveryFrom,contract.DeliveryTo,sourcePoint.Unit,
                new ConnectedWeightedSum([new(CurvePointReference.Exact(sourcePoint.Key),default(ExactDecimal))]));
            var output=new ConnectedCurvePoint(new(customCurve,Guid.NewGuid()),contract.DeliveryFrom,contract.DeliveryTo,sourcePoint.Unit,
                new ConnectedWeightedMean([new(CurvePointReference.Exact(sourcePoint.Key),ExactDecimal.Parse("1")),new(CurvePointReference.Exact(zero.Key),ExactDecimal.Parse("2"))]));
            var plan=ConnectedCurves.Compile([new(curve.Key,[sourcePoint]),new(zeroKey,[zero]),new(customCurve,[output])],[output.Key],context,new VisibleConnectedCurves());
            var connected=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),snapshot);
            var bindings=ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(custom.Dependencies.Single(),output.Key);
            var result=FinancialSwapValuation.Evaluate(swap,connected,bindings,market,null);
            if(result.Instrument.PresentValue.Numerator!="-59"||result.Instrument.PresentValue.Denominator!="30")throw new Exception("Custom schedule PV was rounded or bypassed.");
            var cash=result.Sides.Single(side=>side.Contribution.Value.SideId==custom.Id).Payments.Single();
            if(cash.Cashflow.CashAmount.Numerator!="31"||cash.Cashflow.CashAmount.Denominator!="30"||cash.Discount.Enabled||
                result.Instrument.ScenarioId!=connected.Scenario.Id)throw new Exception("Connected exact cash, discount or scenario evidence was lost.");
            var scaled=result.Scale(unit);
            if(scaled.ExactPresentValue.Numerator!="-59"||scaled.ExactPresentValue.Denominator!="30"||scaled.NumericalPolicy!=FinancialSwapValue.ExactPolicy)
                throw new Exception("Trade scaling must retain the exact rational PV.");
            Reject("precision_loss",()=>{_ = scaled.PresentValue;});
            var multiplier=new NumericValue(ExactDecimal.Parse("0.1"),new(R("rate"),NumericKind.Dimensionless,null,null,ExactDecimal.Parse("-0.1"),ExactDecimal.Parse("1")));
            var scaledCurve=new CurveKey(revision,"synthetic_scaled_source");
            var scaledPoint=new ConnectedCurvePoint(new(scaledCurve,Guid.NewGuid()),contract.DeliveryFrom,contract.DeliveryTo,sourcePoint.Unit,
                new ConnectedBuiltCurve(CurveRule.ScaleAdd,CurvePointReference.Exact(sourcePoint.Key),CurvePointReference.Exact(zero.Key),multiplier,
                    new(new(left,InputUse.Variable),new(left,null,null,R("rate")),snapshot.Selections[1].Observation),quote,
                    new(snapshot.Id,snapshot.ValuationAt,snapshot.MarketCutoff,snapshot.SelectionPolicy,InputUse.Variable,false),CurveModel.NumericalPolicy),contract);
            var weightedOutput=output with{Expression=new ConnectedWeightedMean([new(CurvePointReference.Exact(scaledPoint.Key),ExactDecimal.Parse("1")),new(CurvePointReference.Exact(zero.Key),ExactDecimal.Parse("2"))])};
            var scaledPlan=ConnectedCurves.Compile([new(curve.Key,[sourcePoint]),new(zeroKey,[zero]),new(scaledCurve,[scaledPoint]),new(customCurve,[weightedOutput])],
                [output.Key],context,new VisibleConnectedCurves());
            var scaledResult=FinancialSwapValuation.Evaluate(swap,ConnectedValuation.Prepare(ConnectedScenario.Base(scaledPlan),snapshot),bindings,market,null);
            var price=scaledResult.Sides.Single(side=>side.Contribution.Value.SideId==custom.Id).Payments.Single().Cashflow.Prices.Single();
            if(price.ExactPrice.Numerator!="31"||price.ExactPrice.Denominator!="300"||
                !price.Lineage.SetEquals(new[]{snapshot.Selections[0].Observation.Key,snapshot.Selections[1].Observation.Key}))
                throw new Exception("A typed source multiplier must retain its exact numerical result and observation lineage.");
        });
        var value = Price(snapshot, true, mapping);
        Check("independent side PVs preserve the common-payment high-precision trade oracle", () =>
        {
            if (value.PresentValue.Amount.ToString() != "9.71118981067694871" || value.Sides.Length != 2 ||
                value.Instrument.Sides.Length != 2 || value.Quantity.Amount.ToString() != "100")
                throw new Exception("Incorrect independently summed trade PV");
            var fixedValue = value.Sides[0].Contribution.Value; var floatValue = value.Sides[1].Contribution.Value;
            if (fixedValue.Payments.Single().CashAmount.ToMoney().Amount.ToString() != "3" ||
                floatValue.Payments.Single().CashAmount.ToMoney().Amount.ToString() != "3.1" ||
                value.Instrument.Sides[0].Coefficient.ToString() != "-1" || value.Instrument.Sides[1].Coefficient.ToString() != "1")
                throw new Exception("Trade quantity or direction entered side valuation");
            Reject("precision_loss", () => floatValue.PresentValue.ToMoney());
            if (value.Instrument.PresentValue.Scale(ExactDecimal.Parse("100")).ToMoney() != value.PresentValue)
                throw new Exception("Trade quantity was not applied once after signed side PV aggregation");
        });
        Check("each side retains the actual valued schedule with its own lookup and payment identity", () =>
        {
            var row = value.Sides[1].Payments.Single().Cashflow;
            if (row.Plan.Key.SideId != floating.Id || row.Plan.PaymentAt != payment || row.Plan.Currency != quote.Currency ||
                row.Prices.Single().Price.Value.ToString() != "3.1" ||
                row.Prices.Single().Term.Lookup?.Market != new CurveRequest(curve.Key, contract.Key) ||
                row.Prices.Single().Lineage.Single() != snapshot.Selections[0].Observation.Key ||
                row.Plan.FirstFixingAt != At("2020-12-31T12:00:00Z") || row.Plan.Averaging != FloatingAveraging.MonthlyIndexPrice ||
                value.Sides[1].Contribution.Value.Snapshot != snapshot)
                throw new Exception("Floating side lost its actual schedule or exact lookup evidence");
            if (value.Sides[0].Contribution.Value.Schedule.Implementation != FixedSide.Implementation ||
                value.Sides[1].Contribution.Value.Schedule.Implementation != FloatingSide.Implementation ||
                value.Sides[0].Contribution.Value.NumericalPolicy != SideValuation.Policy || value.NumericalPolicy != FinancialSwapValue.ExactPolicy)
                throw new Exception("Missing side model or numerical policy");
        });
        Check("each payment retains currency default curve definition and actual pillar inputs", () =>
        {
            foreach (var side in value.Sides)
            {
                var row = side.Payments.Single(); var discount = row.Discount;
                if (discount.MappingId != mapping.Id || discount.Curve != mapping.Definition.Key || discount.Definition != mapping.Definition ||
                    discount.Currency != quote.Currency || discount.Snapshot != snapshot || discount.Pillars.Length != 2 ||
                    discount.Pillars[0].Maturity != new DateOnly(2021, 1, 18) || discount.Pillars[0].AnnualRate != "0.1" ||
                    discount.Pillars[0].DiscountFactor != "0.991578375441456126" || discount.Pillars[1].Maturity != new DateOnly(2021, 3, 18) ||
                    discount.Pillars[1].DiscountFactor != "0.953002610966057441" ||
                    discount.Factor.ToString() != "0.971118981067694871" || row.Cashflow.Discount.DiscountFactor != discount.Factor)
                    throw new Exception("Side payment lost discount construction evidence");
            }
        });
        Check("independent payment dates use different DFs before instrument aggregation", () =>
        {
            var laterFixed = new FixedSide(Guid.NewGuid(), fixedSide.Price, unit, contract.DeliveryFrom,
                contract.DeliveryTo, At("2021-03-18T12:00:00Z"), FixedSide.Implementation,quote);
            var separate = new FinancialSwapInstrument(Guid.NewGuid(), [new(laterFixed, ExactDecimal.Parse("-1")),
                new(floating, ExactDecimal.Parse("1"))], FinancialSwapInstrument.Implementation, true);
            var result = FinancialSwapValuation.Price(separate, model.Build(snapshot, separate.Dependencies),
                new(ExactDecimal.Parse("100"), quote.Quantity), true, mapping);
            if (result.PresentValue.Amount.ToString() != "15.14610084116817771" ||
                result.Sides[0].Payments.Single().Discount.Factor.ToString() != "0.953002610966057441" ||
                result.Sides[1].Payments.Single().Discount.Factor.ToString() != "0.971118981067694871" ||
                result.Sides[0].Payments.Single().Discount.Pillars.Length != 1)
                throw new Exception("Side payment dates were netted or shared a DF");
            Reject("swap_valuation", () => separate.Price(model.Build(snapshot, separate.Dependencies), unit,
                ExactDecimal.Parse("0.95"), FinancialSwapInstrument.Discounted));
        });
        Check("standalone rate curve and fixed-side path reject conflicting observation identities", () =>
        {
            var conflict = snapshot with { Selections = snapshot.Selections.SetItem(2, snapshot.Selections[2] with {
                Observation = snapshot.Selections[2].Observation with { Key = snapshot.Selections[1].Observation.Key } }) };
            Reject("observation_conflict", () => new ZeroRateCurve(mapping.Definition).Build(conflict));
            Reject("observation_conflict", () => new CurrencyDefaultDiscounting([fixedSide.GenerateSchedule()], conflict, true, mapping));
        });
        Check("standalone fixed side needs rate inputs but no built commodity data", () =>
        {
            var source = new CurrencyDefaultDiscounting([fixedSide.GenerateSchedule()], snapshot, true, mapping);
            var independent = fixedSide.PV(new SideValuationContext(snapshot, source));
            if (independent.PresentValue != value.Sides[0].Contribution.Value.PresentValue)
                throw new Exception("Fixed side depended on an instrument or commodity market");
            Reject("discount_context", () => fixedSide.PV(new SideValuationContext(snapshot with { ValuationAt = snapshot.ValuationAt.AddMinutes(1) }, source)));
        });
        Check("the same standalone side remains unchanged under a reversed instrument membership", () =>
        {
            var reverse = new FinancialSwapInstrument(Guid.NewGuid(), [new(fixedSide, ExactDecimal.Parse("1")),
                new(floating, ExactDecimal.Parse("-1"))], FinancialSwapInstrument.Implementation, true);
            var result = FinancialSwapValuation.Price(reverse, model.Build(snapshot, reverse.Dependencies),
                new(ExactDecimal.Parse("100"), quote.Quantity), true, mapping);
            if (result.PresentValue.Amount.ToString() != "-9.71118981067694871" ||
                result.Sides[0].Contribution.Value.PresentValue != value.Sides[0].Contribution.Value.PresentValue ||
                result.Sides[1].Contribution.Value.PresentValue != value.Sides[1].Contribution.Value.PresentValue)
                throw new Exception("A membership changed shared side PV semantics");
        });
        Check("changed rate inputs under the same snapshot identity reprice both independent sides", () =>
        {
            var changed = snapshot with { Selections = snapshot.Selections.SetItem(1, Selection(left, "0.11", ObservationRole.Parameter)) };
            if (Price(changed, true, mapping).PresentValue == value.PresentValue)
                throw new Exception("Snapshot identity incorrectly reused a prior side PV");
        });
        Check("explicit undiscounted valuation bypasses missing rates independently on both sides", () =>
        {
            var noRates = snapshot with { Selections = [snapshot.Selections[0]] };
            var result = Price(noRates, false, null);
            if (result.PresentValue.Amount.ToString() != "10" || result.Sides.Any(s => s.Payments.Any(p =>
                p.Discount.Enabled || p.Discount.MappingId is not null || p.Discount.Observations.Length != 0 ||
                p.Cashflow.Discount.Source != DiscountFactorSource.Undiscounted)))
                throw new Exception("An undiscounted side unexpectedly consumed rate inputs");
        });
        Check("a due payment reports its lifecycle rejection before any currency-default discount lookup", () =>
        {
            // No market input is requested: only the order of the two guards is under test.
            var due = snapshot with { ValuationAt = payment, MarketCutoff = payment };
            var market = model.Build(due, []);
            foreach (var (discounting, selected) in new[] { (false, (CurrencyDiscountCurve?)null), (true, mapping) })
            {
                var swap = new FinancialSwapInstrument(Guid.NewGuid(), instrument.Sides, FinancialSwapInstrument.Implementation, discounting);
                Reject("side_payment_due", () => FinancialSwapValuation.Price(swap, market, new(ExactDecimal.Parse("100"), quote.Quantity), discounting, selected));
                Reject("side_payment_due", () => FinancialSwapValuation.CreateDiscounting(swap, due, selected));
            }
            Reject("side_payment_due", () => new CurrencyDefaultDiscounting(instrument.Sides.Select(m => m.Side.GenerateSchedule()), due, false, null));
            // A later side's missing discount curve cannot hide another side's due payment.
            var later = new FixedSide(Guid.NewGuid(), fixedSide.Price, unit, contract.DeliveryFrom, contract.DeliveryTo,
                At("2021-03-18T12:00:00Z"), FixedSide.Implementation, quote);
            Reject("side_payment_due", () => new CurrencyDefaultDiscounting([later.GenerateSchedule(), floating.GenerateSchedule()], due, true, null));
        });
        Check("reusable payment inputs retain the supplied frozen context and each side's payment date", () =>
        {
            var frozen=snapshot with { Selections=snapshot.Selections.SetItem(0,snapshot.Selections[0] with {
                Input=snapshot.Selections[0].Input with { Use=InputUse.FrozenFact } }) };
            var laterFixed=new FixedSide(Guid.NewGuid(),fixedSide.Price,unit,contract.DeliveryFrom,
                contract.DeliveryTo,At("2021-03-18T12:00:00Z"),FixedSide.Implementation,quote);
            var separate=new FinancialSwapInstrument(Guid.NewGuid(),[new(laterFixed,ExactDecimal.Parse("-1")),
                new(floating,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,true);
            var factors=FinancialSwapValuation.CreateDiscounting(separate,frozen,mapping);
            if(!ReferenceEquals(factors.Snapshot,frozen)||factors.Inputs.Count!=2||
                factors.Resolve(laterFixed.GenerateSchedule(),frozen).Single().DiscountFactor.ToString()!="0.953002610966057441"||
                factors.Resolve(floating.GenerateSchedule(),frozen).Single().DiscountFactor.ToString()!="0.971118981067694871")
                throw new Exception("Reusable discounting replaced the exact frozen context or shared independent payment factors");
            Reject("discount_context",()=>factors.Resolve(floating.GenerateSchedule(),snapshot));
            var undiscounted=new FinancialSwapInstrument(Guid.NewGuid(),instrument.Sides,FinancialSwapInstrument.Implementation,false);
            var explicitInputs=FinancialSwapValuation.CreateDiscounting(undiscounted,frozen with { Selections=[frozen.Selections[0]] },null);
            if(explicitInputs.Inputs.Values.Any(p=>p.Enabled||p.Factor!=ExactDecimal.Parse("1")))
                throw new Exception("An explicitly undiscounted instrument required a rate default");
            Reject("discount_curve_missing",()=>FinancialSwapValuation.CreateDiscounting(separate,frozen,null));
        });
        Check("invalid trade quantities fail before a discount mapping or side valuation is consumed", () =>
        {
            var market = model.Build(snapshot, instrument.Dependencies);
            Reject("swap_quantity", () => FinancialSwapValuation.Price(instrument, market, new(default, quote.Quantity), true, null));
            Reject("swap_quantity", () => FinancialSwapValuation.Price(instrument, market, new(ExactDecimal.Parse("1"), R("wrong")), true, null));
        });
        Check("shared sides retain each instrument's explicit discounting in the common risk portfolio",()=>
        {
            var plain=new FinancialSwapInstrument(Guid.NewGuid(),instrument.Sides,FinancialSwapInstrument.Implementation,false);
            var book=Guid.NewGuid();
            RiskPosition Holding(FinancialSwapInstrument i)=>new(Guid.NewGuid(),Guid.NewGuid(),book,i.Id,
                new(ExactDecimal.Parse("100"),quote.Quantity),new(new Money(default,quote.Currency),quote.Quantity),new(default,quote.Currency));
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
            {InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty
                .Add(instrument.Id,FinancialSwapValuation.CreateDiscounting(instrument,snapshot,mapping))
                .Add(plain.Id,FinancialSwapValuation.CreateDiscounting(plain,snapshot,null))};
            var portfolio=new ValuationPortfolio([instrument,plain],[Holding(instrument),Holding(plain)],setup);
            var bump=new QuoteBump(new(commodity.Key,InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),ExactDecimal.Parse("0.01"),QuoteBump.Convention);
            var risk=RiskEngine.Calculate(model,snapshot,portfolio,[bump],RiskEngine.NumericalPolicy,detail:RiskDetail.Books);
            var total=risk.Factors.Single().Totals.Single();
            if(total.Base.Amount.ToString()!="19.71118981067694871"||total.UpPnl.Amount.ToString()!="1.971118981067694871"||
                total.Delta.Value.ToString()!="197.1118981067694871"||total.Gamma.Value!=default)
                throw new Exception("Instrument-specific discounting was replaced by another instrument sharing its sides.");
            Reject("side_context",()=>RiskEngine.Calculate(model,snapshot,
                new ValuationPortfolio([instrument,plain],portfolio.Positions,setup with{InstrumentDiscounting=setup.InstrumentDiscounting.Remove(plain.Id)}),
                [bump],RiskEngine.NumericalPolicy));
            Reject("side_context",()=>new ValuationPortfolio([instrument,plain],portfolio.Positions,
                setup with{Discounting=FinancialSwapValuation.CreateDiscounting(instrument,snapshot,mapping)}));
        });
        Check("side discount contexts require schedules and a selected snapshot", () =>
        {
            Reject("discount_context", () => new CurrencyDefaultDiscounting([], snapshot, false, null));
            Reject("discount_context", () => new CurrencyDefaultDiscounting([fixedSide.GenerateSchedule()], null!, false, null));
        });
        Check("par fixed price uses the independent floating PV and fixed payment discount factor", () =>
        {
            var early = new FixedSide(Guid.NewGuid(), new UnitPrice(ExactDecimal.Parse("0"), quote), unit,
                contract.DeliveryFrom, contract.DeliveryTo, At("2021-03-18T12:00:00Z"), FixedSide.Implementation, quote);
            var swap = new FinancialSwapInstrument(Guid.NewGuid(), [new(early, ExactDecimal.Parse("-1")),new(floating, ExactDecimal.Parse("1"))], FinancialSwapInstrument.Implementation, true);
            var par = FinancialSwapValuation.ParRate(swap,model.Build(snapshot,swap.Dependencies),mapping,new AmountFormat(12,CashTieRule.ToEven,false,true));
            // Independent oracle: 3.1 * floating DF / fixed DF, rounded at the selected boundary.
            if(par.Price.Value.ToString() != "3.158930318416")throw new Exception("Incorrect discounted par rate: "+par.Price.Value);
            var sameDate = FinancialSwapValuation.ParRate(instrument,model.Build(snapshot,instrument.Dependencies),mapping,new AmountFormat(12,CashTieRule.ToEven,false,true));
            if(sameDate.Price.Value.ToString()!="3.1")throw new Exception("Common payment DF did not cancel exactly");
        });
        Check("two floating sides value independently with opposite membership directions", () =>
        {
            var later = new FloatingSide(Guid.NewGuid(),R("index"),quote,unit,contract.DeliveryFrom,contract.DeliveryTo,
                At("2020-12-31T12:00:00Z"),At("2021-03-18T12:00:00Z"),FloatingAveraging.MonthlyIndexPrice,
                [new(new(curve.Key,contract.Key),contract)],FloatingSide.Implementation,spread:new UnitPrice(ExactDecimal.Parse("0.2"),quote));
            var swap = new FinancialSwapInstrument(Guid.NewGuid(),[new(later,ExactDecimal.Parse("-1")),new(floating,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,true);
            var result = FinancialSwapValuation.Evaluate(swap,model.Build(snapshot,swap.Dependencies),true,mapping);
            if(result.Instrument.PresentValue != value.Sides[1].Contribution.Value.PresentValue.Subtract(later.PV(new SideValuationContext(model.Build(snapshot,swap.Dependencies),new CurrencyDefaultDiscounting([later.GenerateSchedule()],snapshot,true,mapping))).PresentValue))throw new Exception("Floating sides were netted before discounting");
            Reject("swap_par_rate",()=>FinancialSwapValuation.ParRate(swap,model.Build(snapshot,swap.Dependencies),mapping,new AmountFormat(6,CashTieRule.ToEven,false,true)));
        });
        Check("identical reusable floating sides retain both memberships and offset exactly", () =>
        {
            var swap=new FinancialSwapInstrument(Guid.NewGuid(),[new(floating,ExactDecimal.Parse("-1")),new(floating,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,true);
            var result=FinancialSwapValuation.Evaluate(swap,model.Build(snapshot,swap.Dependencies),true,mapping);
            if(result.Sides.Length!=2||result.Instrument.PresentValue.ToMoney().Amount!=default)throw new Exception("Opposing memberships were lost");
            var conflicting=new FloatingSide(floating.Id,R("index"),quote,unit,contract.DeliveryFrom,contract.DeliveryTo,At("2020-12-31T12:00:00Z"),payment,FloatingAveraging.MonthlyIndexPrice,[new(new(curve.Key,contract.Key),contract)],FloatingSide.Implementation,spread:new UnitPrice(ExactDecimal.Parse("0.1"),quote));
            var invalid=new FinancialSwapInstrument(Guid.NewGuid(),[new(conflicting,ExactDecimal.Parse("-1")),new(floating,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,true);
            Reject("side_definition",()=>FinancialSwapValuation.Evaluate(invalid,model.Build(snapshot,invalid.Dependencies),true,mapping));
        });
        return passed;
    }

    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
    private sealed class VisibleConnectedCurves:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new MarketDataException("curve_binding","The synthetic test uses exact versions.");
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
}
