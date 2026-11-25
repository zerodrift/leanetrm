using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

// What one unit delivered into the contract costs and one unit delivered out of it earns on a gas day.
// The caller states both, with whatever forward, charge and discounting it selected; none is resolved here.
public sealed record GasStorageDayPrice(DeliveryInterval GasDay, ExactDecimal InjectionCost, ExactDecimal WithdrawalValue);
public sealed record GasStorageProposal(string Implementation, Quantity InventoryStep, GasStoragePlanResult Plan, SignedRatio Objective)
{
    public string ObjectiveText => ExactNumberText.Format(Objective);
}

// The best plan among those that move inventory in whole steps from the opening inventory, found by
// exact backward induction over that lattice. Equal objectives prefer the smaller absolute move,
// then the lower signed move, on the earliest differing day.
public static class GasStoragePlanProposals
{
    public const string Implementation = "gas_storage_inventory_lattice_search_v1";
    public const int MaximumLevels = 4001;
    public const long MaximumWork = 20_000_000;
    // Each admitted move's scaled point quantity is bounded by its ratchet below 10^42,
    // as is each scaled price. Fewer than 2^290 covers the sum over at most 1,830 days.
    private const int MaximumScoreBits = 300;

    public static GasStorageProposal Propose(GasStorageEntitlement entitlement, GasStorageState opening,
        GasStorageTerminal terminal, ImmutableArray<GasStorageDayPrice> prices, Quantity inventoryStep, string implementation)
    {
        if (implementation != Implementation)
            throw GasStoragePlans.Error("gas_storage_implementation", "Select the supported plan search explicitly.");
        GasStoragePlans.ValidateEntitlement(entitlement);
        if (prices.IsDefault || prices.Any(p => p is null))
            throw GasStoragePlans.Error("gas_storage_grid", "Supply one price row for each gas day.");
        var unit = entitlement.Quantity;
        var zero = new Quantity(default, unit);
        GasStoragePlans.ValidateGrid([.. prices.Select(p => new GasStorageDay(p.GasDay, zero, zero))]);
        if (opening is null || opening.Inventory is null || opening.Inventory.Specification != unit || opening.At != prices[0].GasDay.Start)
            throw GasStoragePlans.Error("gas_storage_opening", "Supply the opening inventory at the first gas day's start in the contract's quantity.");
        GasStoragePlans.RequireCapacity(entitlement, opening.Inventory);
        GasStoragePlans.ValidateTerminal(entitlement, terminal);
        if (inventoryStep is null || inventoryStep.Specification != unit || inventoryStep.Amount.CompareTo(default) <= 0)
            throw GasStoragePlans.Error("gas_storage_step", "Supply a positive inventory step in the contract's quantity.");

        // One step of inventory as the quantity delivered in, and as the quantity delivered out.
        var scale = ExactDecimal.WorkingScale;
        var step = inventoryStep.Amount.ScaledValue;
        var injected = BigInteger.DivRem(step * scale, scale - entitlement.InjectionFuel.ScaledValue, out var injectionRemainder);
        var withdrawn = BigInteger.DivRem(step * (scale - entitlement.WithdrawalFuel.ScaledValue), scale, out var withdrawalRemainder);
        if (injectionRemainder != 0 || withdrawalRemainder != 0)
            throw GasStoragePlans.Error("gas_storage_step", "Choose an inventory step whose delivered quantities are exact decimals under the contract's fuel fractions.");

        // Lattice levels are the opening inventory plus whole steps, inside the contracted range.
        var start = new SignedRatio(opening.Inventory.NumeratorValue, opening.Inventory.DenominatorValue);
        SignedRatio Decimal(ExactDecimal value) => new(value.ScaledValue, scale);
        var width = Decimal(inventoryStep.Amount);
        static BigInteger Floor(SignedRatio value) => BigInteger.Divide(value.Numerator - (value.Numerator.Sign < 0 ? value.Denominator - 1 : 0), value.Denominator);
        var above = Floor((Decimal(entitlement.MaximumInventory.Amount) - start) * new SignedRatio(width.Denominator, width.Numerator));
        var below = Floor((start - Decimal(entitlement.MinimumInventory.Amount)) * new SignedRatio(width.Denominator, width.Numerator));
        if (above + below + 1 > MaximumLevels)
            throw GasStoragePlans.Error("gas_storage_work", "The inventory step gives more lattice levels than this search admits; choose a larger step.");
        var levels = (int)(above + below) + 1;
        var origin = (int)below;
        var inventory = new ExactQuantity[levels];
        var up = new int[levels];
        var down = new int[levels];
        long work = 0;
        for (var n = 0; n < levels; n++)
        {
            var level = start + width * new SignedRatio(n - origin, 1);
            inventory[n] = new(level.Numerator, level.Denominator, unit);
            var ratchet = entitlement.Ratchets[GasStoragePlans.Band(entitlement, inventory[n])];
            up[n] = (int)BigInteger.Min(levels - 1 - n, BigInteger.Divide(ratchet.MaximumDailyInjection.Amount.ScaledValue, injected));
            down[n] = (int)BigInteger.Min(n, BigInteger.Divide(ratchet.MaximumDailyWithdrawal.Amount.ScaledValue, withdrawn));
            work += up[n] + down[n] + 1;
        }
        work *= prices.Length;
        if (work > MaximumWork)
            throw WorkError();

        var low = ExactQuantity.FromQuantity(terminal.MinimumInventory);
        var high = ExactQuantity.FromQuantity(terminal.MaximumInventory);
        var bounds = Enumerable.Repeat(new LevelPlanBounds(0, levels - 1, 0), prices.Length + 1).ToImmutableArray();
        var rewards = prices.Select(price => (Injection: -price.InjectionCost.ScaledValue * injected,
            Withdrawal: price.WithdrawalValue.ScaledValue * withdrawn)).ToArray();
        IEnumerable<LevelPlanCandidate> Candidates(int day, LevelPlanState state)
        {
            var level = state.Level;
            var reward = rewards[day];
            for (var move = -down[level]; move <= up[level]; move++)
            {
                var score = move switch
                {
                    < 0 => reward.Withdrawal * -move,
                    > 0 => reward.Injection * move,
                    _ => BigInteger.Zero
                };
                yield return new(move, new(level + move, 0), score);
            }
        }

        var proposal = LevelPlanSearch.Search(bounds, new(origin, 0), Candidates, _ => 0,
            state => GasStoragePlans.Compare(inventory[state.Level], low) >= 0 && GasStoragePlans.Compare(inventory[state.Level], high) <= 0,
            work, new(MaximumLevels, MaximumWork, (long)bounds.Length * levels, MaximumScoreBits));
        if (proposal.Status == LevelPlanStatus.WorkLimit)
            throw WorkError();
        if (proposal.Score is not { } objective)
            throw GasStoragePlans.Error("gas_storage_infeasible", "No plan on this lattice ends inside the stated terminal inventory range within the contract's limits.");

        var days = ImmutableArray.CreateBuilder<GasStorageDay>(prices.Length);
        for (var day = 0; day < prices.Length; day++)
        {
            var move = proposal.Moves[day];
            days.Add(new(prices[day].GasDay, new(ExactDecimal.FromScaled(move > 0 ? injected * move : BigInteger.Zero), unit),
                new(ExactDecimal.FromScaled(move < 0 ? withdrawn * -move : BigInteger.Zero), unit)));
        }
        // The proposed plan is admitted by the same operation that admits a supplied one.
        var plan = GasStoragePlans.Evaluate(entitlement, opening, terminal, days.MoveToImmutable(), GasStoragePlans.Implementation);
        return new(Implementation, inventoryStep, plan, new SignedRatio(objective, scale * scale));
    }

    private static MarketDataException WorkError() =>
        GasStoragePlans.Error("gas_storage_work", "The days, lattice levels and daily limits exceed this search's bound; choose a larger step or a shorter plan.");
}
