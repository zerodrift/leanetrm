using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

public sealed record UnitDefinition(ReferenceKey Key, ReferenceKey Dimension, string Symbol);
public sealed record UnitConversion(ReferenceKey From, ReferenceKey To, ExactDecimal Numerator, ExactDecimal Denominator);

// A supplied immutable catalog is qualified once. Inverse/chained paths are
// valid only when every path in a connected component agrees exactly.
public sealed class UnitConverter
{
    public const string Implementation = "consistent_exact_unit_ratios_v1";
    private readonly ImmutableDictionary<ReferenceKey, UnitDefinition> units;
    private readonly ImmutableDictionary<ReferenceKey, QuantitySpecification> quantities;
    private readonly Dictionary<ReferenceKey, (ReferenceKey Root, ExactRatio Scale)> scales = [];
    private static ExactRatio Multiply(ExactRatio left,ExactRatio right)
    {
        try{return left.Times(right);}
        catch(ArgumentOutOfRangeException){throw Invalid();}
    }
    private static MarketDataException Invalid() => new("unit_conversion", "Unit definitions require coherent exact revisions, positive ratios and consistent conversion paths.");
    public UnitConverter(ImmutableArray<UnitDefinition> definitions, ImmutableArray<UnitConversion> conversions,
        ImmutableArray<QuantitySpecification> quantitySpecifications)
    {
        if (definitions.IsDefaultOrEmpty || definitions.Length > 256 || conversions.IsDefault || conversions.Length > 1024 ||
            quantitySpecifications.IsDefault || definitions.Any(u=>u is null) || conversions.Any(c=>c is null) || quantitySpecifications.Any(q=>q is null) || definitions.Select(u => u.Key).Distinct().Count() != definitions.Length ||
            quantitySpecifications.Select(q => q.Key).Distinct().Count() != quantitySpecifications.Length) throw Invalid();
        units = definitions.ToImmutableDictionary(u => u.Key); quantities = quantitySpecifications.ToImmutableDictionary(q => q.Key);
        var edges = definitions.ToDictionary(u => u.Key, _ => new List<(ReferenceKey To, ExactRatio Scale)>());
        foreach (var unit in definitions)
        {
            Money.RequireKey(unit.Key); Money.RequireKey(unit.Dimension);
            if (unit.Key.Revision != definitions[0].Key.Revision || unit.Dimension.Revision != unit.Key.Revision || !AmountLabel.IsSupportedSymbol(unit.Symbol)) throw Invalid();
        }
        foreach (var quantity in quantitySpecifications)
        {
            if (!units.TryGetValue(quantity.Unit, out var unit) || quantity.Dimension != unit.Dimension ||
                new[] { quantity.Key, quantity.Commodity, quantity.MeasurementBasis }.Any(k => k.Revision != unit.Key.Revision || string.IsNullOrWhiteSpace(k.Code))) throw Invalid();
        }
        foreach (var conversion in conversions)
        {
            if (!units.TryGetValue(conversion.From, out var from) || !units.TryGetValue(conversion.To, out var to) ||
                from.Dimension != to.Dimension || conversion.From == conversion.To) throw Invalid();
            if(conversion.Numerator.CompareTo(default)<=0||conversion.Denominator.CompareTo(default)<=0)throw Invalid();
            var ratio = new ExactRatio(conversion.Numerator.ScaledValue, conversion.Denominator.ScaledValue);
            edges[from.Key].Add((to.Key, ratio)); edges[to.Key].Add((from.Key, ratio.Inverse()));
        }
        foreach (var root in definitions.Select(u => u.Key).OrderBy(k => k.Code, StringComparer.Ordinal))
        {
            if (scales.ContainsKey(root)) continue;
            scales.Add(root, (root, new(1, 1))); var queue = new Queue<ReferenceKey>(); queue.Enqueue(root);
            while (queue.TryDequeue(out var at))
                foreach (var edge in edges[at])
                {
                    var expected = Multiply(scales[at].Scale,edge.Scale);
                    if (scales.TryGetValue(edge.To, out var prior)) { if (prior.Scale != expected) throw Invalid(); }
                    else { scales.Add(edge.To, (root, expected)); queue.Enqueue(edge.To); }
                }
        }
    }
    public QuantitySpecification Specification(ReferenceKey key) => quantities.TryGetValue(key, out var value) ? value :
        throw new MarketDataException("missing_reference", "The exact quantity specification is unavailable in this unit catalog.");
    public bool CanConvert(ReferenceKey from,ReferenceKey to)
    {
        _=Specification(from);_=Specification(to);
        try{_=Between(from,to);return true;}catch(MarketDataException e) when(e.Code=="quantity_conversion"){return false;}
    }
    public string Symbol(ReferenceKey specification) => units[Specification(specification).Unit].Symbol;
    private ExactRatio Between(ReferenceKey from, ReferenceKey to)
    {
        var source = Specification(from); var target = Specification(to);
        if (source.Commodity != target.Commodity || source.MeasurementBasis != target.MeasurementBasis || source.Dimension != target.Dimension ||
            scales[source.Unit].Root != scales[target.Unit].Root)
            throw new MarketDataException("quantity_conversion", "Conversion requires the same exact commodity, measurement basis, dimension and a declared unit path.");
        return Multiply(scales[target.Unit].Scale,scales[source.Unit].Scale.Inverse());
    }
    private static ExactDecimal Scale(ExactDecimal value, ExactRatio ratio, AmountFormat? rounding)
    {
        var numerator = value.ScaledValue * ratio.Numerator;
        if (rounding is not null) return rounding.RoundFraction(numerator, ratio.Denominator * ExactDecimal.WorkingScale);
        var result = BigInteger.DivRem(numerator, ratio.Denominator, out var remainder);
        if (!remainder.IsZero) throw new MarketDataException("precision_loss", "This conversion requires an explicit final rounding policy.");
        return ExactDecimal.FromScaled(result);
    }
    public Quantity Convert(Quantity value, ReferenceKey target, AmountFormat? rounding = null) =>
        new(Scale(value.Amount, Between(value.Specification, target), rounding), target);
    public ExactQuantity Convert(ExactQuantity value,ReferenceKey target)
    {
        var scaled=value.Scale(Between(value.Specification,target));
        return new(scaled.NumeratorValue,scaled.DenominatorValue,target);
    }
    public UnitPrice Convert(UnitPrice value, ReferenceKey target, AmountFormat? rounding = null) =>
        new(Scale(value.Value, Between(value.Unit.Quantity, target).Inverse(), rounding), new PriceUnit(value.Unit.Currency, target, value.Unit.Multiplier));
}
