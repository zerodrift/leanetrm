using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public enum StatementBasis { DailyImbalance = 1, MonthToDateBalance }
public enum AccountNetting { NetAccounts = 1, EachAccount }

// These are eligible, temporally resolved statements. Revision selection and the
// statement policy are applied before constructing this input, never by falling
// back to an older eligible revision here.
public sealed record AccountDayBalance(Guid AccountId, DateOnly Day, ExactQuantity Current,
    Guid StatementId, string Status, string Evidence);

public sealed record ImbalanceDay(DateOnly Day, string State, SignedRatio? OwedToUs,
    SignedRatio? OwedByUs, ImmutableArray<DeliveryQuantityEvidence> Evidence);

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
        if (accounts.Values.Any(ids => ids.IsDefault || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length) ||
            balances.Any(balance => balance is null || !accounts.TryGetValue(balance.Day, out var ids) || !ids.Contains(balance.AccountId) ||
                balance.Current is null || balance.StatementId == Guid.Empty || balance.Status is not ("final" or "provisional") ||
                string.IsNullOrWhiteSpace(balance.Evidence)) ||
            balances.Select(balance => balance.Current.Specification).Distinct().Count() > 1 ||
            balances.Select(balance => (balance.AccountId, balance.Day)).Distinct().Count() != balances.Length)
            throw Invalid("Supply one eligible statement per account and gas day in one quantity unit, with its revision and evidence.");

        var facts = balances.ToDictionary(balance => (balance.AccountId, balance.Day));
        var result = ImmutableArray.CreateBuilder<ImbalanceDay>(days.Length);
        foreach (var day in days)
        {
            if (!ended(day)) { result.Add(new(day, NotApplicable, null, null, [])); continue; }
            var month = new DateOnly(day.Year, day.Month, 1);
            if (basis == StatementBasis.MonthToDateBalance && monthsWithTransfers.Contains(month))
                throw new MarketDataException("gas_imbalance_transfer", "An accepted transfer makes the month-to-date cash-out ambiguous for that month.");

            var evidence = ImmutableArray.CreateBuilder<DeliveryQuantityEvidence>();
            var figures = new List<SignedRatio>();
            var complete = !accounts[day].IsEmpty &&
                !(basis == StatementBasis.MonthToDateBalance && days[0].Day != 1 && month <= days[0]);
            foreach (var account in accounts[day])
            {
                if (!facts.TryGetValue((account, day), out var current)) { complete = false; continue; }
                evidence.Add(Evidence(current));
                var figure = Quantity(current);
                if (basis == StatementBasis.MonthToDateBalance && day.Day != 1)
                {
                    if (!facts.TryGetValue((account, day.AddDays(-1)), out var previous)) { complete = false; continue; }
                    evidence.Add(Evidence(previous));
                    figure -= Quantity(previous);
                }
                figures.Add(figure);
            }
            if (!complete) { result.Add(new(day, Unassessed, null, null, evidence.ToImmutable())); continue; }

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
    private static MarketDataException Invalid(string message) => new("gas_imbalance_input", message);
}
