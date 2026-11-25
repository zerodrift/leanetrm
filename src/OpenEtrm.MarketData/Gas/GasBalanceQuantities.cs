using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record SlotBalances(int Slot, ImmutableDictionary<DateOnly, ImmutableArray<Guid>> AccountsOn,
    ImmutableArray<AccountDayBalance> Balances, ImmutableHashSet<DateOnly> MonthsWithTransfers)
{
    public ImmutableArray<AccountStatementEvidence> StatementHeads { get; init; } = [];
}
public sealed record AccountStatementEvidence(Guid AccountId,DateOnly Day,Guid ServiceVersionId,Guid? StatementId,
    string Status,ExactQuantity? ReportedBalance,string Evidence);

// Gas resolves statements; the common Side lifecycle receives only a state and
// absolute quantity. One resolution serves both directions of the cash-out pair.
internal static class GasBalanceQuantities
{
    internal static ImmutableArray<ImmutableArray<ReportedDayQuantity>> Build(ImmutableArray<RetainedGasMember> members,
        PhysicalCashState state, ImmutableArray<PhysicalLifecycleGeometry> geometries)
    {
        var sources = members.Where(member => member.Balance is not null).Select(member => member.SourceSlot!.Value).ToHashSet();
        if (state.Balances.Any(slot => slot is null || !sources.Contains(slot.Slot)) ||
            state.Balances.Select(slot => slot.Slot).Distinct().Count() != state.Balances.Length)
            throw Invalid("Balance facts must name distinct physical sources of the retained cash-out entries.");
        if (sources.Count == 0) return default;
        var selected = new Dictionary<int, ImmutableArray<ImbalanceDay>>();
        foreach (var source in sources)
        {
            var terms = members.First(member => member.Balance is not null && member.SourceSlot == source).Balance!;
            var geometry = geometries[source];
            ImmutableArray<DateOnly> days = [..geometry.Days.Keys.Order()];
            var facts = state.Balances.SingleOrDefault(slot => slot.Slot == source);
            if (facts is not null && (facts.AccountsOn is null || !facts.AccountsOn.Keys.ToHashSet().SetEquals(days) ||
                facts.Balances.IsDefault || facts.MonthsWithTransfers is null || facts.Balances.Any(balance =>
                    balance is null || balance.Current is null || balance.Current.Specification != state.Quantity.Specification)))
                throw Invalid("Balance facts require complete dated account scope and the source's native quantity unit.");
            if (facts is not null) GasImbalanceCashout.RequireBalanceFacts(facts.AccountsOn, facts.Balances);
            var eligible = facts is null ? [] : facts.Balances.Where(balance =>
                terms.StatementPolicy != GasImbalanceTrading.FinalPolicy || balance.Status == "final").ToImmutableArray();
            selected.Add(source, GasImbalanceCashout.Days(days, day => geometry.Days[day] == DeliveryState.Ended,
                day => facts?.AccountsOn[day] ?? [], eligible, terms.Basis, terms.Netting, facts?.MonthsWithTransfers ?? []));
        }
        return [..members.Select(member => member.Balance is not {} terms ? default(ImmutableArray<ReportedDayQuantity>) :
            selected[member.SourceSlot!.Value].Select(day => new ReportedDayQuantity(member.SourceSlot.Value, day.Day, day.State,
                (terms.OwedToUs ? day.OwedToUs : day.OwedByUs) is {} quantity ? ExactQuantity.FromRatio(quantity, state.Quantity.Specification) : null,
                day.Evidence){Reason=day.Reason}).ToImmutableArray())];
    }
    private static MarketDataException Invalid(string message) => new("gas_imbalance_input", message);
}
