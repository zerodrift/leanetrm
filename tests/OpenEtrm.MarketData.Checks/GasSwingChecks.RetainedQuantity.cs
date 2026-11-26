using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private static SwingQuantityRight QuantityTerms(HeldFixture f) => Terms() with
    {
        Days = f.Terms.Days, Slots = f.Terms.Slots, LeadHours = 0, Upper = null, TotalBasis = null
    };

    private static RetainedGasInstrument Expanded(HeldFixture f, SwingQuantityRight right,
        ImmutableArray<RetainedGasMember> members = default, ExerciseMarks? marks = null) =>
        new(f.Instrument.Id, members.IsDefault ? f.Instrument.Members : members, f.Instrument.Assignments,
            right, Pricing with { NumericalPolicy = SwingUnitResources.Policy }, marks: marks ?? f.Instrument.Marks);

    private static void RetainedQuantityLifecycle()
    {
        var f = new HeldFixture(from: new(2041, 1, 30));
        var terms = QuantityTerms(f) with { Period = SwingQuantityRight.Month, ChangeCount = 1, CountRule = SwingQuantityRight.DiffersFromBase };
        var january = f.Instrument.Marks!.Volatilities.Single();
        var february = new CurveVolatilityMark(january.Curve, new(2041, 2, 1),
            new(Guid.NewGuid(), "0", VolatilityInput.Annualized));
        var marks = f.Instrument.Marks with { Volatilities = [january, february] };
        var instrument = Expanded(f, terms, marks: marks);
        var at = terms.Days[0].ExerciseAt;
        var held = f.State();
        var before = f.Inspect(held, at.AddTicks(-10), instrument);
        var assessed = before.QuantitySwing!;
        var pendingCash = PhysicalLifecycle.FromValuation(before).Rows.Single(r=>r.Slot==1);
        Require(pendingCash.Settlement == SettlementState.Remaining && pendingCash.Accrued is null && pendingCash.Outstanding is null &&
            pendingCash.Expected is null && pendingCash.Paid == f.Money("0") && pendingCash.MissingDays.IsEmpty,
            "A pending-only occurrence has no selected cash, payable balance or missing deadline.");
        Refuse("payment_state", () => pendingCash.RequireSwingEarnedPayment(at));
        Require(assessed.Comparison!.PlannedDays == 3 && assessed.Days.All(d => d.SelectedQuantity is null)
            && before.Sides.All(s => s.Contribution == f.Money("0")),
            "Prior to the first deadline all choices belong exclusively to the remaining right.");
        var result = f.Inspect(held, at, instrument);
        var right = result.QuantitySwing!;
        Require(right.Days.Select(d=>d.State).SequenceEqual(["missing_election","period_unassessed","undecided"])
            && right.Counted == f.Money("330") && right.Comparison!.Amount == f.Money("330")
            && right.Comparison.UnassessedDays == 2 && right.Comparison.PlannedDays == 1 && right.Days[2].PlannedQuantity == f.Quantity("1100")
            && result.Incomplete, "A missed January election blocks January's joint budget. February still values at 1100 × 0.30.");
        var views = result.Sides.Select((side,slot) => ((RetainedProgramValuation)side.Value).ViewInput($"entry{slot}",
            held.Quantity.Amount, slot==0?R(1):R(-1), [("quantity",ScheduleViewMeasure.Quantity),("amount",ScheduleViewMeasure.Amount)])).ToImmutableArray();
        var shown = ScheduleViews.Build(views, ["term","month","day"], f.Market.Clock.Boundary);
        Require(shown.UnassessedDays == 2 && shown.Totals["entry1.amount"].UnassessedDays == 2 && shown.Totals["entry1.amount"].Value is null &&
            shown.Totals[ScheduleViews.NetCash].UnassessedDays==2, "Schedule totals retain the distinct source days of a whole-term follower without reporting a planned cash amount.");
        var ordinary = f.Inspect(f.State([f.Elect(1,"1050")]), at, instrument);
        Require(ordinary.QuantitySwing!.Days[1].SelectedQuantity == f.Quantity("1050") &&
            PhysicalLifecycle.FromValuation(ordinary).Total == f.Money("15"),
            "An early January election remains an ordinary -315 even after another January deadline is missed.");
        var ordinaryCash = PhysicalLifecycle.FromValuation(ordinary);
        var futurePayment = ordinaryCash.Rows.Single(r => r.Slot == 1);
        var inspection = ordinary.Sides[1].Value.InspectSchedule(instrument.Members[1].Program.Document.ScheduleManifest,
            held.Quantity.Amount, R(-1), f.Market.Clock.Boundary).Read(0, 1).Rows.Single();
        Require(futurePayment.Expected == f.Money("-3150") && futurePayment.Accrued is null && futurePayment.Outstanding is null &&
            ordinaryCash.ExpectedCash.Single().Amount == f.Money("-3255") && ordinaryCash.ExpectedCash.Single().UnassessedDays == 1 && inspection.PresentValue == "-3150" &&
            inspection.Lifecycle.UnavailableReason == "missing_election",
            "Future elected consideration survives the missed day in expected cash and the held Side inspection.");
        var zeroFuture = PhysicalLifecycle.FromValuation(f.Inspect(f.State([f.Elect(1,"0")]), at,
            Expanded(f, terms with { Minimum = R(0) }, marks:marks)));
        Require(zeroFuture.ExpectedCash.Length == 1 && zeroFuture.ExpectedCash[0].Amount == f.Money("0") &&
            zeroFuture.ExpectedCash[0].UnassessedDays == 1, "An elected zero is a known cash subtotal on a partially assessed due date.");
        Refuse("exercise_marks", () => f.Inspect(held, at, Expanded(f, terms)));
        Require(result.UnassessedDays == 2 && PhysicalLifecycle.FromValuation(result).Rows.Single(r => r.Slot == 1).MissingDays.Length == 2,
            "A whole-term payment carries both unassessed January source days, counted once across all three entries.");
        var end = terms.Days[1].ExerciseAt;
        var receipt = new CashOccurrencePayment(Guid.NewGuid(), 1, 0, end, end, f.Money("-2500"));
        var known = f.State([f.Elect(0, "1050")], paid: [receipt]);
        var paid = PhysicalLifecycle.FromValuation(f.Inspect(known, end, instrument)).Rows.Single(r => r.Slot == 1);
        Require(paid.Settlement == SettlementState.PartPaid && paid.Accrued == f.Money("-3150") && paid.Outstanding == f.Money("-650"),
            "The elected ended day retains exact earned cash and its absolute payment alongside a missed second day.");
        var withdrawn = f.State(paid: [receipt]);
        var withdrawnValue = f.Inspect(withdrawn, end, instrument);
        var withdrawnCash = PhysicalLifecycle.FromValuation(withdrawnValue);
        var vanished = withdrawnCash.Rows.Single(r => r.Slot == 1);
        var withdrawnInspection = withdrawnValue.Sides[1].Value.InspectSchedule(instrument.Members[1].Program.Document.ScheduleManifest,
            held.Quantity.Amount, R(-1), f.Market.Clock.Boundary).Read(0, 1).Rows.Single();
        Require(withdrawnCash.ExpectedCash.IsEmpty && withdrawnInspection.PresentValue is null && withdrawnInspection.Paid == "-2500",
            "With no elected amount the old payment survives inspection without implying expected cash or a known PV.");
        Require(vanished.Settlement == SettlementState.UnassessedPaid && vanished.Receipts.Single() == receipt &&
            vanished.Paid == receipt.Amount && vanished.Accrued is null && vanished.Outstanding is null && vanished.Expected is null &&
            vanished.RealizedUnsettled is null, "A later known timely withdrawal preserves the paid lineage and leaves outstanding cash unknown.");
        var futureWithReceipt = PhysicalLifecycle.FromValuation(f.Inspect(f.State([f.Elect(1,"1050")], paid:[receipt]), end, instrument))
            .Rows.Single(r => r.Slot == 1);
        Require(futureWithReceipt.Expected == f.Money("-3150") && futureWithReceipt.Unrealized == f.Money("-3150") &&
            futureWithReceipt.Paid == receipt.Amount && futureWithReceipt.Receipts.Single() == receipt &&
            futureWithReceipt.Accrued is null && futureWithReceipt.Outstanding is null && futureWithReceipt.RealizedUnsettled is null,
            "An old receipt on withdrawn earnings cannot reduce the still-unearned elected future cash subtotal.");
        Refuse("payment_state", () => vanished.RequireSwingEarnedPayment(end));
        paid.RequireSwingEarnedPayment(end);
        var laterEnded = terms.Days[2].ExerciseAt;
        var later = PhysicalLifecycle.FromValuation(f.Inspect(f.State([f.Elect(1,"1050")]), laterEnded, instrument)).Rows.Single(r=>r.Slot==1);
        Refuse("payment_state", () => later.RequireSwingEarnedPayment(end));
        later.RequireSwingEarnedPayment(laterEnded);
        RetainedQuantityScaling();
        RetainedSelectionAndCoverage(f, terms, instrument, marks);
        RetainedCashoutCoverage();
        RetainedEndpointAccounting(f, instrument);
        Console.WriteLine("PASS retained quantity swing: exact Trade and Side scaling, mixed monthly strips, temporal selection, earned cash, withdrawal, incomplete coverage, risk and P&L");
    }

    private static void RetainedQuantityScaling()
    {
        var f = new HeldFixture(from: new(2041, 1, 30));
        var january = f.Instrument.Marks!.Volatilities.Single();
        var marks = f.Instrument.Marks with { Volatilities = [january, new(january.Curve, new(2041, 2, 1),
            new(Guid.NewGuid(), "0.4", VolatilityInput.Annualized))] };
        var premium = GasChargeSides.LumpSum(Guid.NewGuid(), new(ExactDecimal.Parse("5"), f.Market.Quote),
            f.Instrument.UnitQuantity, f.Market.Clock, f.Market.Clock.Boundary(f.Market.To).AddDays(2),
            SyntheticSideCalendars.Dated(false), f.Market.Quote);
        var atom = new SignedRatio(1, ExactDecimal.WorkingScale);
        foreach (var changes in new[] { 1, 2 })
        foreach (var holder in new[] { ExerciseHolder.Us, ExerciseHolder.Counterparty })
        foreach (var position in new[] { 1, -1 })
        {
            var terms = QuantityTerms(f) with { Holder = holder, Period = SwingQuantityRight.Month,
                QuantityPolicy = SwingRight.ElectedDeliveryQuantity, ChangeCount = changes, CountRule = SwingQuantityRight.DiffersFromBase };
            var sideSign = holder == ExerciseHolder.Us ? 1 : -1;
            SignedRatio? unitCounted = null;
            foreach (var size in new[] { R(1), R(10), atom, atom * R(2) })
            {
                var sizeRatio = new ExactRatio(size.Numerator, size.Denominator);
                var reference = size * R(1,3);
                ExactQuantity Native(SignedRatio q) => ExactQuantity.FromRatio(q * reference, f.Market.Quote.Quantity);
                var members = f.Instrument.Members.Select((m,i) => new RetainedGasMember(m.SideId,
                    sideSign == 1 ? m.Direction : default(ExactDecimal) - m.Direction, new(1, i == 2 ? 6 : 3),
                    m.Program, 0, i == 0 ? GasHeldQuantities.DeliverySchedulePolicy : null)).Append(new(premium.Id,
                    ExactDecimal.Parse("-1"), sizeRatio.Inverse(), Retain(premium, "-1").Program,
                    owned: new(ChargeBasis.LumpSum, f.Market.Clock, ExactRatio.One))).ToImmutableArray();
                var instrument = Expanded(f, terms, members, marks);
                var fact = f.Elect(0,"1000") with { Quantity = Native(R(21,20)), ExercisingParty = holder };
                var cut = new DeliveryQuantityEvidence("scheduled", Guid.NewGuid(), "Synthetic scheduled cut");
                var days = terms.Days.Select((d,i) => new HeldDayQuantity(0, d.Day, Native(R(1)), i==0 ? Native(R(9,10)) : Native(R(1)),
                    i==0 ? PhysicalLifecycle.ScheduledQuantity : PhysicalLifecycle.ContractQuantity, i==0 ? [cut] : [])).ToImmutableArray();
                var held = f.State([fact], size: ExactNumberText.Format(size * R(position)), days: days);
                var trade = new Trade(held.TradeId, held.BookId!.Value, [new(held.BindingId, instrument, held.Quantity)]);
                var orientation = R(position * sideSign);
                PortfolioValue Price(string second)
                {
                    var (market,setup) = f.Market.Prepare(instrument, held, f.Before, dailyPoints: ["3.20",second,"3.05"]);
                    var portfolio = ValuationPortfolio.FromTrades([trade], setup);
                    var declared = portfolio.Declare(market.Market.Snapshot);
                    var value = portfolio.PV(market.Market, declared);
                    var reuse = portfolio.PV(market.Market, declared, market.Market, value);
                    Require(reuse.UnitValuations == 0 && reuse.Positions.SequenceEqual(value.Positions), "Binding-specific no-shock reuse.");
                    return value;
                }
                var flat = Price("3.05");
                var exact = Price("3.050000000000000001");
                var right = exact.PositionQuantitySwings.Single()!;
                var counted = right.Counted!.Ratio;
                var januaryRatio = changes == 1 ? R(1) : R(11,10);
                var expectedStrip = atom * reference * orientation * januaryRatio;
                var comparison = right.Comparison!;
                Require(comparison.PlannedDays == 2 && comparison.Amount.Ratio == expectedStrip &&
                    right.Days[0].PlannedQuantity is null && right.Days[0].SelectedQuantity == Native(R(9,10)) &&
                    right.Days[0].Evidence.Last() == cut && right.Days[0].Election == fact &&
                    right.Days[1].PlannedQuantity == Native(januaryRatio) && right.Days[2].PlannedQuantity == Native(R(1)) &&
                    right.Work.Operations <= right.Estimate.Operations && right.Periods[0].ChangedDays == 1 && (changes == 1 ? right.Days[1].UnitPayoff!.Volatility is null : right.Days[1].UnitPayoff!.Volatility == D("0")),
                    "An off-grid election reserves the change. The cut enters ordinary Sides; the exact January strip and stochastic February day enter the comparison.");
                Require(exact.Positions.Single().Ratio - flat.Positions.Single().Ratio == expectedStrip &&
                    counted - flat.PositionQuantitySwings.Single()!.Counted!.Ratio == expectedStrip &&
                    exact.Positions.Single().Ratio == counted + orientation * reference * R(9,10) * R(15,100) - R(5 * position),
                    "Count once, preserve the exact sub-atom strip, the ordinary elected margin and the independent five-unit premium.");
                var cashAt = terms.Days[1].ExerciseAt;
                var amount = f.Money("1").Scale(reference * orientation * R(-2));
                var payment = new CashOccurrencePayment(Guid.NewGuid(),1,0,cashAt,cashAt,amount);
                var started = f.State([fact], size:ExactNumberText.Format(size * R(position)), days:days, paid:[payment]);
                var paid = PhysicalLifecycle.FromValuation(f.Inspect(started,cashAt,instrument)).Rows.Single(r=>r.Slot==1);
                Require(paid.Accrued!.Ratio == reference * orientation * R(-27,10) && paid.Paid == payment.Amount &&
                    paid.Outstanding!.Ratio == reference * orientation * R(-7,10) && paid.Receipts.Single() == payment,
                    "Proportional off-grid operational facts and absolute payments conserve exact accrued, paid and outstanding at either size and sign.");
                unitCounted ??= counted;
                Require(counted == unitCounted * size && counted.Numerator.Sign == position * sideSign,
                    "Full Trade PV preserves exact stochastic homogeneity for both holder and external signs at ordinary and atomic size.");
            }
        }
    }

    private static void RetainedSelectionAndCoverage(HeldFixture f, SwingQuantityRight terms, RetainedGasInstrument instrument, ExerciseMarks marks)
    {
        var at = terms.Days[0].ExerciseAt;
        var source = instrument.Members[0];
        var scale = new ExactRatio(1000,1);
        var contract = GasDailyQuantitySelection.ReadContract(source.Program, scale);
        var members = ImmutableArray.Create(new GasFollowedMember(0, contract, scale, GasHeldQuantities.DeliverySchedulePolicy));
        var observed = new GasQuantityObservation(Guid.NewGuid(), "allocated", "provisional", f.Quantity("5000"),
            "Synthetic allocation", "manual_daily_v1");
        ImmutableArray<GasQuantityHead> heads = [new(0, contract[0].Day, observed),
            new(0, contract[0].Day, observed with { Id = Guid.NewGuid(), Source = "facility_allocation_adoption_v1", Quantity = f.Quantity("0") })];
        var policy = terms with { QuantityPolicy = SwingRight.ElectedDeliveryQuantity };
        var days = GasHeldQuantities.Build(members, heads, f.Before, policy, []);
        Require(days[0].OperationalRecords.Length==2 && days[0].HasOperationalRecord && days[0].Source == PhysicalLifecycle.ContractQuantity,
            "Conflicting valid allocations remain operational facts without selecting either one on an unelected day.");
        Refuse("gas_quantity_selection", () => GasHeldQuantities.Build(members, heads, f.Before, policy, [f.Elect(0,"1050")]));
        foreach (var t in new[] { terms with { Period = SwingQuantityRight.Term }, terms with { ChangeCount = null, CountRule = null,
            Upper = R(31,10), TotalBasis = SwingQuantityRight.PerPeriod, Period = SwingQuantityRight.Term },
            terms with { ChangeCount = null, CountRule = null, Lower = R(21,10), TotalBasis = SwingQuantityRight.PerPeriod, Period = SwingQuantityRight.Term },
            terms with { ChangeCount = null, CountRule = null } })
        {
            var result = f.Inspect(f.State(days: days), at, Expanded(f,t,marks:marks));
            var dailyOnly = !t.HasTotal && t.ChangeCount is null;
            Require(result.UnassessedDays == (dailyOnly ? 1 : 3) &&
                (dailyOnly ? result.QuantitySwing!.Comparison!.PlannedDays == 2 : result.QuantitySwing is { Comparison:null, Counted:null }),
                "Each stated count or endpoint couples a term; daily bounds alone leave later days assessable.");
            var inspected = result.Sides[0].Value.InspectSchedule(source.Program.Document.ScheduleManifest, ExactDecimal.Parse("1000"),
                R(1), f.Market.Clock.Boundary).Read(0, 3).Rows;
            Require(inspected[0].Quantity is null && inspected[0].PresentValue is null, "A missed source day shows no selected quantity or ordinary PV.");
        }
        var extreme = marks with { Volatilities = [marks.Volatilities[0], marks.Volatilities[1] with {
            Volatility = new(Guid.NewGuid(),"1000000",VolatilityInput.Annualized) }] };
        Refuse("swing_numerical_scope", () => f.Inspect(f.State(),at,Expanded(f,terms,marks:extreme)));
        var future = f.Before.AddMinutes(20);
        var upcoming = f.State([f.Elect(0,"1050")], transition:future);
        Require(f.Inspect(upcoming,f.Before,instrument).QuantitySwing!.Days[0].State == PhysicalLifecycle.ElectedQuantity &&
            instrument.Declare(f.Context(upcoming,f.Before,instrument)).Time.To == future &&
            f.Inspect(f.State(),future,instrument).QuantitySwing!.Days[0].State == RetainedSwingValuation.Undecided &&
            f.Inspect(f.State(),at,instrument).QuantitySwing!.Days[0].State == RetainedSwingQuantityValuation.Missing,
            "A withdrawal published and effective before the deadline releases its election. Crossing the deadline changes it to missing.");
        var saved = f.State([f.Elect(2,"1050") with { EffectiveAt = future }]);
        Require(instrument.Declare(f.Context(saved,f.Before,instrument)).Time.To == future &&
            f.Inspect(saved,f.Before,instrument).QuantitySwing!.Days[2].Election is null &&
            f.Inspect(saved,future,instrument).QuantitySwing!.Days[2].SelectedQuantity == f.Quantity("1050"),
            "Known future-effective elections bound reuse and reserve only at effectiveness.");
        Refuse("valuation_time_coverage", () => f.Inspect(f.State(transition:future),future,instrument));
        Refuse("swing_right", () => Expanded(f,policy));
        Refuse("swing_right", () => Expanded(f,terms with { LeadHours=3 }));
        Refuse("swing_right", () => Expanded(f,terms with { Slots=[0,1] }));
        var v2 = instrument.EconomicIdentity;
        Require(v2 != f.Instrument.EconomicIdentity && v2 != Expanded(f,terms with { ChangeCount=0 },marks:marks).EconomicIdentity &&
            v2 == Expanded(f,terms,marks:marks).EconomicIdentity && v2.Canonical.Contains("gas_swing_quantity_right_v1",StringComparison.Ordinal),
            "The retained economic identity contains the complete versioned normalized right.");
    }

    private static void RetainedCashoutCoverage()
    {
        var f = new GasImbalanceLifecycleChecks.CashoutValuationFixture();
        var original = f.Instrument();
        var terms = Terms(4, f.From) with { Upper = null, TotalBasis = null, Minimum = R(0), LeadHours = 0,
            Days = [..Enumerable.Range(0,4).Select(i => new SwingDay(f.From.AddDays(i), f.Clock.Boundary(f.From.AddDays(i))))] };
        var curve = original.Assignments[0].Projection.Market.Curve;
        var marks = new ExerciseMarks([new(curve, f.From, new(Guid.NewGuid(),"0",VolatilityInput.Annualized))], [], ExerciseMarks.HeldLogReturn);
        var instrument = new RetainedGasInstrument(original.Id, original.Members, original.Assignments, terms,
            Pricing with { NumericalPolicy = SwingUnitResources.Policy }, original.PriceLookups, marks:marks);
        var fact = new SwingElection(0,terms.Days[0].Day, ExactQuantity.FromQuantity(new(ExactDecimal.Parse("0"),f.Quote.Quantity)),
            ExerciseHolder.Us, terms.Days[0].ExerciseAt,terms.Days[0].ExerciseAt,Guid.NewGuid(),"Synthetic accepted zero");
        var at = f.Clock.Boundary(f.From.AddDays(2));
        var pay = new CashOccurrencePayment(Guid.NewGuid(),1,0,at,at,ExactMoney.Parse("-25",original.Currency));
        PhysicalLifecycleValue Value(string? statement, ImmutableArray<SwingElection> facts)
        {
            var state = f.State(original, f.Balances("300",statement,null,null), [pay]);
            return f.Value(instrument,new(state.TradeId,state.BindingId,state.Quantity,state.BookId,state.Payments,PhysicalCashState.Policy,
                state.OwnerDays, balances:state.Balances,swingElections:facts), endedDays:2);
        }
        var value = Value(null,[fact]);
        var known = value.Rows.Single(r=>r.Slot == 1);
        Require(value.UnassessedDays == 2 && known.Settlement == SettlementState.Overpaid &&
            known.Outstanding == ExactMoney.Parse("25",original.Currency) && known.FirstSwingEarnedAt == f.Clock.Boundary(f.From.AddDays(1)) &&
            value.MissingAssessments.Count(m=>m.Day==f.From.AddDays(1)) == 2 &&
            value.MissingAssessments.Where(m=>m.Day==f.From.AddDays(1)).Select(m=>m.Owner).ToHashSet().SetEquals(["swing_right","reported_balance"]),
            "D454 and swing share one missing source date while retaining both owners. Elected zero preserves the known overpayment.");
        var observed = Value("300", [fact]);
        var sharedDay = f.From.AddDays(1);
        Require(observed.UnassessedDays == 2 && Value(null,[]).UnassessedDays == 3 &&
            observed.Rows.Single(r => r.Slot == 2 && r.DeliveryFrom == sharedDay).ReportedQuantity?.State == ReportedDayQuantity.Assessed &&
            observed.MissingAssessments.Where(m => m.Day == sharedDay).Select(m => m.Owner).SequenceEqual(["swing_right"]),
            "An assessed balance clears only its own reason on the missing commercial day.");

        RetainedScenarioCoverage(f, instrument, terms, fact);
    }

    private static void RetainedScenarioCoverage(GasImbalanceLifecycleChecks.CashoutValuationFixture f,
        RetainedGasInstrument instrument, SwingQuantityRight terms, SwingElection fact)
    {
        var held = f.State(instrument, f.Balances("300", null, null, null));
        var riskState = new PhysicalCashState(held.TradeId, held.BindingId, held.Quantity, held.BookId, [], PhysicalCashState.Policy,
            held.OwnerDays, balances: held.Balances, swingElections: [fact]);
        var prepared = f.Prepare(instrument, riskState, 2, null, null).Risk;
        var point = instrument.Assignments[3].Projection.Market;
        var shock = new CurveShift(CurveShockCoordinate.MarketQuote, new ContractCurveTarget(point.Curve, point.Contract),
            PriceUnit.FromQuote(f.Quote), ExactDecimal.Parse("0.10"), CurveShift.ParentAdditive);
        var samples = new ConnectedRiskScenarioSet("synthetic_move", "1", ConnectedRiskScenarioSet.Independent,
            [new("fourth_day", "Fourth gas day price increase", [shock])]);
        var joint = RiskEngine.CalculateScenarios(prepared, samples, RiskEngine.ExactNumericalPolicy, detail: RiskDetail.Holdings)
            .Scenarios.Single().Groups.Single();
        var position = joint.Positions.Single();
        MissingDayAssessment[] swing = [new(0, f.From.AddDays(1), "swing_right", 0, "missing_election"),
            new(0, f.From.AddDays(2), "swing_right", 0, "missing_election")];
        MissingDayAssessment[] balance = [new(0, f.From.AddDays(1), "reported_balance", 0, "statement_unavailable")];
        void RequireOwners(int slot, ImmutableArray<MissingDayAssessment> missing)
        {
            var expected = slot < 2 ? swing : balance;
            Require(missing.Length == expected.Length && missing.ToHashSet().SetEquals(expected),
                "Each repriced entry retains precisely its missing dates, owner and reason.");
        }
        Require(joint.Totals.Single().UnassessedDays == 2 && position.BaseUnassessedDays == 2 && position.UnassessedDays == 2 && position.Incomplete &&
            position.Values.Pnl == ExactMoney.Parse("1100", instrument.Currency),
            "A priced fourth day changes the holding subtotal while its two missing source days are counted once.");
        var missingAtHolding = swing.Concat(balance).ToHashSet();
        Require(position.MissingAssessments.Length == 3 && position.BaseMissingAssessments.Length == 3 &&
            missingAtHolding.SetEquals(position.MissingAssessments) && missingAtHolding.SetEquals(position.BaseMissingAssessments),
            "Both holding snapshots retain the distinct swing and balance reasons sharing the second gas day.");
        var bump = new ConnectedRiskBump(shock.Coordinate, shock.Target, shock.Unit, shock.Size, ConnectedRiskBump.Convention);
        var symmetric = RiskEngine.CalculateExact(prepared, [bump], RiskEngine.ExactNumericalPolicy, detail: RiskDetail.Holdings)
            .Groups.Single().Factors.Single().Positions.Single();
        Require(symmetric.Entries.Length == 4 && symmetric.UnassessedDays == 2 && symmetric.Values.UpPnl == position.Values.Pnl,
            "Finite and joint shocks agree on the holding subtotal and the finite result retains all four entries.");
        foreach (var entry in symmetric.Entries)
        {
            RequireOwners(entry.Slot, entry.MissingAssessments);
            RequireOwners(entry.Slot, entry.UpMissingAssessments);
            RequireOwners(entry.Slot, entry.DownMissingAssessments);
        }

        var coupled = new RetainedGasInstrument(instrument.Id, instrument.Members, instrument.Assignments,
            terms with { ChangeCount = 1, CountRule = SwingQuantityRight.DiffersFromBase }, Pricing with { NumericalPolicy = SwingUnitResources.Policy },
            instrument.PriceLookups, marks:instrument.Marks);
        var allMissing = f.State(coupled, f.Balances("0", null, null, null));
        var allMissingRisk = f.Prepare(coupled, allMissing, 2, null, null).Risk;
        var books = RiskEngine.CalculateScenarios(allMissingRisk, samples, RiskEngine.ExactNumericalPolicy, detail: RiskDetail.Books)
            .Scenarios.Single().Groups.Single();
        var book = books.Totals.Single();
        Require(books.Positions.IsEmpty && book.Values.Value == ExactMoney.Parse("0", instrument.Currency) &&
            book.Incomplete && book.UnassessedDays == 4 && book.BaseUnassessedDays == 4,
            "An all-missing books-only scenario carries the incomplete marker directly on its zero subtotal.");
    }

    private static void RetainedEndpointAccounting(HeldFixture f, RetainedGasInstrument instrument)
    {
        var book = f.State().BookId!.Value;
        var trade = Guid.NewGuid();
        var binding = instrument.Id;
        var key = new AccountingPositionKey(trade,binding,book);
        var deadline = f.Terms.Days[0].ExerciseAt;
        var ended = f.Terms.Days[1].ExerciseAt;
        var publication = ended.AddMinutes(10);
        var receipt = new CashOccurrencePayment(Guid.NewGuid(),1,0,ended,ended,f.Money("-2500"));
        var accepted = f.Elect(0,"1050");
        var heads = f.State().OwnerDays;
        PnlAccountingPoint Endpoint(DateTimeOffset at, DateTimeOffset cutoff, ImmutableArray<SwingElection> selectedAtCutoff,
            bool checkRisk = false)
        {
            var held = new PhysicalCashState(trade,binding,new(ExactDecimal.Parse("1000"),f.Market.Quote.Quantity),book,
                [receipt],PhysicalCashState.Policy,heads,swingElections:selectedAtCutoff);
            var (prepared,setup) = f.Market.Prepare(instrument,held,at,dailyPoints:["3.20","2.80","3.40"],knownAt:cutoff);
            var context = prepared.Scenario.Plan.Context;
            var flows = receipt.KnownAt <= cutoff ? ImmutableArray.Create(new AccountingFlow(receipt.Id,1,key,
                receipt.PaidAt,receipt.KnownAt,receipt.Amount,AccountingFlowKind.EconomicCash)) : [];
            var facts = new BindingAccountingFacts(key,context.ContextId,cutoff,flows);
            var setupWithCash = setup with { AccountingFacts = setup.AccountingFacts.Add(new(trade,binding),facts) };
            var portfolio = ValuationPortfolio.FromTrades([new Trade(trade,book,[new(binding,instrument,held.Quantity)])],setupWithCash);
            prepared = RiskEngine.PrepareConnected(prepared.Valuation,prepared.Bindings,[portfolio],prepared.IntervalBasis);
            if (checkRisk)
            {
                var risk = RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings);
                var position = risk.Groups.Single().BasePositions.Single();
                Require(position.Incomplete && position.QuantitySwing is { ModeledDays:1, Counted:not null } &&
                    position.Entries.Single(e=>e.Slot==1).MissingAssessments.Length == 2,
                    "The real risk holding retains missing days together with its separate assessed February model.");
                var second = new PhysicalCashState(Guid.NewGuid(),binding,held.Quantity,book,[],PhysicalCashState.Policy,heads);
                Trade Position(PhysicalCashState state) => new(state.TradeId,book,[new(binding,instrument,state.Quantity)]);
                var two = ValuationPortfolio.FromTrades([Position(held),Position(second)], setup with
                    { BindingStates=setup.BindingStates.Add(new(second.TradeId,binding),second) });
                var combined = two.PV(prepared.Market,two.Declare(prepared.Market.Snapshot));
                Require(combined.UnitValuations==2 && combined.UnassessedDays==4 && combined.Totals.Single().UnassessedDays==4 &&
                    combined.PositionUnassessedDays.SequenceEqual([2,2]), "Identical source dates on separate trades retain independent book coverage.");
            }
            return RiskEngine.CaptureAccounting(prepared);
        }
        var before = Endpoint(deadline.AddMinutes(-1),deadline.AddMinutes(-1),[]);
        var earlyKnown = Endpoint(deadline.AddMinutes(-1),publication,[]);
        Require(earlyKnown.Positions.Single().UnassessedDays==0 && earlyKnown.Flows.Single().EffectiveAt > earlyKnown.At,
            "Viewed before the deadline with later knowledge, the withdrawn right is pending and the known payment is still future-dated.");
        var original = Endpoint(ended,ended,[accepted]);
        var withdrawn = Endpoint(ended.AddMinutes(10),publication,[],true);
        var movement = TradingPnlAccounting.Compare(original,withdrawn,TradingPnlAccounting.Version1).Positions.Single();
        Require(before.Positions.Single().UnassessedDays==0 && before.Flows.IsEmpty && withdrawn.Positions.Single().MissingAssessments.Select(m=>m.Day).Distinct().Count()==2 && original.Flows.Single()==withdrawn.Flows.Single() &&
            original.Positions.Single().UnassessedDays==1 && movement.OpeningUnassessedDays==1 && movement.ClosingUnassessedDays==2 &&
            movement.PeriodCash == f.Money("0") && movement.PriorCashKnowledgeAdjustment == f.Money("0") &&
            movement.OpeningValue == f.Money("-425") && movement.ClosingValue == f.Money("330") &&
            Endpoint(ended,ended,[accepted]).Positions.Single().RemainingValue == f.Money("-425"),
            "The receipt persists across a later-published timely withdrawal. Original cutoff recognizes its original part-paid accrual.");
        var invalid = f.State(paid:[receipt with { PaidAt=deadline, KnownAt=deadline }]);
        Refuse("payment_state",()=>f.Inspect(invalid,ended,instrument));
    }
}
