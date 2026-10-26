namespace OpenEtrm.MarketData;

public enum CashTieRule { ToEven = 1, AwayFromZero }
public enum CashRoundingStage { Cashflow = 1, PaymentTotal }
public sealed record CashRoundingConvention(
    ReferenceKey Key, ReferenceKey Currency, ExactDecimal Increment,
    CashTieRule TieRule, CashRoundingStage Stage);

public sealed record Money
{
    public ExactDecimal Amount { get; }
    public ReferenceKey Currency { get; }
    public Money(ExactDecimal amount, ReferenceKey currency)
    {
        RequireKey(currency); Amount = amount; Currency = currency;
    }
    internal static void RequireKey(ReferenceKey key)
    {
        if (key.Revision.CatalogId == Guid.Empty || key.Revision.RevisionId == Guid.Empty || string.IsNullOrWhiteSpace(key.Code))
            throw new MarketDataException("missing_reference", "Exact reference identity is required.");
    }
    public string ToString(AmountFormat format, AmountLabel currency) => Amount.ToString(format) + " " + currency.Require(Currency);
    private void Same(Money other)
    {
        if (Currency != other.Currency)
            throw new MarketDataException("currency_mismatch", "Money arithmetic requires the exact same currency reference.");
    }
    public Money Add(Money other) { Same(other); return new(Amount + other.Amount, Currency); }
    public Money Subtract(Money other) { Same(other); return new(Amount - other.Amount, Currency); }
    public Money Scale(ExactDecimal scalar) => new(Amount * scalar, Currency);
    public Money Round(CashRoundingConvention convention, CashRoundingStage stage)
    {
        RequireKey(convention.Key);
        if (convention.Key.Revision != Currency.Revision || convention.Currency != Currency ||
            convention.Stage != stage || !Enum.IsDefined(stage))
            throw new MarketDataException("rounding_policy", "Currency, reference revision and requested rounding stage must match.");
        return new(Amount.RoundToIncrement(convention.Increment, convention.TieRule), Currency);
    }
}

public sealed record Quantity
{
    public ExactDecimal Amount { get; }
    public ReferenceKey Specification { get; }
    public Quantity(ExactDecimal amount, ReferenceKey specification)
    {
        Money.RequireKey(specification); Amount = amount; Specification = specification;
    }
    public string ToString(AmountFormat format, AmountLabel unit) => Amount.ToString(format) + " " + unit.Require(Specification);
    public Quantity ConvertTo(ReferenceKey target, UnitConverter converter, AmountFormat? rounding = null) => converter.Convert(this, target, rounding);
    public Quantity Add(Quantity other)
    {
        if (Specification != other.Specification)
            throw new MarketDataException("quantity_mismatch", "Quantity arithmetic requires the exact same unit/commodity/basis specification.");
        return new(Amount + other.Amount, Specification);
    }
    public Quantity Scale(ExactDecimal scalar) => new(Amount * scalar, Specification);
    public ExactQuantity Times(QuantityFactor factor,ReferenceKey target,UnitConverter converter)=>
        factor.Apply(ExactQuantity.FromQuantity(this),target,converter);
}

// A working price unit is independent of a quote's observed-value domain and tick.
// Intermediate prices retain their units without inheriting final quote limits.
public sealed record PriceUnit
{
    public ReferenceKey Currency { get; }
    public ReferenceKey Quantity { get; }
    public ExactDecimal Multiplier { get; }
    public PriceUnit(ReferenceKey currency, ReferenceKey quantity, ExactDecimal multiplier)
    {
        Money.RequireKey(currency); Money.RequireKey(quantity);
        if (currency.Revision != quantity.Revision || multiplier.CompareTo(default) <= 0)
            throw new MarketDataException("quote_specification", "Price unit requires coherent references and a positive quote multiplier.");
        Currency = currency; Quantity = quantity; Multiplier = multiplier;
    }
    public static PriceUnit FromQuote(QuoteSpecification specification)
    {
        Money.RequireKey(specification.Key);
        if (specification.Key.Revision != specification.Currency.Revision)
            throw new MarketDataException("quote_specification", "Quote identity and currency revision must match.");
        return new(specification.Currency, specification.Quantity, specification.Multiplier);
    }
}

public sealed record NumericValue
{
    public ExactDecimal Value { get; }
    public ValueSpecification Specification { get; }
    public NumericValue(ExactDecimal value, ValueSpecification specification)
    {
        specification.Validate();
        if (value.CompareTo(specification.Minimum) < 0 || value.CompareTo(specification.Maximum) > 0)
            throw new MarketDataException("price_domain", "Numeric value is outside its declared domain.");
        Value = value; Specification = specification;
    }
}

public sealed record UnitPrice
{
    public ExactDecimal Value { get; }
    public PriceUnit Unit { get; }
    public UnitPrice(ExactDecimal value, PriceUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        Value = value; Unit = unit;
    }
    public UnitPrice(ExactDecimal value, QuoteSpecification specification) : this(value, PriceUnit.FromQuote(specification))
    {
        if (specification.Minimum.CompareTo(specification.Maximum) > 0 ||
            value.CompareTo(specification.Minimum) < 0 || value.CompareTo(specification.Maximum) > 0)
            throw new MarketDataException("quote_specification", "Price is outside its explicit quote domain.");
    }
    internal void RequireQuote(QuoteSpecification specification)
    {
        if (Unit != PriceUnit.FromQuote(specification))
            throw new MarketDataException("unit_mismatch", "Calculated price must retain the exact selected quote unit.");
        if (Value.CompareTo(specification.Minimum) < 0 || Value.CompareTo(specification.Maximum) > 0)
            throw new MarketDataException("price_domain", "Calculated value lies outside the declared domain.");
    }
    public string ToString(AmountFormat format, AmountLabel currency, AmountLabel quantity) =>
        Value.ToString(format) + " " + currency.Require(Unit.Currency) + "/" + quantity.Require(Unit.Quantity) +
        (Unit.Multiplier == ExactDecimal.Parse("1") ? "" : " × " + Unit.Multiplier);
    public UnitPrice ConvertTo(ReferenceKey target, UnitConverter converter, AmountFormat? rounding = null) => converter.Convert(this, target, rounding);
    public UnitPrice Add(UnitPrice other)
    {
        Same(other);
        return new(Value + other.Value, Unit);
    }
    private void Same(UnitPrice other)
    {
        if (Unit != other.Unit)
            throw new MarketDataException("unit_mismatch", "Price arithmetic requires the exact same currency, quantity and quote multiplier.");
    }
    public UnitPrice Subtract(UnitPrice other) { Same(other); return new(Value - other.Value, Unit); }
    public UnitPrice Times(NumericValue factor)
    {
        var spec = factor.Specification;
        if (spec.Key.Revision != Unit.Currency.Revision ||
            spec.Kind == NumericKind.QuantityRatio && spec.NumeratorQuantity != Unit.Quantity)
            throw new MarketDataException("multiplier_type", "Multiplier must cancel the exact price denominator in the selected reference revision.");
        var unit = spec.Kind == NumericKind.Dimensionless ? Unit :
            new PriceUnit(Unit.Currency, spec.DenominatorQuantity!.Value, Unit.Multiplier);
        return new(Value * factor.Value, unit);
    }
    public Money Times(Quantity quantity)
        =>ExactTimes(quantity).ToMoney();
    public ExactMoney ExactTimes(Quantity quantity)
    {
        if (Unit.Quantity != quantity.Specification)
            throw new MarketDataException("quantity_mismatch", "Price denominator and quantity specification must match exactly.");
        return ExactMoney.WeightedSum(Unit.Currency, new[] {(
            (IEnumerable<(ExactDecimal, ExactDecimal)>)new[] {(Value, ExactDecimal.Parse("1"))},
            new[] {Unit.Multiplier, quantity.Amount})});
    }
}
