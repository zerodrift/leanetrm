using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

// A declared physical exchange before its principal is fixed. The selected
// marks and carrying inputs are valuation terms, never accepted final evidence.
[EconomicDefinition("gas_unfixed_delivery_exchange_v1")]
public sealed record GasUnfixedPhysicalDeliveryInstrument:Instrument
{
    public const string Version="gas_unfixed_delivery_exchange_pv_v1";
    [IdentityField]public GasPhysicalDeliveryTerms Terms{get;}
    [IdentityField]public CurveRequest PrincipalMark{get;}
    [IdentityField]public PriceUnit PriceUnit{get;}
    public override ReferenceKey Currency=>Terms.Currency;
    public override Quantity UnitQuantity=>new(ExactDecimal.Parse("1"),Terms.Quantity);
    public override bool SupportsQuantityScaling=>true;
    public GasUnfixedPhysicalDeliveryInstrument(Guid id,GasPhysicalDeliveryTerms terms,CurveRequest principalMark,PriceUnit priceUnit):base(id)
    {
        Require(terms,principalMark,priceUnit);Terms=terms;PrincipalMark=principalMark;PriceUnit=priceUnit;
    }
    private static void Require(GasPhysicalDeliveryTerms terms,CurveRequest principalMark,PriceUnit priceUnit)
    {
        if(terms is null||priceUnit is null||priceUnit.Currency!=terms.Currency||priceUnit.Quantity!=terms.Quantity||
            principalMark.Curve.Revision.CatalogId==Guid.Empty||principalMark.Curve.Revision.RevisionId==Guid.Empty||
            string.IsNullOrWhiteSpace(principalMark.Curve.Code)||principalMark.Contract.Id==Guid.Empty||principalMark.Contract.Revision!=principalMark.Curve.Revision)
            throw new MarketDataException("gas_unfixed_delivery_terms","Select exact physical terms, principal mark and native quotation units.");
    }
    public static GasUnfixedPhysicalDeliveryInstrument Create(GasPhysicalDeliveryTerms terms,CurveRequest principalMark,PriceUnit priceUnit)
    {
        Require(terms,principalMark,priceUnit);
        return new(ContractIdentity.FromCanonical(DefinitionIdentity.CanonicalTerm(new object?[]{Version,terms,principalMark,priceUnit})),terms,principalMark,priceUnit);
    }
    public ImmutableArray<SidePaymentKey> RequiredDiscounts(DateTimeOffset calculationAt)=>
        GasDeliveryExchange.RequiredDiscounts(Id,Terms,calculationAt);
    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        var discounted=RequiredDiscounts(context.CalculationAt);
        return new(Id,Version,[..new[]{Terms.Mark,PrincipalMark}.Distinct()],Terms.Quantity,Currency){
            Time=discounted.IsEmpty?ValuationTimeDependency.Independent:ValuationTimeDependency.Between(context.CalculationAt,discounted.Min(p=>p.PaymentAt)),
            DiscountInputs=[..discounted.Select(p=>new DiscountInput(p,context.PaymentDiscounting(Id)))]};
    }
    public override UnitValuation PV(UnitValuationContext context)
    {
        if(context.Measure!=ValuationMeasure.PresentValue)throw new MarketDataException("valuation_measure","An unfixed physical exchange implements remaining PV.");
        ValidateValuationState(context.BindingState);
        return new(context,ExchangeValue(context),Terms.Quantity);
    }
    internal ExactMoney ExchangeValue(UnitValuationContext context)
    {
        var asset=context.Inputs.ExactPrice(Terms.Mark);var principal=context.Inputs.ExactPrice(PrincipalMark);
        if(asset.Unit!=PriceUnit||principal.Unit!=PriceUnit)
            throw new MarketDataException("gas_delivery_unit","The asset and principal marks require their exact selected native quotation; conversion must be configured explicitly.");
        return GasDeliveryExchange.Value(Id,Terms,UnitQuantity,asset,principal,context);
    }
}

// One carrying calculation serves the unfixed and fixed principal contracts.
internal static class GasDeliveryExchange
{
    private static SidePaymentKey Cash(Guid id,GasPhysicalDeliveryTerms terms)=>new(id,0,terms.CashDueAt,terms.Currency);
    private static SidePaymentKey Physical(Guid id,GasPhysicalDeliveryTerms terms)=>new(id,1,terms.From,terms.Currency);
    internal static ImmutableArray<SidePaymentKey> RequiredDiscounts(Guid id,GasPhysicalDeliveryTerms terms,DateTimeOffset calculationAt)
    {
        if(!TradingInstant.IsSupported(calculationAt))throw new MarketDataException("gas_delivery_discount","Select an exact supported calculation instant.");
        return terms.Carrying==GasHoldingCarrying.CurrencyCurve?[..new[]{Physical(id,terms),Cash(id,terms)}.Where(p=>p.PaymentAt>calculationAt)]:[];
    }
    internal static ExactMoney Value(Guid id,GasPhysicalDeliveryTerms terms,Quantity quantity,ExactUnitPrice mark,ExactUnitPrice principal,UnitValuationContext context)
    {
        if(mark.Unit!=principal.Unit)throw new MarketDataException("gas_delivery_unit","The delivery mark and principal require one exact native quotation; conversion must be configured explicitly.");
        var physical=mark.ExactTimes(quantity);var cash=principal.ExactTimes(quantity);
        var keys=RequiredDiscounts(id,terms,context.CalculationAt);
        if(!keys.IsEmpty)
        {
            var factors=context.Inputs.Discounts(keys);
            if(factors.Length!=keys.Length||keys.Any(key=>factors.Count(f=>f.Payment==key)!=1))
                throw new MarketDataException("gas_delivery_discount","Select each exact physical-value and cash-due currency carrying input.");
            foreach(var factor in factors)
                if(factor.Payment==Physical(id,terms))physical=physical.Scale(factor.DiscountFactor);else cash=cash.Scale(factor.DiscountFactor);
        }
        return physical.Subtract(cash);
    }
}
