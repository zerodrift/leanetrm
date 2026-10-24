using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class SwapLifecycleChecks
{
    public static int Run()
    {
        var f = new IndexFixingChecks.FixingFixture(monthly: true); var swap = f.Swap();
        var snapshot = f.Snapshot(f.Payment, 1); var trade = Guid.NewGuid(); var binding = Guid.NewGuid();
        var quantity = new Quantity(ExactDecimal.Parse("200"), swap.UnitQuantity.Specification);
        var zero = ExactMoney.Parse("0", swap.Currency);
        var completions=Enumerable.Range(0,2).Select(n=>new SwapPeriodCompletion(n,f.From,f.To,
            new DateTimeOffset(f.To.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)),"date_only_utc_v1")).ToImmutableArray();
        var receive = new SettlementContribution(Guid.NewGuid(), 1, f.From, f.To, 1, f.Payment,
            ExactMoney.Parse("2000", swap.Currency), ExactMoney.Parse("1500", swap.Currency));
        var adjustment = new SettlementContribution(Guid.NewGuid(), 1, f.From, f.To, 2, f.Payment.AddDays(1),
            ExactMoney.Parse("200", swap.Currency), zero);
        var fixedCash = new SettlementContribution(Guid.NewGuid(), 0, f.From, f.To, 1, f.Payment,
            ExactMoney.Parse("-3000", swap.Currency), zero);
        var inputs = new SwapSettlementInputs(trade, binding, quantity, [receive,adjustment,fixedCash], ExactMoney.Parse("1500", swap.Currency), zero,completions,
            "swap_due_nominal_v1", "swap_completed_period_trading_pnl_v1");
        var value = inputs.Evaluate(swap, new(f.Model.Build(snapshot, []), new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted)));
        Equal(value.RemainingValue.ToExactString(), "-2300"); Equal(value.DueBalance.ToExactString(), "-2500");
        Equal(value.Realized.ToExactString(), "-800"); Equal(value.Unrealized.ToExactString(), "0");
        Equal(value.DeterminedOutstanding.ToExactString(), "-2300");
        Equal(value.NetCash.ToExactString(), "1500"); Equal(value.Corrections.ToExactString(), "200");
        // Later eligible market prices cannot reprice determined balances.
        var revised = snapshot with { Id = Guid.NewGuid(), Selections = snapshot.Selections.Select(s => s.Input.Use == InputUse.FrozenFact
            ? s with { Observation = s.Observation with { Key = new(s.Observation.Key.CatalogId, Guid.NewGuid()), Value = ExactDecimal.Parse("99") } } : s).ToImmutableArray() };
        Equal(inputs.Evaluate(swap, new(f.Model.Build(revised, []), new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted))).RemainingValue, value.RemainingValue);
        var beforeDelivery = snapshot with { Id = Guid.NewGuid(), ValuationAt = f.Fixings[0].FixingAt, MarketCutoff = f.Fixings[0].FixingAt };
        Equal(inputs.Evaluate(swap, new(f.Model.Build(beforeDelivery, []), new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted))).Realized.ToExactString(), "0");
        var otherTrade = Guid.NewGuid(); var otherBinding = Guid.NewGuid(); var book = Guid.NewGuid();
        var other = new SwapSettlementInputs(otherTrade, otherBinding, quantity,
            inputs.Contributions.Select(c => c with { Id = Guid.NewGuid(), Applied = zero }).ToImmutableArray(),zero,zero,completions,
            "swap_due_nominal_v1", "swap_completed_period_trading_pnl_v1");
        var provider = new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        RiskPosition Position(Guid t, Guid b) => new(t,b,book,swap.Id,quantity,new(new Money(default,swap.Currency),quantity.Specification),new(default,swap.Currency));
        var portfolio = new ValuationPortfolio([swap],[Position(trade,binding),Position(otherTrade,otherBinding)],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
            { Settlements = ImmutableDictionary<MeasureBinding,SwapBindingValuation>.Empty
                .Add(new(trade,binding),new(inputs,provider)).Add(new(otherTrade,otherBinding),new(other,provider)) });
        // A close receipt identifies models by instrument. Mixed enrollment must
        // declare the same dispatch model for both scopes while retaining distinct values.
        var mixed = new ValuationPortfolio([swap],[Position(trade,binding),Position(Guid.NewGuid(),Guid.NewGuid())],portfolio.Setup with { Settlements=portfolio.Setup.Settlements.Remove(new(otherTrade,otherBinding)), InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(swap.Id,provider) });
        var mixedContracts=mixed.Declare(snapshot);
        Equal(mixedContracts.Select(c=>c.ModelVersion).Distinct().Count(),1);
        var preDue=snapshot with{Id=Guid.NewGuid(),ValuationAt=f.Payment.AddDays(-1),MarketCutoff=f.Payment.AddDays(-1)};
        var mixedRisk=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model,preDue,[mixed]),[],RiskEngine.ExactNumericalPolicy);
        var pure=swap.PV(new(f.Model.Build(preDue,swap.DependenciesAt(preDue)),provider)).PresentValue.Scale(quantity.Amount);
        var enrolled=inputs.Evaluate(swap,new(f.Model.Build(preDue,[]),provider)).RemainingValue;
        Equal(mixedRisk.Groups.Single().BaseValues.Single().Value,pure.Add(enrolled));
        var prepared = RiskEngine.Prepare(f.Model,snapshot,[portfolio]);
        Equal(prepared.Factors.Length,0);
        var risk = RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy);
        Equal(risk.Groups.Single().BaseValues.Single().Value.ToExactString(),"-3100");
        var fullyPaid = new SwapSettlementInputs(trade,binding,quantity,
            inputs.Contributions.Select(c=>c with{Applied=c.Amount}).ToImmutableArray(),ExactMoney.Parse("-800",swap.Currency),zero,completions,
            "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
        var closing = fullyPaid.Evaluate(swap,new(f.Model.Build(snapshot,[]),provider));
        var bridge = SwapEconomicBridge.Calculate(value,closing,closing.NetCash.Subtract(value.NetCash));
        Equal(bridge.EconomicPnl.ToExactString(),"0");
        Equal(bridge.NetCashMovement.ToExactString(),"-2300");
        Equal(bridge.RemainingValueMovement.ToExactString(),"2300");
        var clocked=new SwapSettlementInputs(trade,binding,quantity,inputs.Contributions,inputs.NetCash,zero,
            completions.Select(c=>c with{At=c.At.AddHours(5),Source="retained_quantity_interval_end_v1"}).ToImmutableArray(),
            "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
        var clockBoundary=snapshot with{Id=Guid.NewGuid(),ValuationAt=completions[0].At.AddHours(1),MarketCutoff=completions[0].At.AddHours(1)};
        Equal(clocked.Evaluate(swap,new(f.Model.Build(clockBoundary,[]),provider)).Realized.ToExactString(),"0");
        clockBoundary=clockBoundary with{Id=Guid.NewGuid(),ValuationAt=completions[0].At.AddHours(5),MarketCutoff=completions[0].At.AddHours(5)};
        Equal(clocked.Evaluate(swap,new(f.Model.Build(clockBoundary,[]),provider)).Realized.ToExactString(),"-800");
        var mixedClock=new SwapSettlementInputs(trade,binding,quantity,inputs.Contributions,inputs.NetCash,zero,
            completions.Select(c=>c.MembershipOrdinal==1?c with{At=c.At.AddHours(5),Source="retained_quantity_interval_end_v1"}:c).ToImmutableArray(),
            "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
        var mixedBoundary=clockBoundary with{Id=Guid.NewGuid(),ValuationAt=completions[0].At.AddHours(1),MarketCutoff=completions[0].At.AddHours(1)};
        var mixedValue=mixedClock.Evaluate(swap,new(f.Model.Build(mixedBoundary,[]),provider));
        Equal(mixedValue.Realized.ToExactString(),"0");
        if(mixedValue.Payments.Any(p=>p.Realized))throw new Exception("A matching period realized before both completion boundaries.");
        var delayed=new IndexFixingChecks.FixingFixture(monthly:true,delayedMonthlyPublication:true);var delayedSwap=delayed.Swap();
        var delayedAt=delayed.Snapshot(new DateTimeOffset(2041,2,1,8,0,0,TimeSpan.Zero));
        var delayedZero=ExactMoney.Parse("0",delayedSwap.Currency);
        var pending=new SwapSettlementInputs(Guid.NewGuid(),Guid.NewGuid(),new(ExactDecimal.Parse("1"),delayedSwap.UnitQuantity.Specification),[],delayedZero,delayedZero,
            Enumerable.Range(0,2).Select(n=>new SwapPeriodCompletion(n,delayed.From,delayed.To,new(2041,2,1,0,0,0,TimeSpan.Zero),"date_only_utc_v1")).ToImmutableArray(),
            "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
        var pendingValue=pending.Evaluate(delayedSwap,new(delayed.Model.Build(delayedAt,delayedSwap.DependenciesAt(delayedAt)),provider));
        Equal(pendingValue.Realized.ToExactString(),"0");
        if(pendingValue.Payments.Any(p=>p.Realized))throw new Exception("A period realized its fixed side before the floating side was final.");
        Console.WriteLine("PASS settlement-aware value: exact coverage, partial payment, nominal due cash, explicit correction due and per-period realization");
        return 10;
    }
    private static void Equal<T>(T actual, T expected)
    { if (!Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}"); }
}
