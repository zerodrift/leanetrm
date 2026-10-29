namespace OpenEtrm.MarketData.Trading;

public sealed partial record FixedSide
{
    [IdentityField] public PpaSideTerms? IntervalDefinition { get; }
    public override PpaSideTerms Terms=>IntervalDefinition??throw new MarketDataException("side_schedule_terms","This fixed side uses explicit dated period parameters.");
    public FixedSide(Guid id,PpaSideTerms terms,FixedPriceTerm price):base(id)
    {
        if(terms is null||terms.Schedule is null||terms.Schedule.Delivery!=terms.Delivery||price is null||price.Unit.Currency!=terms.Currency)
            throw PpaErrors.Error("ppa_price_rule","fixed_price","Supply complete native interval terms and an explicit fixed price in their currency.");
        IntervalDefinition=terms;Price=price;UnitQuantity=terms.UnitQuantity;
    }
}

public sealed partial record FloatingSide
{
    [IdentityField] public PpaSideTerms? IntervalDefinition { get; }
    [IdentityField] public IndexPrice? IntervalPriceDefinition { get; }
    public override PpaSideTerms Terms=>IntervalDefinition??throw new MarketDataException("side_schedule_terms","This floating side uses explicit dated period parameters.");
    public IndexPrice Price=>IntervalPriceDefinition??throw new MarketDataException("side_schedule_terms","Select the dated observation parameters of this floating side.");
    public FloatingSide(Guid id,PpaSideTerms terms,IndexPrice price):base(id)
    {
        if(terms is null||terms.Schedule is null||terms.Schedule.Delivery!=terms.Delivery||price is null||price.Multiplier.CompareTo(default)<=0||
            price.Adder is null||price.Adder.Unit.Currency!=terms.Currency||string.IsNullOrWhiteSpace(price.Curve.Code))
            throw PpaErrors.Error("ppa_price_rule","index","Supply complete native interval terms, exact curve, positive multiplier and explicit adder.");
        IntervalDefinition=terms;IntervalPriceDefinition=price;UnitQuantity=terms.UnitQuantity;
    }
}
