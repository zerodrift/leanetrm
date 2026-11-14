using System.Text.Json.Serialization;
using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// Operation-selected pricing inputs for one real held option. They establish
// neither an accepted exercise nor a generated future, title or cash payment.
public sealed record FuturesOptionPhysicalValueBinding(PhysicalOptionPolicy Policy,FuturesOptionAutomaticRule Rule,
    PhysicalOptionTerminal Terminal,FuturesMarginPolicy Margin,CapturedPriceSource Asset,
    [property:JsonIgnore]ISideDiscounting? Carrying,[property:JsonIgnore]ISideDiscounting MarginDiscounting,string MarkInterpretation);

public sealed partial record FuturesOptionInstrument
{
    private GasPhysicalDeliveryTerms PhysicalTerms(UnitValuationContext context,FuturesOptionMarketBinding reference,FuturesOptionPhysicalValueBinding selected)
    {
        PhysicalFuturesOptionModel.RequirePolicy(selected.Policy);
        if(selected.Margin is not{Implementation:FuturesMarginPolicy.DeliveryVersion,Delivery:{} delivery}||
            delivery.ProfileId==Guid.Empty||delivery.ProfileRevision<1||delivery.Terms is not GasPhysicalDeliveryTerms gas||
            gas.Series!=Series.UnderlyingSeries||gas.Currency!=Currency||selected.Margin.Contract!=reference.Underlying.Contract||
            selected.Asset is null||selected.Asset.Market!=gas.Mark||selected.Asset.Snapshot.Knowledge!=context.Snapshot.Knowledge||
            selected.Asset.Snapshot.ValuationAt!=context.Snapshot.ValuationAt||selected.Asset.Snapshot.MarketCutoff!=context.Snapshot.MarketCutoff||
            selected.Asset.Valuation.Scenario.Plan.GetPoint(selected.Asset.Point).Unit.Currency!=Currency||
            selected.Asset.Valuation.Scenario.Plan.GetPoint(selected.Asset.Point).Unit.Quantity!=gas.Quantity||
            (gas.Carrying==GasHoldingCarrying.CurrencyCurve)!=(selected.Carrying is not null)||selected.MarginDiscounting is null||
            selected.Rule is null||selected.Rule.Key.Revision!=Series.Key.Revision||selected.Rule.Key.Code!=Series.AutomaticExerciseRule||selected.Rule.Version<1||
            !Enum.IsDefined(selected.Terminal)||selected.Terminal!=PhysicalOptionTerminal.Automatic&&!Series.ContraryInstructionsAllowed||
            selected.MarkInterpretation is not(FuturesPhysicalBasisValueBinding.SameDeliverable or FuturesPhysicalBasisValueBinding.ValuationProxy))
            throw PhysicalBindingError();
        _=selected.Rule.Decide(Series.Terms.Right,Series.Terms.Strike,Series.Terms.Strike);
        return gas;
    }
    private Guid DeliveryPaymentId(FuturesOptionPhysicalValueBinding selected)=>ContractIdentity.FromCanonical(
        DefinitionIdentity.CanonicalTerm(new object[]{PhysicalOptionPolicy.Version,SeriesKey,selected.Margin.Id,selected.Margin.Revision,
            selected.Margin.Delivery!.ProfileId,selected.Margin.Delivery.ProfileRevision}));
    private SidePaymentKey MarginPayment(FuturesMarginOccurrence occurrence)=>new(occurrence.Id,0,occurrence.DueAt,Currency);
    private UnitPricingContract DeclarePhysical(UnitValuationContext context,FuturesOptionMarketBinding reference,FuturesOptionPhysicalValueBinding selected)
    {
        var gas=PhysicalTerms(context,reference,selected);
        var margins=PhysicalFuturesOptionModel.RequiredMarginOccurrences(Series.Terms,selected.Margin,context.CalculationAt,reference.Policy);
        var physical=GasDeliveryExchange.RequiredDiscounts(DeliveryPaymentId(selected),gas,context.CalculationAt);
        return new(Id,PhysicalOptionPolicy.Version,[..new[]{reference.Underlying,gas.Mark}.Distinct()],UnitQuantity.Specification,Currency){
            // The selected economic consumer redeclares at its event clock. A
            // generic frozen-clock child cannot invent new margin-due inputs.
            Time=ValuationTimeDependency.Pinned,
            DiscountInputs=[new(new(Id,0,Series.Terms.ExpiryAt,Currency),reference.Discounting),
                ..margins.Select(o=>new DiscountInput(MarginPayment(o),selected.MarginDiscounting)),
                ..physical.Select(p=>new DiscountInput(p,selected.Carrying!){Source=selected.Asset.Snapshot})],
            Volatility=reference.Calibration is SolvedVolatility solved?[new(reference.Id,solved.Volatility.ToString(),VolatilityInput.Annualized)]:[],
            StaticDerivatives=new(StaticDerivativeRestriction.Version,"physical_option_reference_strategy_derivatives_not_qualified"),
            ScheduleIdentity=$"{PhysicalOptionPolicy.Version}:{selected.Margin.Delivery!.ProfileId:D}:{selected.Margin.Delivery.ProfileRevision}:{selected.MarkInterpretation}:"+
                ConnectedIdentity.Digest(new{selected.Policy,selected.Rule,selected.Terminal,selected.Margin,gas,
                    selected.Asset.Market,selected.Asset.Point,selected.Asset.ManifestId,selected.Asset.Snapshot})};
    }
    private UnitValuation PhysicalValue(UnitValuationContext context,FuturesOptionMarketBinding reference,FuturesOptionPhysicalValueBinding selected)
    {
        var gas=PhysicalTerms(context,reference,selected);var inputs=context.Inputs;
        if(inputs.SnapshotId!=reference.SnapshotId)throw PhysicalBindingError();
        var model=context.Market!.Model;
        model.RequireFuturesOptionDefinition(Series,reference.Underlying,reference.Settlement);
        var future=model.FuturesContract(selected.Margin.Contract);var quote=model.QuoteFor(reference.Underlying);
        if(future.Series.SettlementMethod!="physical"||Series.Multiplier!=future.LotSize.Amount*future.Series.Multiplier||
            selected.Asset.Valuation.Scenario.Plan.GetPoint(selected.Asset.Point).Unit!=PriceUnit.FromQuote(quote))throw PhysicalBindingError();
        var market=new FuturesMarketBinding(ContractIdentity.FromCanonical($"physical_option_future_market_v1:{Id:D}:{selected.Margin.Id:D}:{selected.Margin.Revision}"),
            reference.Underlying,model.ContractDefinition(selected.Margin.Contract),future.Series,quote,"physical",FuturesMarketBinding.Implementation);
        _=future.ValidateMarginPolicy(market,selected.Margin,model,context.Snapshot);
        inputs.RequireSource(selected.Asset);
        var a=inputs.ExactPrice(reference.Underlying);var b=inputs.ExactPrice(gas.Mark);
        if(a.Unit!=PriceUnit.FromQuote(quote)||b.Unit!=a.Unit)throw PhysicalBindingError();
        var expiry=Discount(context,reference);
        var discounts=GasDeliveryExchange.RequiredDiscounts(DeliveryPaymentId(selected),gas,context.CalculationAt);
        var factors=discounts.IsEmpty?ImmutableArray<SidePaymentFactor>.Empty:inputs.Discounts(discounts);
        var one=LatticeDecimal.Parse("1");
        var assetFactor=gas.Carrying==GasHoldingCarrying.Nominal?one:LatticeDecimal.From(factors.Single(f=>f.Payment.Ordinal==1).DiscountFactor);
        var principalFactor=gas.Carrying==GasHoldingCarrying.Nominal?one:LatticeDecimal.From(factors.Single(f=>f.Payment.Ordinal==0).DiscountFactor);
        var margins=PhysicalFuturesOptionModel.RequiredMarginOccurrences(Series.Terms,selected.Margin,context.CalculationAt,reference.Policy);
        var marginFactors=inputs.Discounts([..margins.Select(MarginPayment)]).ToDictionary(f=>f.Payment);
        var carrying=new PhysicalOptionCarrying(gas.From,gas.CashDueAt,assetFactor,principalFactor,
            [..margins.Select(o=>new PhysicalOptionMarginDiscount(o.Id,o.DueAt,LatticeDecimal.From(marginFactors[MarginPayment(o)].DiscountFactor)))]);
        var calibration=reference.Calibration is SolvedVolatility solved?solved with{Volatility=LatticeDecimal.Parse(inputs.Volatility(reference.Id).Format(30,MidpointRounding.ToEven))}:reference.Calibration;
        if(calibration is SolvedVolatility)reference.RecordTreePrice();
        var value=PhysicalFuturesOptionModel.Price(Series.Terms,LatticeDecimal.From(a.ToUnitPriceExact().Value),LatticeDecimal.From(b.ToUnitPriceExact().Value),
            calibration,LatticeDecimal.From(expiry.DiscountFactor),context.CalculationAt,reference.Policy,selected.Policy,selected.Rule,selected.Terminal,
            selected.Margin,carrying,context.Cancellation);
        ExactMoney Money(LatticeDecimal amount)=>ExactMoney.FromMoney(new(amount.ToExact(),Currency)).Scale(Series.Multiplier);
        var contract=Money(value.ContractReference);var continuation=Money(value.DeliveryContinuation);
        var total=Money(value.Total);var rounding=total.Subtract(contract.Add(continuation));
        // Preserve the option's total-price rounding once before its multiplier.
        // Any component-display difference is numerical value, never cash.
        context.RecordComponents([new("reference_strategy_contract",contract),new("declared_delivery_continuation",continuation),new("working_scale_rounding",rounding)]);
        return new(context,total,UnitQuantity.Specification);
    }
    private static MarketDataException PhysicalBindingError()=>new("physical_option_binding",
        "Select the exact held option, native delivery policy, automatic rule, captured asset mark and explicit carrying inputs.");
}
