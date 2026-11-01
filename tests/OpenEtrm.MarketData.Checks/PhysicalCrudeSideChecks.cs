using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

internal static class PhysicalCrudeSideChecks
{
    internal static void Run()
    {
        var fixture = new CommodityLookupFixture(31, monthly: true, volume: true);
        var lookup = fixture.Swap(true, 0).Floating.GenerateSchedule().Rows[0].Prices[0].Lookup!;
        ReferenceKey R(string code) => new(fixture.Quote.Key.Revision, code);
        var quantity = new QuantitySpecification(fixture.Quote.Quantity, R("test_crude"), R("standard_volume"), R("unit"), R("volume"));
        var location = new CrudeDeliveryLocation(R("test_terminal"), R("test_custody_point"));
        CrudeValuationAssignment Assignment(ReferenceKey grade) => new(R("crude_assignment"), quantity.Commodity, grade, location,
            quantity, R("utc"), ExactDecimal.Parse("60"), "fahrenheit", lookup.Settlement, fixture.Quote, new(lookup.Market, lookup.Contract), lookup.Fixing!);
        DeliveryWindows(lookup.Contract);
        var assignment = Assignment(R("test_grade"));
        var from = new DateTimeOffset(2041, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(3);
        PhysicalCrudeSide Delivery(DateTimeOffset starts, DateTimeOffset ends, ExactDecimal tolerance = default) =>
            new(Guid.NewGuid(), assignment, starts, ends, tolerance);
        var physical = Delivery(from, to);
        var snapshot = fixture.Snapshot with { Selections = [..fixture.Snapshot.Selections.Select((s,n) => s with {
            Observation = s.Observation with { Value = ExactDecimal.Parse(n % 2 == 0 ? "80" : "0") } })] };
        snapshot = snapshot.WithKnowledge(Guid.NewGuid(), snapshot.ValuationAt);
        var market = fixture.Model.Build(snapshot, physical.DependenciesAt(snapshot));
        var discount = new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted);
        SideValuationContext Context(CurveBuild scenario, CrudeValuationAssignment selected) =>
            SideValuationContext.ForPhysicalCrude(market, scenario, discount, [selected]);
        var context = Context(market, assignment);
        var one = ExactDecimal.Parse("1"); var negative = ExactDecimal.Parse("-1"); var thousand = ExactDecimal.Parse("1000");
        FixedSide Cash(string price) => new(Guid.NewGuid(), new FlatPrice(new UnitPrice(ExactDecimal.Parse(price), fixture.Quote)),
            fixture.Quote, physical.UnitQuantity, [new(physical.DeliveryDateFrom, physical.DeliveryDateTo, to.AddDays(5), new(1,1), null)]);
        var purchase = new PhysicalCrudeInstrument(Guid.NewGuid(), physical, Cash("78"), Cash("0.5"), one, negative, negative);
        Equal(physical.PV(context).PresentValue.Scale(thousand), "80000");
        Equal(purchase.PV(context).PresentValue.Scale(thousand), "1500");
        Equal(new PhysicalCrudeInstrument(Guid.NewGuid(), physical, Cash("78"), Cash("0.5"), negative, one, negative).PV(context).PresentValue.Scale(thousand), "-2500");
        var definition = ScheduleDefinitionCompiler.Generate(physical);
        if (definition.Family != "crude" || definition.Roots.Any(r => r.Measure == "contractual_amount") ||
            !definition.Roots.Any(r => r.Measure == "physical_value") || definition.Grids.Single().Interval(0).Start != from ||
            definition.Grids.Single().Interval(0).End != to || physical.GenerateSchedule().Rows.Count != 1)
            throw new Exception("Crude must retain one total-volume physical obligation with exact UTC geometry.");
        var wrongLifecycle = new ScheduleDefinition(physical.Id,"crude",definition.EconomicIdentity,definition.PricingBindingIdentity,
            definition.SchemaVersion,definition.GeneratorVersion,definition.Rows.Select(r=>r.Operation=="output_guard"?
                r with { Parameters=r.Parameters.SetItem("lifecycle","future_gas_delivery_v1") }:r).ToArray(),definition.Edges,definition.Bindings,definition.Roots,definition.Ports,definition.Grids);
        Reject("side_schedule_program",()=>BookedSideProgram.Compile(wrongLifecycle));
        var bytes = BookedSideProgram.Compile(definition).Write();
        var loaded = BookedSideProgram.Read(bytes, new(BookedSideProgram.Digest(bytes), definition.Manifest, definition.Family, definition.EconomicIdentity, definition.PricingBindingIdentity));
        var sources = ScheduleMarketSources.ForPhysicalCrude(market, [assignment]);
        var replay = sources.Evaluate(loaded, physical.Id, market, discount);
        Equal(replay.PresentValue.Scale(thousand), "80000");
        if (replay.Contributions.SelectMany(c => c.States).Any(s => s.Kind != ComponentKind.Delivery)) throw new Exception("Physical crude became cash.");
        var factor = fixture.Model.QuoteFactors(snapshot).Single(f => f.Input == fixture.Selection(0, false).Input);
        var shock = fixture.Model.Scenarios(market, [new QuoteBump(factor.Input, factor.Quote, factor.Unit, one, QuoteBump.Convention)]).Single();
        Equal(purchase.PV(Context(shock.Up, assignment)).PresentValue.Scale(thousand), "2500");
        Equal(purchase.PV(Context(shock.Down, assignment)).PresentValue.Scale(thousand), "500");
        Equal(purchase.PV(context).PresentValue.Scale(thousand), "1500");
        Reject("crude_tolerance", () => Delivery(from,to,ExactDecimal.Parse("0.02")));
        Reject("crude_delivery_window", () => Delivery(to,from));
        Reject("crude_delivery_window", () => Delivery(from,from.AddMonths(1)));
        Reject("crude_delivery_window", () => Delivery(from.ToOffset(TimeSpan.FromHours(1)),to));
        Reject("side_market_sources", () => physical.PV(Context(market,Assignment(R("different_grade")))));
        Reject("crude_instrument", () => new PhysicalCrudeInstrument(Guid.NewGuid(),physical,Cash("78"),Cash("0.5"),one,negative,null));
        var started = new OpenEtrm.MarketData.Calculation.ResolvedCalculationContext(Guid.NewGuid(),"crude_started",from,from,from,from);
        Reject("crude_delivery_lifecycle", () => loaded.Inputs(started));
        Reject("crude_quantity", () => purchase.ValidateBinding(new(default,quantity.Key),null));
        Reject("crude_quantity", () => purchase.ValidateBinding(new(thousand,R("other_quantity")),null));
        Reject("crude_valuation_assignment", () => new CrudeValuationAssignment(assignment.Key,assignment.Commodity,assignment.Grade,
            location,quantity,assignment.Clock,ExactDecimal.Parse("15"),"celsius",lookup.Settlement,fixture.Quote,assignment.Projection,lookup.Fixing!));
        Reject("side_market_sources", () => ScheduleMarketSources.ForPhysicalCrude(market,[assignment,assignment]));
        var cubic = quantity with { Key=R("cubic_metres"), Unit=R("m3") };
        var mass = quantity with { Key=R("mass"), Unit=R("kg"), Dimension=R("mass_dimension") };
        var converter = new UnitConverter([new(quantity.Unit,quantity.Dimension,"bbl"),new(cubic.Unit,cubic.Dimension,"m³"),new(mass.Unit,mass.Dimension,"kg")],
            [new(quantity.Unit,cubic.Unit,ExactDecimal.Parse("158987294928"),ExactDecimal.Parse("1000000000000"))],[quantity,cubic,mass]);
        var converted = converter.Convert(new Quantity(thousand,quantity.Key),cubic.Key);
        if (converted.Amount.ToString()!="158.987294928" || converter.Convert(converted,quantity.Key).Amount!=thousand)
            throw new Exception("Standard barrels require exact reversible geometric conversion.");
        Reject("quantity_conversion", () => converter.Convert(new Quantity(thousand,quantity.Key),mass.Key));
        IndependentGrades(assignment,from,to);
        PortfolioRisk(fixture, purchase, market);
        Console.WriteLine("PASS physical crude: exact total-volume retained delivery, independent cash and fees, assignment pins, lifecycle refusal and symmetric shocks");
    }
    private static void PortfolioRisk(CommodityLookupFixture fixture, PhysicalCrudeInstrument purchase, CurveBuild market)
    {
        var members = purchase.Sides.Select(m => {
            var program = BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(m.Side));
            var bytes = program.Write();
            return new RetainedCrudeMember(m.Side.Id, m.Coefficient,
                BookedSideProgram.Read(bytes, new(BookedSideProgram.Digest(bytes), program.Document.ScheduleManifest,
                    program.Document.Family, program.Document.EconomicIdentity, program.Document.PricingBindingIdentity)));
        }).ToImmutableArray();
        var retained = new RetainedCrudeInstrument(purchase.Id, purchase.Delivery.Assignment, members);
        var financial = fixture.Swap(true, 0);
        var book = Guid.NewGuid();
        Trade Holding(Instrument instrument, string quantity) => new(Guid.NewGuid(), book,
            [new(Guid.NewGuid(), instrument, new(ExactDecimal.Parse(quantity), instrument.UnitQuantity.Specification))]);
        var portfolio = ValuationPortfolio.FromTrades([Holding(retained, "1000"), Holding(retained, "2000"), Holding(financial, "10")],
            new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted)));
        if (portfolio.Declare(market.Snapshot).Single(c => c.Id == retained.Id).ScheduleIdentity != retained.RetainedScheduleIdentity ||
            retained.RetainedScheduleIdentity is null)
            throw new Exception("Retained program identity must survive the common portfolio declaration.");
        var dependencies = portfolio.Declare(market.Snapshot).SelectMany(c => c.Dependencies).Distinct().ToImmutableArray();
        var export = fixture.Model.Compile(market.Snapshot, dependencies, false, market.Graph.Plan.Context);
        var bindings = dependencies.ToImmutableDictionary(r => r, r => export.Points[(r, InputUse.Variable)]);
        var prepared = RiskEngine.PrepareConnected(OpenEtrm.MarketData.Calculation.ConnectedValuation.Prepare(market.Graph, market.Snapshot), bindings, [portfolio], fixture.Model.Build(market.Snapshot, []));
        var request = market.Graph.Plan.Primitives.Single(p => p.Source.Input == fixture.Selection(0, false).Input).Source.Request;
        var bump = new ConnectedRiskBump(OpenEtrm.MarketData.Calculation.CurveShockCoordinate.MarketQuote,
            new OpenEtrm.MarketData.Calculation.ContractCurveTarget(request.Curve, request.Contract), new PriceUnit(purchase.Currency, purchase.UnitQuantity.Specification, purchase.Delivery.Assignment.Quote.Multiplier),
            ExactDecimal.Parse("1"), ConnectedRiskBump.Convention);
        var result = RiskEngine.CalculateExact(prepared, [bump], RiskEngine.ExactNumericalPolicy, detail: RiskDetail.Holdings);
        var group = result.Groups.Single();
        // Physical: 3000*(80-78-0.5)=4500. Independent financial swap: 10*(80-4)=760.
        Equal(group.BaseValues.Single().Value, "5260");
        var values = group.Factors.Single().Totals.Single().Values;
        Equal(values.Up, "8270"); Equal(values.Down, "2250");
        Equal(values.Delta.Value, "3010"); Equal(values.Gamma.Value, "0");
        if (result.Work.UniqueValuations != 2 || result.Work.PositionCount != 3)
            throw new Exception("Repeated crude holdings must share one retained unit valuation.");
        Equal(RiskEngine.CalculateExact(prepared, [], RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value, "5260");
        var stress = new OpenEtrm.MarketData.Calculation.CurveShift(bump.Coordinate, bump.Target, bump.Unit,
            ExactDecimal.Parse("2"), OpenEtrm.MarketData.Calculation.CurveShift.ParentAdditive);
        var stressed = RiskEngine.CalculateExact(RiskEngine.WithShifts(prepared, [stress]), [bump], RiskEngine.ExactNumericalPolicy);
        Equal(stressed.Groups.Single().BaseValues.Single().Value, "11280");
        Equal(stressed.Groups.Single().Factors.Single().Totals.Single().Values.Up, "14290");
        Equal(stressed.Groups.Single().Factors.Single().Totals.Single().Values.Down, "8270");
        Equal(RiskEngine.CalculateExact(prepared, [], RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value, "5260");
        Reject("native_program_context", () => RiskEngine.PrepareConnected(
            OpenEtrm.MarketData.Calculation.ConnectedValuation.Prepare(market.Graph, market.Snapshot), bindings, [portfolio]));
        Reject("crude_quantity", () => retained.ValidateBinding(new(default, retained.UnitQuantity.Specification), null));
        var reusedCash = new RetainedCrudeInstrument(Guid.NewGuid(), purchase.Delivery.Assignment,
            [members[0], members[1], new(members[1].SideId, ExactDecimal.Parse("1"), members[1].Program)]);
        var reusedPortfolio = ValuationPortfolio.FromTrades([Holding(reusedCash, "1000")], portfolio.Setup);
        var reusedRisk = RiskEngine.PrepareConnected(OpenEtrm.MarketData.Calculation.ConnectedValuation.Prepare(market.Graph, market.Snapshot),
            bindings, [reusedPortfolio], market);
        var reusedValues = RiskEngine.CalculateExact(reusedRisk, [bump], RiskEngine.ExactNumericalPolicy).Groups.Single();
        Equal(reusedValues.BaseValues.Single().Value, "80000");
        Equal(reusedValues.Factors.Single().Totals.Single().Values.Up, "81000");
        Equal(reusedValues.Factors.Single().Totals.Single().Values.Down, "79000");
    }
    private static void DeliveryWindows(MarketContract template)
    {
        var february = template with { DeliveryFrom = new(2040, 2, 1), DeliveryTo = new(2040, 3, 1) };
        var start = new DateTimeOffset(2040, 2, 29, 12, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2040, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var leapDay = new CrudeDeliveryWindow(start, end);
        var fullMonth = new CrudeDeliveryWindow(new(2040, 2, 1, 0, 0, 0, TimeSpan.Zero), end);
        var ordinaryFebruary = february with { DeliveryFrom = new(2041, 2, 1), DeliveryTo = new(2041, 3, 1) };
        if (!fullMonth.IsCoveredBy(february) || !CrudeDeliveryWindow.IsMonthlyForward(ordinaryFebruary) ||
            CrudeDeliveryWindow.IsMonthlyForward(february with { Kind = ContractKind.Future }) ||
            CrudeDeliveryWindow.IsMonthlyForward(february with { Interval = new(start, end, new(2040, 2, 29), 12, 0, 1) }))
            throw new Exception("Only a complete dated forward month qualifies as the delivery price contract.");
        _ = new CrudeDeliveryWindow(new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2000, 1, 2, 0, 0, 0, TimeSpan.Zero));
        _ = new CrudeDeliveryWindow(new(2099, 12, 31, 0, 0, 0, TimeSpan.Zero), new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        if (!leapDay.IsCoveredBy(february) || leapDay.DateFrom != new DateOnly(2040, 2, 29) ||
            leapDay.DateTo != new DateOnly(2040, 3, 1))
            throw new Exception("Leap-day delivery must preserve its exclusive midnight endpoint.");
        var intraday = new CrudeDeliveryWindow(start, start.AddTicks(10));
        if (intraday.DateTo != new DateOnly(2040, 3, 1) || !intraday.IsCoveredBy(february))
            throw new Exception("Intraday microsecond delivery must use the enclosing date envelope.");
        if (new CrudeDeliveryWindow(end, end.AddDays(1)).IsCoveredBy(february) ||
            new CrudeDeliveryWindow(start, end.AddTicks(10)).IsCoveredBy(february) ||
            leapDay.IsCoveredBy(february with { DeliveryFrom = new(2040, 2, 2) }) ||
            CrudeDeliveryWindow.IsMonthlyForward(february with { DeliveryTo = new(2040, 2, 29) }) ||
            CrudeDeliveryWindow.IsMonthlyForward(february with { DeliveryFrom = new(9999, 12, 1), DeliveryTo = new(9999, 12, 31) }))
            throw new Exception("Monthly coverage must refuse nonmonthly and out-of-contract windows.");
        Reject("crude_delivery_window", () => new CrudeDeliveryWindow(start, start));
        Reject("crude_delivery_window", () => new CrudeDeliveryWindow(end, start));
        Reject("crude_delivery_window", () => new CrudeDeliveryWindow(start.AddTicks(1), end));
        Reject("crude_delivery_window", () => new CrudeDeliveryWindow(start, end.AddTicks(1)));
        Reject("crude_delivery_window", () => new CrudeDeliveryWindow(start.ToOffset(TimeSpan.FromHours(1)), end));
        Reject("crude_delivery_window", () => new CrudeDeliveryWindow(new(1999, 12, 31, 0, 0, 0, TimeSpan.Zero), end));
        Reject("crude_delivery_window", () => new CrudeDeliveryWindow(start, new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(10)));
    }
    private static void IndependentGrades(CrudeValuationAssignment template,DateTimeOffset from,DateTimeOffset to)
    {
        var revision=template.Projection.Contract.Key.Revision;
        ReferenceKey R(string code)=>new(template.Key.Revision,code);
        var contracts=Enumerable.Range(0,2).Select(_=>template.Projection.Contract with{Key=new(revision,Guid.NewGuid())}).ToArray();
        var indices=new[]{R("grade_a_index"),R("grade_b_index")};
        var series=contracts.Select(c=>new MarketSeries(new(revision,Guid.NewGuid()),c.Key,template.Quote.Key,null)).ToArray();
        var curves=contracts.Select((c,n)=>new Curve(new(revision,"grade_"+n),template.Quote.Key,template.Location.CustodyPoint,R("uniform"),
            new(SettlementKind.Index,indices[n],null),CurveRule.Direct,[c.Key],[],ObservationRole.TraderMark,R("authored_source"),null)).ToArray();
        var at=new DateTimeOffset(2040,12,1,0,0,0,TimeSpan.Zero);
        var selections=series.Select(s=>new SelectedObservation(new(s.Key,InputUse.Variable),new(new(revision.CatalogId,Guid.NewGuid()),s.Key,
            R("authored_source"),ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse("80")))).ToImmutableArray();
        var snapshot=new MarketSnapshot(Guid.NewGuid(),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,selections).WithKnowledge(Guid.NewGuid(),at);
        var model=new CurveModel(revision,template.Key.Revision,[template.Quantity],[template.Quote],contracts,curves,[],[],series);
        var assignments=contracts.Select((c,n)=>new CrudeValuationAssignment(R("assignment_"+n),template.Commodity,R("grade_"+n),template.Location,
            template.Quantity,template.Clock,template.StandardTemperature,template.TemperatureScale,PriceSettlementSelection.CurveTarget(indices[n]),template.Quote,
            new(new(curves[n].Key,c.Key),c),new(indices[n],template.Quote.Key,c.DeliveryFrom,c.DeliveryTo,c.Expiry,series[n].Key,R("authored_source"),IndexFixing.SupportedImplementation))).ToImmutableArray();
        var sides=assignments.Select(a=>new PhysicalCrudeSide(Guid.NewGuid(),a,from,to,default)).ToArray();
        var basis=model.Build(snapshot,[..sides.SelectMany(s=>s.Dependencies)]);
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var factor=model.QuoteFactors(snapshot).Single(f=>f.Input.Series==series[0].Key);
        var shock=model.Scenarios(basis,[new QuoteBump(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("1"),QuoteBump.Convention)]).Single();
        var context=SideValuationContext.ForPhysicalCrude(basis,shock.Up,discount,assignments);
        Equal(sides[0].PV(context).PresentValue.Scale(ExactDecimal.Parse("1000")),"81000");
        Equal(sides[1].PV(context).PresentValue.Scale(ExactDecimal.Parse("1000")),"80000");
        Reject("side_market_sources",()=>sides[1].PV(SideValuationContext.ForPhysicalCrude(basis,basis,discount,[assignments[0]])));
    }
    private static void Equal(ExactMoney value,string expected)
    { if(value.ToExactString()!=expected) throw new Exception($"Expected {expected}, received {value.ToExactString()}."); }
    private static void Reject(string code,Action action)
    { try { action(); } catch(MarketDataException e) when(e.Code==code) { return; } throw new Exception("Expected "+code); }
}
