namespace OpenEtrm.MarketData.Trading;

// One cash-settled indexed forward. Delivery/payment obligations for physical
// products retain their own explicitly qualified composition contracts.
[EconomicDefinition("indexed_forward_contract_v1")]
public sealed record ForwardContract:SidedInstrument
{
    public FixedSide Fixed=>(FixedSide)Sides[0].Side;
    public FloatingSide Floating=>(FloatingSide)Sides[1].Side;
    [IdentityField]public bool Discounting{get;}
    public override ReferenceKey Currency=>Floating.Quote.Currency;
    public override Quantity UnitQuantity=>Floating.UnitQuantity;
    public override DefinitionIdentity PricingBindingIdentity=>Floating.PricingBindingIdentity;
    public ForwardContract(Guid id,FixedSide fixedSide,FloatingSide floating,bool discounting)
        :base(id,[new(fixedSide,ExactDecimal.Parse("-1")),new(floating,ExactDecimal.Parse("1"))])
    {
        if(fixedSide.Price.Unit!=PriceUnit.FromQuote(floating.Quote)||fixedSide.UnitQuantity!=floating.UnitQuantity||
            fixedSide.DeliveryFrom!=floating.DeliveryFrom||fixedSide.DeliveryTo!=floating.DeliveryTo||fixedSide.PaymentAt!=floating.PaymentAt)
            throw new MarketDataException("forward_definition","A cash-settled forward requires compatible fixed/indexed sides on the same delivery period and payment instant.");
        Discounting=discounting;
    }
    public override InstrumentValuation PV(SideValuationContext context)=>FinancialSwapInstrument.RequireDiscounting(base.PV(context),Discounting);
}
