using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

// Authoring expands one stated pair into two existing daily index payments.
// The original command is retained unchanged for replay and reopening.
internal static class GasCashoutTerms
{
    internal static ImmutableArray<(GasCashRequest Cash, ReportedBalanceFollow? Balance)> Entries(GasCompositionRequest body)
    {
        var pairs = body.ImbalanceCashouts ?? [];
        if (pairs.IsDefault || pairs.Any(pair => pair is null) ||
            !pairs.IsEmpty && body.PhysicalSides.Any(side => side.Link is not null) ||
            pairs.Select(pair => pair.Side).Distinct(StringComparer.Ordinal).Count() != pairs.Length)
            throw Invalid();
        var result = ImmutableArray.CreateBuilder<(GasCashRequest, ReportedBalanceFollow?)>();
        result.AddRange(body.CashSides.Select(cash => (cash, (ReportedBalanceFollow?)null)));
        foreach (var pair in pairs)
        {
            if (string.IsNullOrWhiteSpace(pair.Code) || string.IsNullOrWhiteSpace(pair.Index) ||
                string.IsNullOrWhiteSpace(pair.SpreadOwedToUs) || string.IsNullOrWhiteSpace(pair.SpreadOwedByUs) ||
                pair.PaymentDay is < 1 or > 28 || body.PhysicalSides.Count(side => side.Code == pair.Side) != 1)
                throw Invalid();
            foreach (var owed in new[] { true, false })
            {
                var balance = Terms(pair.Profile, owed, pair.StatementBasis, pair.AccountNetting, pair.StatementPolicy);
                var cash = new GasCashRequest(pair.Code + (owed ? ".to_us" : ".by_us"), "indexed_consideration",
                    owed ? "receive" : "pay", pair.Side, "delivery", null, null, null,
                    new(pair.Index, GasIndexedCash.Observation, GasIndexedCash.Payment, pair.PaymentDay,
                        owed ? pair.SpreadOwedToUs : pair.SpreadOwedByUs, null)) { Share = "1", CalendarPolicy = pair.CalendarPolicy };
                result.Add((cash, balance));
            }
        }
        return result.ToImmutable();
    }
    internal static ReportedBalanceFollow Terms(string profile, bool owed, string basis, string netting, string policy)
    {
        var result = new ReportedBalanceFollow(profile, owed,
            basis switch { "daily_imbalance_v1" => StatementBasis.DailyImbalance, "month_to_date_balance_v1" => StatementBasis.MonthToDateBalance, _ => throw Invalid() },
            netting switch { "net_accounts_v1" => AccountNetting.NetAccounts, "each_account_v1" => AccountNetting.EachAccount, _ => throw Invalid() }, policy);
        result.Require("gas_composition_input");
        return result;
    }
    private static MarketDataException Invalid() => new("gas_composition_input",
        "State one complete daily index cash-out pair per independent physical side, with its basis, netting, statement policy, spreads and payment day.");
}
