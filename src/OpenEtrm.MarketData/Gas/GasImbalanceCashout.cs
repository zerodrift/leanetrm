using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenEtrm.MarketData.Physical;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public enum StatementBasis { DailyImbalance = 1, MonthToDateBalance }
public enum AccountNetting { NetAccounts = 1, EachAccount }

[EconomicDefinition("reported_balance_follow_v1")]
public sealed record ReportedBalanceFollow([property: IdentityField] string Profile, [property: IdentityField] bool OwedToUs,
    [property: IdentityField] StatementBasis Basis, [property: IdentityField] AccountNetting Netting,
    [property: IdentityField] string StatementPolicy)
{
    public const string DailyIndexProfile = "daily_index_cashout_v1";
    public void Require(string code)
    {
        if (Profile != DailyIndexProfile || !Enum.IsDefined(Basis) || !Enum.IsDefined(Netting) ||
            StatementPolicy is not (GasImbalanceTrading.FinalPolicy or GasImbalanceTrading.ReportedPolicy))
            throw new MarketDataException(code, "Select the daily index cash-out profile, statement basis, account netting and an installed statement policy.");
    }
}

// These are eligible, temporally resolved statements. Revision selection and the
// statement policy are applied before constructing this input, never by falling
// back to an older eligible revision here.
public sealed record AccountDayBalance(Guid AccountId, DateOnly Day, ExactQuantity Current,
    Guid StatementId, string Status, string Evidence);

public sealed record ImbalanceDay(DateOnly Day, string State, SignedRatio? OwedToUs,
    SignedRatio? OwedByUs, ImmutableArray<DeliveryQuantityEvidence> Evidence)
{
    public string? Reason { get; init; }
}

public static class GasImbalanceCashout
{
    public const string Assessed = "assessed", Unassessed = "unassessed", NotApplicable = "not_applicable";

    public static ImmutableArray<ImbalanceDay> Days(ImmutableArray<DateOnly> days, Func<DateOnly, bool> ended,
        Func<DateOnly, ImmutableArray<Guid>> accountsOn, ImmutableArray<AccountDayBalance> balances,
        StatementBasis basis, AccountNetting netting, ImmutableHashSet<DateOnly> monthsWithTransfers)
    {
        if (days.IsDefaultOrEmpty || days[0] == default || ended is null || accountsOn is null || balances.IsDefault ||
            basis is not (StatementBasis.DailyImbalance or StatementBasis.MonthToDateBalance) ||
            netting is not (AccountNetting.NetAccounts or AccountNetting.EachAccount) || monthsWithTransfers is null ||
            monthsWithTransfers.Any(month => month == default || month.Day != 1) ||
            days.Where((day, index) => day.DayNumber != days[0].DayNumber + index).Any())
            throw Invalid("Supply consecutive gas days, dated accounts and explicit statement basis and account netting.");

        var accounts = days.ToDictionary(day => day, accountsOn);
        RequireBalanceFacts(accounts, balances);
        var facts = balances.ToDictionary(balance => (balance.AccountId, balance.Day));
        var result = ImmutableArray.CreateBuilder<ImbalanceDay>(days.Length);
        foreach (var day in days)
        {
            if (!ended(day)) { result.Add(new(day, NotApplicable, null, null, []){Reason="day_not_ended"}); continue; }
            var month = new DateOnly(day.Year, day.Month, 1);
            if (basis == StatementBasis.MonthToDateBalance && monthsWithTransfers.Contains(month))
                throw new MarketDataException("gas_imbalance_transfer", "An accepted transfer makes the month-to-date cash-out ambiguous for that month.");

            var evidence = ImmutableArray.CreateBuilder<DeliveryQuantityEvidence>();
            var figures = new List<SignedRatio>();
            string? reason=accounts[day].IsEmpty?"no_account":
                basis == StatementBasis.MonthToDateBalance && days[0].Day != 1 && month <= days[0]?"partial_first_month":null;
            var complete=reason is null;
            foreach (var account in accounts[day])
            {
                if (!facts.TryGetValue((account, day), out var current)) { complete = false; reason??="statement_unavailable"; continue; }
                evidence.Add(Evidence(current));
                var figure = Quantity(current);
                if (basis == StatementBasis.MonthToDateBalance && day.Day != 1)
                {
                    if (!facts.TryGetValue((account, day.AddDays(-1)), out var previous)) { complete = false; reason??="preceding_day_unavailable"; continue; }
                    evidence.Add(Evidence(previous));
                    figure -= Quantity(previous);
                }
                figures.Add(figure);
            }
            if (!complete) { result.Add(new(day, Unassessed, null, null, evidence.ToImmutable()){Reason=reason}); continue; }

            var toUs = SignedRatio.Zero;
            var byUs = SignedRatio.Zero;
            foreach (var figure in netting == AccountNetting.NetAccounts ? [SignedRatio.Sum(figures)] : figures)
                if (figure.Numerator.Sign >= 0) toUs += figure;
                else byUs -= figure;
            result.Add(new(day, Assessed, toUs, byUs, evidence.ToImmutable()));
        }
        return result.MoveToImmutable();
    }

    private static SignedRatio Quantity(AccountDayBalance balance) => new(balance.Current.NumeratorValue, balance.Current.DenominatorValue);
    private static DeliveryQuantityEvidence Evidence(AccountDayBalance balance) => new("operator_statement_v1", balance.StatementId, balance.Evidence);
    internal static void RequireBalanceFacts(IReadOnlyDictionary<DateOnly, ImmutableArray<Guid>> accounts, ImmutableArray<AccountDayBalance> balances)
    {
        if (balances.IsDefault || accounts.Values.Any(ids => ids.IsDefault || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length) ||
            balances.Any(balance => balance is null || !accounts.TryGetValue(balance.Day, out var ids) || !ids.Contains(balance.AccountId) ||
                balance.Current is null || balance.StatementId == Guid.Empty || balance.Status is not ("final" or "provisional") ||
                string.IsNullOrWhiteSpace(balance.Evidence)) ||
            balances.Select(balance => balance.Current.Specification).Distinct().Count() > 1 ||
            balances.Select(balance => (balance.AccountId, balance.Day)).Distinct().Count() != balances.Length)
            throw Invalid("Supply one statement head per account and gas day in one quantity unit, with its revision and evidence.");
    }
    private static MarketDataException Invalid(string message) => new("gas_imbalance_input", message);
}

internal static class GasImbalanceCashoutEntries
{
    internal static void RequireRetained(ImmutableArray<RetainedGasMember> members)
    {
        foreach (var group in members.Where(member => member.Balance is not null).GroupBy(member => member.SourceSlot!.Value))
        {
            var pair = group.ToArray();
            if (pair.Length != 2 || pair.Count(member => member.Balance!.OwedToUs) != 1 ||
                pair[0].Balance! with { OwedToUs = false } != pair[1].Balance! with { OwedToUs = false } ||
                pair.Any(member => member.Ratio != members[group.Key].Ratio) ||
                RetainedShape(pair[0], members[group.Key]) != RetainedShape(pair[1], members[group.Key]))
                throw RetainedInvalid();
        }
    }

    private static (string Index, int PaymentDay) RetainedShape(RetainedGasMember member, RetainedGasMember source)
    {
        try
        {
            using var definition = JsonDocument.Parse(member.Program.Document.EconomicIdentity);
            var periods = definition.RootElement.GetProperty(nameof(FloatingSide.Periods));
            var days = PhysicalDeliverySchedule.ReadLiteralQuantities(source.Program, ExactRatio.One);
            if (periods.GetArrayLength() != days.Length || days.IsEmpty) throw RetainedInvalid();
            string? index = null; int? paymentDay = null;
            for (var ordinal = 0; ordinal < days.Length; ordinal++)
            {
                var period = periods[ordinal]; var day = days[ordinal];
                var from = day.OperatingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var to = day.OperatingDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var fixings = period.GetProperty(nameof(FloatingSidePeriod.Fixings));
                var allocation = period.GetProperty(nameof(FloatingSidePeriod.Allocation));
                var quantity = new SignedRatio(BigInteger.Parse(allocation[1].GetString()!, CultureInfo.InvariantCulture),
                    BigInteger.Parse(allocation[2].GetString()!, CultureInfo.InvariantCulture));
                var paidAt = period.GetProperty(nameof(FloatingSidePeriod.PaymentAt)).GetDateTimeOffset();
                if (period.GetProperty(nameof(FloatingSidePeriod.ObservationImplementation)).GetString() != FloatingSide.DeliveryIndexImplementation ||
                    period.GetProperty(nameof(FloatingSidePeriod.DeliveryFrom)).GetString() != from ||
                    period.GetProperty(nameof(FloatingSidePeriod.DeliveryTo)).GetString() != to ||
                    period.GetProperty(nameof(FloatingSidePeriod.DeliveryCompletionAt)).GetDateTimeOffset() != day.End ||
                    quantity != new SignedRatio(day.Quantity.NumeratorValue, day.Quantity.DenominatorValue) ||
                    fixings.GetArrayLength() != 1 || fixings[0].GetProperty(nameof(IndexFixing.From)).GetString() != from ||
                    fixings[0].GetProperty(nameof(IndexFixing.To)).GetString() != to || paidAt.Day is < 1 or > 28 ||
                    paidAt != PaymentDatePolicy.Start(new DateOnly(day.OperatingDate.Year, day.OperatingDate.Month, 1).AddMonths(1).AddDays(paidAt.Day - 1)))
                    throw RetainedInvalid();
                var selected = fixings[0].GetProperty(nameof(IndexFixing.Index)).GetRawText();
                if (index is not null && index != selected || paymentDay is not null && paymentDay != paidAt.Day) throw RetainedInvalid();
                index = selected; paymentDay = paidAt.Day;
            }
            return (index!, paymentDay!.Value);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or IndexOutOfRangeException)
        { throw RetainedInvalid(); }
    }

    private static MarketDataException RetainedInvalid() => new("gas_membership",
        "Retain a complete matching cash-out pair with whole-term daily index programs, exact source quantities and next-month payment dates.");

    internal static void Require(GasCashComponent entry, PhysicalGasSide source)
    {
        if (entry.Balance is not {} terms || entry.Role != "imbalance_cashout" || entry.Side is not FloatingSide side ||
            entry.Share != ExactDecimal.Parse("1") || entry.Part is not null || entry.Stage != GasQuantityStage.Delivery ||
            entry.Direction != ExactDecimal.Parse(terms.OwedToUs ? "1" : "-1") ||
            side.DeliveryFrom != source.Quantities.From || side.DeliveryTo != source.Quantities.To ||
            side.Periods.IsDefaultOrEmpty || side.Periods.Any(period =>
                period.ObservationImplementation != FloatingSide.DeliveryIndexImplementation ||
                period.Projections[0].Contract.DeliveryFrom != period.DeliveryFrom || period.Projections[0].Contract.DeliveryTo != period.DeliveryTo ||
                period.PaymentAt != PaymentDatePolicy.Start(new DateOnly(period.DeliveryFrom.Year, period.DeliveryFrom.Month, 1)
                    .AddMonths(1).AddDays(period.PaymentAt.Day - 1)) || period.PaymentAt.Day is < 1 or > 28) ||
            side.Periods.Select(period => period.PaymentAt.Day).Distinct().Count() != 1 ||
            side.Periods.Select(period => period.Fixings[0].Index).Distinct().Count() != 1)
            throw Invalid("An imbalance entry requires a whole-term daily index payment, share one, its fixed direction and next-month payment day 1 to 28.");
        terms.Require("gas_composition_input");
    }

    internal static void RequirePairs(ImmutableArray<GasCashComponent> entries)
    {
        foreach (var group in entries.Where(entry => entry.Balance is not null).GroupBy(entry => entry.Source))
        {
            var pair = group.ToArray();
            if (pair.Length != 2 || pair.Count(entry => entry.Balance!.OwedToUs) != 1 ||
                pair[0].Balance! with { OwedToUs = false } != pair[1].Balance! with { OwedToUs = false } ||
                !((FloatingSide)pair[0].Side).Periods.Select(period => (period.Fixings[0].Index, period.PaymentAt))
                    .SequenceEqual(((FloatingSide)pair[1].Side).Periods.Select(period => (period.Fixings[0].Index, period.PaymentAt))))
                throw Invalid("Each physical source requires exactly one received and one paid cash-out entry with matching profile, index, payment day, basis, netting and policy.");
        }
    }

    private static MarketDataException Invalid(string message) => new("gas_composition_input", message);
}
