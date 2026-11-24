using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// Explicit selected pricing inputs, separate from accepted exercise/VM facts.
public sealed record FuturesOptionCashValueBinding(CashFuturesOptionPolicy Policy,FuturesOptionAutomaticRule Rule,
    PhysicalOptionTerminal Terminal,FuturesMarginPolicy Margin,[property:JsonIgnore]ISideDiscounting MarginDiscounting,FuturesMarketBinding Underlying);

public sealed partial record FuturesOptionInstrument
{
    private void RequireCashBinding(UnitValuationContext context,FuturesOptionMarketBinding reference,FuturesOptionCashValueBinding selected)
    {
        ReferenceFuturesOptionStrategy.RequireQualifiedStyle(Series.Terms);
        CashFuturesOptionModel.RequirePolicy(selected.Policy);
        if(selected.Margin is not{Implementation:FuturesMarginPolicy.Version or FuturesMarginPolicy.DeliveryVersion,Delivery:null}||selected.Margin.Contract!=reference.Underlying.Contract||
            selected.Margin.SettlementCurve!=reference.Underlying.Curve||selected.MarginDiscounting is null||selected.Underlying is null||
            selected.Underlying.Market!=reference.Underlying||selected.Underlying.Series.Key!=Series.UnderlyingSeries||
            selected.Underlying.SettlementMethod!="cash"||selected.Underlying.EligibleFrom>context.CalculationAt||
            selected.Rule is null||selected.Rule.Key.Revision!=Series.Key.Revision||selected.Rule.Key.Code!=Series.AutomaticExerciseRule||selected.Rule.Version<1||
            !Enum.IsDefined(selected.Terminal)||selected.Terminal!=PhysicalOptionTerminal.Automatic&&!Series.ContraryInstructionsAllowed)
            throw CashBindingError();
        _=selected.Rule.Decide(Series.Terms.Right,Series.Terms.Strike,Series.Terms.Strike);
    }
    private UnitPricingContract DeclareCash(UnitValuationContext context,FuturesOptionMarketBinding reference,FuturesOptionCashValueBinding selected)
    {
        RequireCashBinding(context,reference,selected);
        var margins=CashFuturesOptionModel.RequiredMarginOccurrences(Series.Terms,selected.Margin,context.CalculationAt,reference.Policy);
        return new(Id,CashFuturesOptionPolicy.Version,[reference.Underlying],UnitQuantity.Specification,Currency){
            Time=ValuationTimeDependency.Pinned,
            DiscountInputs=[new(new(Id,0,Series.Terms.ExpiryAt,Currency){Timing=ValueTimingKind.Instant},reference.Discounting),
                ..margins.Where(o=>o.DueAt>context.CalculationAt).Select(o=>new DiscountInput(MarginPayment(o),selected.MarginDiscounting))],
            Volatility=reference.Calibration is SolvedVolatility solved?[new(reference.Id,solved.Volatility.ToString(),VolatilityInput.Annualized)]:[],
            StaticDerivatives=new(StaticDerivativeRestriction.Version,"cash_option_reference_strategy_derivatives_not_qualified"),
            ScheduleIdentity=$"{CashFuturesOptionPolicy.Version}:"+ConnectedIdentity.Digest(new{selected.Policy,selected.Rule,selected.Terminal,selected.Margin,selected.Underlying})};
    }
    private UnitValuation CashValue(UnitValuationContext context,FuturesOptionMarketBinding reference,FuturesOptionCashValueBinding selected)
    {
        RequireCashBinding(context,reference,selected);
        var inputs=context.Inputs;if(inputs.SnapshotId!=reference.SnapshotId)throw CashBindingError();
        var model=context.Market!.Model;
        model.RequireFuturesOptionDefinition(Series,reference.Underlying,reference.Settlement);
        var future=model.FuturesContract(selected.Margin.Contract);var quote=model.QuoteFor(reference.Underlying);
        if(future.Series.SettlementMethod!="cash"||Series.Multiplier!=future.LotSize.Amount*future.Series.Multiplier)throw CashBindingError();
        _=future.ValidateMarginPolicy(selected.Underlying,selected.Margin,model,context.Snapshot);
        var a=inputs.ExactPrice(reference.Underlying);if(a.Unit!=PriceUnit.FromQuote(quote))throw CashBindingError();
        var expiry=Discount(context,reference);
        var margins=CashFuturesOptionModel.RequiredMarginOccurrences(Series.Terms,selected.Margin,context.CalculationAt,reference.Policy);
        var calibration=reference.Calibration is SolvedVolatility solved?solved with{Volatility=LatticeDecimal.Parse(inputs.Volatility(reference.Id).Format(30,MidpointRounding.ToEven))}:reference.Calibration;
        if(calibration is SolvedVolatility)reference.RecordTreePrice();
        var value=CashFuturesOptionModel.Price(Series.Terms,LatticeDecimal.From(a.ToUnitPriceExact().Value),calibration,LatticeDecimal.From(expiry.DiscountFactor),
            context.CalculationAt,reference.Policy,selected.Policy,selected.Rule,selected.Terminal,selected.Margin,
            MarginFactors(context,margins),context.Cancellation);
        ExactMoney Money(LatticeDecimal amount)=>ExactMoney.FromMoney(new(amount.ToExact(),Currency)).Scale(Series.Multiplier);
        var vanilla=Money(value.VanillaReference);var funding=Money(value.Total-value.VanillaReference);var total=Money(value.Total);
        context.RecordComponents([new("reference_vanilla_value",vanilla),new("declared_margin_funding",funding),new("working_scale_rounding",total.Subtract(vanilla.Add(funding)))]);
        return new(context,total,UnitQuantity.Specification);
    }
    private static MarketDataException CashBindingError()=>new("cash_option_binding",
        "Select the exact held cash-underlying option, native rule and saved margin funding inputs.");
}
