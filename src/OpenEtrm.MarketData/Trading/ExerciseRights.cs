using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public enum ExercisePeriod { DeliveryDay = 1, DeliveryMonth }
public enum ExerciseHolder { Us = 1, Counterparty }
public enum ExerciseSettlement { Physical = 1, CashAutomatic }

[EconomicDefinition("exercise_group_v1")]
public sealed record ExerciseGroup(
    [property: IdentityField] DateOnly From,
    [property: IdentityField] DateOnly To,
    [property: IdentityField] DateTimeOffset ExerciseAt);

[EconomicDefinition("exercise_right_v1")]
public sealed record ExerciseRight(
    [property: IdentityField] int Ordinal,
    [property: IdentityField] ImmutableArray<int> Slots,
    [property: IdentityField] ExercisePeriod Period,
    [property: IdentityField] ExerciseHolder Holder,
    [property: IdentityField] ExerciseSettlement Settlement,
    [property: IdentityField] string Model,
    [property: IdentityField] string DayCount,
    [property: IdentityField] ImmutableArray<ExerciseGroup> Groups);
