using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.Shared.Numerics;

// The worked example of the monthly bill design, with totals summed by hand.
internal static class GasMonthlyBillChecks
{
    internal static void Run()
    {
        var f = new CommodityLookupFixture(1);
        var currency = f.Quote.Currency;
        Money M(string value) => new(ExactDecimal.Parse(value), currency);
        const int payment = 1, fee = 2;
        var first = new DateOnly(2021, 1, 1); var second = first.AddDays(1);
        GasBillExpectedDay[] expected = [new(payment, first), new(payment, second), new(fee, first), new(fee, second)];
        var determinations = new List<GasBillDetermination>();
        GasBillDetermination Determine(int slot, DateOnly day, int ordinal, string contribution, bool documented = false)
        {
            var d = new GasBillDetermination(Guid.NewGuid(), slot, day, ordinal, M(contribution), slot == payment ? GasBillDirection.Pay : GasBillDirection.Receive, documented);
            determinations.Add(d); return d;
        }
        GasBillAssessment Assess(bool billIssued = false) => GasMonthlyBill.Assess(expected, determinations, billIssued);
        void Issue() { for (var i = 0; i < determinations.Count; i++) determinations[i] = determinations[i] with { Documented = true }; }

        Determine(payment, first, 1, "-300"); Determine(fee, first, 1, "10");
        var incomplete = Assess();
        Equal(incomplete.State, "incomplete");
        Equal(string.Join(",", incomplete.Missing.Select(m => $"{m.CashSlot}:{m.Day:dd}")), "1:02,2:02");
        if (incomplete.Document is not null) throw new Exception("An incomplete month has no document.");
        Determine(payment, first, 2, "6");
        Equal(Assess().State, "incomplete");
        Determine(payment, second, 1, "-300"); Determine(fee, second, 1, "10");

        var ready = Assess();
        Equal(ready.State, "ready");
        var bill = ready.Document ?? throw new Exception("A complete month has a bill.");
        Equal(bill.Kind, "bill");
        Equal(bill.Lines.Length.ToString(), "5");
        Equal(bill.PayTotal.Amount.ToString(), "-594");
        Equal(bill.ReceiveTotal.Amount.ToString(), "20");
        // Lines are ordered by payment, day and ordinal, whatever order the facts arrive in.
        Equal(string.Join(",", bill.Lines.Select(l => $"{l.CashSlot}:{l.Day:dd}:{l.Ordinal}:{l.Amount.Amount}")), "1:01:1:-300,1:01:2:6,1:02:1:-300,2:01:1:10,2:02:1:10");
        determinations.Reverse();
        Equal(string.Join(",", Assess().Document!.Lines.Select(l => l.DeterminationId)), string.Join(",", bill.Lines.Select(l => l.DeterminationId)));

        Issue();
        var issued = Assess(true);
        Equal(issued.State, "issued");
        if (issued.Document is not null) throw new Exception("An issued month with nothing new has no document.");
        Determine(fee, second, 2, "-0.2");
        var adjustment = Assess(true);
        Equal(adjustment.State, "adjustment_ready");
        Equal(adjustment.Document!.Kind, "adjustment_note");
        Equal(adjustment.Document.Lines.Length.ToString(), "1");
        Equal(adjustment.Document.PayTotal.Amount.ToString(), "0");
        Equal(adjustment.Document.ReceiveTotal.Amount.ToString(), "-0.2");

        // A negative price keeps its payment's direction: the amount moves inside the pay total.
        determinations.Clear(); expected = [new(payment, first)];
        Determine(payment, first, 1, "5");
        Equal(Assess().Document!.PayTotal.Amount.ToString(), "5");
        Equal(Assess().Document!.ReceiveTotal.Amount.ToString(), "0");

        Reject(() => GasMonthlyBill.Assess([], [], false));                                   // nothing billable
        Reject(() => Assess(true));                                                           // a bill cannot exist over undocumented originals only
        determinations.Clear(); Determine(payment, first, 1, "5", documented: true);
        Reject(() => Assess());                                                               // a documented determination without a bill
        determinations.Clear(); Determine(payment, second, 1, "5");
        Reject(() => Assess());                                                               // a determination outside the expected days
        determinations.Clear(); Determine(payment, first, 1, "5");
        determinations.Add(determinations[0] with { Id = Guid.NewGuid(), Contribution = new(ExactDecimal.Parse("1"), new(currency.Revision, "other")) });
        Reject(() => Assess());                                                               // one currency
        determinations.Clear(); Determine(payment, first, 2, "5");
        Reject(() => Assess());                                                               // a correction without its original
        Console.WriteLine("PASS gas monthly bill: missing days, bill over every determination, separate pay and receive totals, adjustment note and refused inconsistencies");
    }
    static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected {expected}, got {actual}"); }
    static void Reject(Action action)
    { try { action(); } catch (MarketDataException) { return; } throw new Exception("Expected gas bill refusal"); }
}
