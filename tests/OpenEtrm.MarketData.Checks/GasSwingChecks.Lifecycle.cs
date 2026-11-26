using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private sealed class HeldFixture
    {
        internal readonly GasPricingScheduleFixture Market;
        internal readonly RetainedGasInstrument Instrument;
        internal SwingQuantityRight Terms => Instrument.QuantityRight!;
        internal DateTimeOffset Before => Terms.Days[0].ExerciseAt.AddHours(-1);
        internal HeldFixture(string? vol = "0", bool optional = false, int dayCount = 3, string payment = "3", string fee = "0.10",
            DateOnly? from = null)
        {
            var f = Market = new(size: 1000, dailyPaymentStart: from);
            var to = f.From.AddDays(dayCount);
            var period = new FixedSidePeriod(f.From, to, f.Clock.Boundary(to).AddDays(2), new(dayCount, 1), null)
                { DeliveryCompletionAt = f.Clock.Boundary(to) };
            RetainedGasMember Payment(string price) => Retain(new FixedSide(Guid.NewGuid(),
                new FlatPrice(new UnitPrice(ExactDecimal.Parse(price), f.Quote)), f.Quote, f.Instrument.UnitQuantity,
                [period], SyntheticSideCalendars.Dated(false), gasDeliveryClock: f.Clock), "-1");
            var right = GasSwingChecks.Terms(dayCount, f.From) with
            {
                Days = [..Enumerable.Range(0,dayCount).Select(i=>f.From.AddDays(i)).Select(d=>new SwingDay(d,f.Clock.Boundary(d)))],
                Slots = [0,1,2], LeadHours = 0, Lower = null, Upper = null, TotalBasis = null
            };
            var curve = f.Instrument.Assignments[0].Projection.Market.Curve;
            var marks = vol is null ? null : new ExerciseMarks([new(curve, new(f.From.Year, f.From.Month, 1),
                new(Guid.NewGuid(), vol, VolatilityInput.Annualized))], [], ExerciseMarks.HeldLogReturn);
            var original = f.Instrument.Members[0];
            var source = new RetainedGasMember(original.SideId, original.Direction, original.Ratio, original.Program, 0);
            if (dayCount == 1)
            {
                var assignment = f.Instrument.Assignments[0];
                var gas = new PhysicalGasSide(Guid.NewGuid(), assignment.Location, assignment.Product, new(f.Clock, f.From, to,
                    GasQuantityBasis.PerOperatingDay, f.Instrument.UnitQuantity, GasFuelConvention.None, default), [assignment], SyntheticSideCalendars.Physical());
                source = Retain(gas, "1");
            }
            Instrument = new(f.Instrument.Id, [source, Payment(payment), Payment(fee)],
                f.Instrument.Assignments, right, Pricing, marks: marks);
        }
        internal ExactQuantity Quantity(string value) => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(value), Market.Quote.Quantity));
        internal SwingElection Elect(int day, string value) => new(0, Terms.Days[day].Day, Quantity(value), ExerciseHolder.Us,
            Before, Before, Guid.NewGuid(), "Synthetic accepted day election");
        internal PhysicalCashState State(ImmutableArray<SwingElection> elections = default, string size = "1000",
            ImmutableArray<HeldDayQuantity> days = default, ImmutableArray<CashOccurrencePayment> paid = default,
            DateTimeOffset? transition = null)
        {
            var state = Market.State([]);
            var contract = Quantity(size.StartsWith('-') ? size[1..] : size);
            return new(state.TradeId, state.BindingId, new(ExactDecimal.Parse(size), Market.Quote.Quantity), state.BookId,
                paid.IsDefault ? [] : paid, PhysicalCashState.Policy, days.IsDefault ?
                    [..Terms.Days.Select(day => new HeldDayQuantity(0, day.Day, contract, contract, PhysicalLifecycle.ContractQuantity, []))] : days,
                swingElections: elections, nextSwingElectionEffectiveAt: transition);
        }
        internal UnitValuationContext Context(PhysicalCashState state, DateTimeOffset at, RetainedGasInstrument? instrument = null)
        {
            var (risk, setup) = Market.Prepare(instrument ?? Instrument, state, at, dailyPoints: Terms.Days.Length == 1 ?
                ["1", "1", "1"] : ["3.20", "2.80", "3.40"]);
            return new UnitValuationContext(risk.Market.Snapshot, setup, risk.Market).For(risk.UnitContracts.Single(), null, state);
        }
        internal InstrumentValuation Inspect(PhysicalCashState state, DateTimeOffset at, RetainedGasInstrument? instrument = null)
        {
            instrument ??= Instrument;
            var context = Context(state, at, instrument);
            var (basis, scenario) = context.RetainedProgramMarkets(instrument.Declare(context).Dependencies);
            return instrument.PV(instrument.PrepareValuation(basis, scenario,
                new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted), state));
        }
        internal PhysicalLifecycleValue Value(PhysicalCashState state, DateTimeOffset at) =>
            Instrument.EvaluateLifecycle(Context(state, at));
        internal ExactMoney Money(string value) => ExactMoney.Parse(value, Instrument.Currency);
    }

}
