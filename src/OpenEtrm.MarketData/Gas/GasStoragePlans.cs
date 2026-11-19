using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Gas;

// A commercial storage contract's own entitlement: contracted inventory range and daily ratchets.
// It is not the physical facility, whose rules, activity and capacity stay with StorageFacilityOperations.
public sealed record GasStorageRatchet(Quantity Lower, Quantity Upper, Quantity MaximumDailyInjection, Quantity MaximumDailyWithdrawal);
public sealed record GasStorageEntitlement(DefinitionRevision Definitions, string Code, ReferenceKey Quantity,
    Quantity MinimumInventory, Quantity MaximumInventory, ImmutableArray<GasStorageRatchet> Ratchets,
    ExactDecimal InjectionFuel, ExactDecimal WithdrawalFuel, string RatchetBasis, string FlowPolicy, string FuelBasis)
{
    public const string OpeningInventoryBand = "gas_day_opening_inventory_band_v1";
    public const string SingleDirection = "single_direction_per_gas_day_v1";
    public const string InKindAtPoint = "in_kind_fuel_at_storage_point_v1";
}

// Injection and Withdrawal are measured at the storage point: gas delivered to the contract and gas
// delivered from it. Fuel in kind is the difference between those and the change in inventory.
public sealed record GasStorageDay(DeliveryInterval GasDay, Quantity Injection, Quantity Withdrawal);
public sealed record GasStorageState(DateTimeOffset At, ExactQuantity Inventory);
public sealed record GasStorageTerminal(Quantity MinimumInventory, Quantity MaximumInventory);
public sealed record GasStorageDayResult(GasStorageDay Day, int Band, ExactQuantity Opening, ExactQuantity Closing,
    ExactQuantity InjectionFuel, ExactQuantity WithdrawalFuel);
public sealed record GasStoragePlanResult(string Implementation, GasStorageEntitlement Entitlement, GasStorageState Opening,
    GasStorageTerminal Terminal, ImmutableArray<GasStorageDayResult> Days, ExactQuantity TotalInjection,
    ExactQuantity TotalWithdrawal, GasStorageState Closing);

// Feasibility and inventory conservation for a supplied immutable plan. This operation performs
// no pricing, optimization, data selection or mutation, and assigns no title or cash.
public static class GasStoragePlans
{
    public const string Implementation = "gas_storage_supplied_plan_v1";
    public const int MaximumDays = 1830, MaximumRatchets = 32;
    private static readonly ExactDecimal One = ExactDecimal.Parse("1");

    public static GasStoragePlanResult Evaluate(GasStorageEntitlement entitlement, GasStorageState opening,
        GasStorageTerminal terminal, ImmutableArray<GasStorageDay> days, string implementation)
    {
        if (implementation != Implementation)
            throw Error("gas_storage_implementation", "Select the supported supplied-plan implementation explicitly.");
        ValidateEntitlement(entitlement);
        ValidateGrid(days);
        var unit = entitlement.Quantity;
        if (opening is null || opening.Inventory is null || opening.Inventory.Specification != unit || opening.At != days[0].GasDay.Start)
            throw Error("gas_storage_opening", "Supply the opening inventory at the first gas day's start in the contract's quantity.");
        if (terminal is null || !Same(terminal.MinimumInventory, unit) || !Same(terminal.MaximumInventory, unit) ||
            terminal.MinimumInventory.Amount.CompareTo(entitlement.MinimumInventory.Amount) < 0 ||
            terminal.MaximumInventory.Amount.CompareTo(entitlement.MaximumInventory.Amount) > 0 ||
            terminal.MinimumInventory.Amount.CompareTo(terminal.MaximumInventory.Amount) > 0)
            throw Error("gas_storage_terminal", "Supply an ordered terminal inventory range inside the contracted capacity; an exact target uses equal bounds.");

        var inventory = opening.Inventory;
        RequireCapacity(entitlement, inventory);
        var injected = Zero(unit);
        var withdrawn = Zero(unit);
        var rows = ImmutableArray.CreateBuilder<GasStorageDayResult>(days.Length);
        foreach (var day in days)
        {
            if (!Same(day.Injection, unit) || !Same(day.Withdrawal, unit) ||
                day.Injection.Amount.CompareTo(default) < 0 || day.Withdrawal.Amount.CompareTo(default) < 0)
                throw Error("gas_storage_flow", "Injection and withdrawal are independently nonnegative amounts in the contract's quantity.");
            if (day.Injection.Amount.CompareTo(default) > 0 && day.Withdrawal.Amount.CompareTo(default) > 0)
                throw Error("gas_storage_direction", "The selected policy allows one flow direction on a gas day.");
            var band = Band(entitlement, inventory);
            var ratchet = entitlement.Ratchets[band];
            if (day.Injection.Amount.CompareTo(ratchet.MaximumDailyInjection.Amount) > 0 ||
                day.Withdrawal.Amount.CompareTo(ratchet.MaximumDailyWithdrawal.Amount) > 0)
                throw Error("gas_storage_ratchet", "A gas day's flow exceeds the daily limit of the band its opening inventory is in.");
            var injection = ExactQuantity.FromQuantity(day.Injection);
            var withdrawal = ExactQuantity.FromQuantity(day.Withdrawal);
            var stored = injection.Scale(One - entitlement.InjectionFuel);
            // Delivered withdrawal is the retained share of what leaves inventory.
            var taken = withdrawal.ScaleFraction(ExactDecimal.WorkingScale, (One - entitlement.WithdrawalFuel).ScaledValue);
            var next = inventory.Add(stored).Add(taken.Scale(-One));
            RequireCapacity(entitlement, next);
            rows.Add(new(day, band, inventory, next, injection.Add(stored.Scale(-One)), taken.Add(withdrawal.Scale(-One))));
            injected = injected.Add(injection);
            withdrawn = withdrawn.Add(withdrawal);
            inventory = next;
        }
        if (Compare(inventory, ExactQuantity.FromQuantity(terminal.MinimumInventory)) < 0 ||
            Compare(inventory, ExactQuantity.FromQuantity(terminal.MaximumInventory)) > 0)
            throw Error("gas_storage_terminal", "The supplied plan does not end inside its stated terminal inventory range.");
        return new(Implementation, entitlement, opening, terminal, rows.MoveToImmutable(), injected, withdrawn,
            new(days[^1].GasDay.End, inventory));
    }

    public static void ValidateEntitlement(GasStorageEntitlement? entitlement)
    {
        if (entitlement is null || entitlement.Definitions.CatalogId == Guid.Empty || entitlement.Definitions.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(entitlement.Code) || entitlement.RatchetBasis != GasStorageEntitlement.OpeningInventoryBand ||
            entitlement.FlowPolicy != GasStorageEntitlement.SingleDirection || entitlement.FuelBasis != GasStorageEntitlement.InKindAtPoint ||
            entitlement.Ratchets.IsDefaultOrEmpty || entitlement.Ratchets.Length > MaximumRatchets ||
            entitlement.InjectionFuel.CompareTo(default) < 0 || entitlement.InjectionFuel.CompareTo(One) >= 0 ||
            entitlement.WithdrawalFuel.CompareTo(default) < 0 || entitlement.WithdrawalFuel.CompareTo(One) >= 0)
            throw Invalid();
        Money.RequireKey(entitlement.Quantity);
        var unit = entitlement.Quantity;
        if (!Same(entitlement.MinimumInventory, unit) || !Same(entitlement.MaximumInventory, unit) ||
            entitlement.MinimumInventory.Amount.CompareTo(default) < 0 ||
            entitlement.MaximumInventory.Amount.CompareTo(entitlement.MinimumInventory.Amount) <= 0)
            throw Invalid();
        var preceding = entitlement.MinimumInventory.Amount;
        foreach (var band in entitlement.Ratchets)
        {
            if (band is null || !Same(band.Lower, unit) || !Same(band.Upper, unit) || !Same(band.MaximumDailyInjection, unit) ||
                !Same(band.MaximumDailyWithdrawal, unit) || band.Lower.Amount != preceding ||
                band.Upper.Amount.CompareTo(band.Lower.Amount) <= 0 || band.MaximumDailyInjection.Amount.CompareTo(default) < 0 ||
                band.MaximumDailyWithdrawal.Amount.CompareTo(default) < 0)
                throw Invalid();
            preceding = band.Upper.Amount;
        }
        if (preceding != entitlement.MaximumInventory.Amount) throw Invalid();
    }

    private static void ValidateGrid(ImmutableArray<GasStorageDay> days)
    {
        if (days.IsDefaultOrEmpty || days.Length > MaximumDays)
            throw Error("gas_storage_grid", "Supply a nonempty plan of gas days within the declared bound.");
        GasStorageDay? previous = null;
        foreach (var day in days)
        {
            // A gas day is 23, 24 or 25 hours on its own clock; the caller supplies its exact bounds and label.
            if (day?.GasDay is not { } gasDay || gasDay.Start.Offset != TimeSpan.Zero || gasDay.End.Offset != TimeSpan.Zero ||
                !Trading.TradingInstant.IsSupported(gasDay.Start) || !Trading.TradingInstant.IsSupported(gasDay.End) ||
                gasDay.End - gasDay.Start is not { TotalHours: 23 or 24 or 25 } ||
                previous is not null && (gasDay.Start != previous.GasDay.End || gasDay.OperatingDate != previous.GasDay.OperatingDate.AddDays(1)))
                throw Error("gas_storage_grid", "Gas days must be whole, ordered and contiguous with consecutive operating dates.");
            previous = day;
        }
    }

    // Bands are half-open from below; a full contract is in its last band.
    private static int Band(GasStorageEntitlement entitlement, ExactQuantity inventory)
    {
        for (var i = 0; i < entitlement.Ratchets.Length - 1; i++)
            if (Compare(inventory, ExactQuantity.FromQuantity(entitlement.Ratchets[i].Upper)) < 0) return i;
        return entitlement.Ratchets.Length - 1;
    }

    private static void RequireCapacity(GasStorageEntitlement entitlement, ExactQuantity inventory)
    {
        if (Compare(inventory, ExactQuantity.FromQuantity(entitlement.MinimumInventory)) < 0 ||
            Compare(inventory, ExactQuantity.FromQuantity(entitlement.MaximumInventory)) > 0)
            throw Error("gas_storage_inventory", "The plan takes inventory outside the contracted capacity.");
    }

    private static bool Same(Quantity? quantity, ReferenceKey unit) => quantity is not null && quantity.Specification == unit;
    private static int Compare(ExactQuantity left, ExactQuantity right) =>
        (left.NumeratorValue * right.DenominatorValue).CompareTo(right.NumeratorValue * left.DenominatorValue);
    private static ExactQuantity Zero(ReferenceKey specification) => new(0, 1, specification);
    private static MarketDataException Invalid() => new("gas_storage_entitlement",
        "Supply an exact contract, one quantity, the selected ratchet, flow and fuel policies, fuel fractions in [0,1) and daily ratchet bands that cover the contracted inventory range.");
    private static MarketDataException Error(string code, string message) => new(code, message);
}
