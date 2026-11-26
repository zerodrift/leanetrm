using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static partial class GasSwingChecks
{
    private static readonly DateOnly From = new(2041, 1, 30);
    private static readonly DateTimeOffset At = new(2041, 1, 29, 12, 0, 0, TimeSpan.Zero);
    private static LatticeDecimal D(string value) => LatticeDecimal.Parse(value);
    private static readonly SwingPricingBinding Pricing = new(0, "gas_swing_one_factor_lognormal_v1",
        OptionNumericalPolicies.ActualOver365FixedInstant, SwingUnitResources.Policy, "curve_marks_log_return_held_v1");

    internal static void Run()
    {
        ExactWorkedPlans();
        ExactPeriodBudgets();
        SmallPlanOracle();
        CheckSwingNodeArithmetic();
        CancelWorkStages();
        UnitWorkLimits();
        UnitScalingAndClock();
        QuantityComponentAdmission();
        BoundedPlanningStep();
        SparsePlanLimits();
        Console.WriteLine("PASS expanded gas swing: worked forward plans, continuous optima, 1,024 independent small cases, monthly budgets, exact reservations, compact wide-grid moves and source coverage");
        RetainedQuantityLifecycle();
    }

    private static RetainedGasMember Retain(Side side, string direction, ReportedBalanceFollow? balance = null) =>
        new(side.Id, ExactDecimal.Parse(direction), ExactRatio.One, BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(side,
            lifecycle: ScheduleLifecycleSelection.StartedPhysicalDelivery)), 0, balance: balance);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Refuse(string code, Action action)
    {
        try { action(); }
        catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception($"Expected {code}.");
    }
}
