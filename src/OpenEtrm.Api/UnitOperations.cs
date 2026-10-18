using System.Collections.Immutable;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record UnitConvertRequest(string? Value, string? From, string? To, int? DecimalPlaces, string? TieRule);
public sealed record DisplayNumberRequest(string Value, int? DecimalPlaces, string? Tick, bool TrimTrailingZeros = false);
public static class UnitOperations
{
    public static UnitConverter Converter(UnitCatalogData data)
    {
        ReferenceKey R(string code) => new(data.Revision, code);
        return new(data.References["units"].Select(u => new UnitDefinition(R(u.Text("code")), R(u.Text("dimension_code")), u.Text("symbol"))).ToImmutableArray(),
            data.References["unit_conversions"].Select(c => new UnitConversion(R(c.Text("from_unit_code")), R(c.Text("to_unit_code")), c.Number("factor_numerator"), c.Number("factor_denominator"))).ToImmutableArray(),
            data.References["quantity_specifications"].Select(q => new QuantitySpecification(R(q.Text("code")), R(q.Text("commodity_code")), R(q.Text("basis_code")), R(q.Text("unit_code")), R(q.Text("dimension_code")))).ToImmutableArray());
    }
    public static object Convert(UnitCatalogData data, UnitConvertRequest body)
    {
        if (body.Value is null || body.From is null || body.To is null ||
            body.DecimalPlaces is null && body.TieRule is not null || body.DecimalPlaces is not null && body.TieRule is not("to_even" or "away_from_zero"))
            throw new MarketDataException("unit_conversion", "Choose source and target quantities and either exact conversion or explicit final precision/tie rule.");
        var converter = Converter(data);
        var source = new Quantity(ExactDecimal.Parse(body.Value), new(data.Revision, body.From));
        AmountFormat? rounding = body.DecimalPlaces is {} places ? new(places, body.TieRule == "to_even" ? CashTieRule.ToEven : CashTieRule.AwayFromZero, false, false) : null;
        var result = source.ConvertTo(new(data.Revision, body.To), converter, rounding);
        var display = rounding ?? new AmountFormat(18, CashTieRule.ToEven, true, true);
        var text = result.ToString(display, new(result.Specification, converter.Symbol(result.Specification)));
        bool exact;
        try { exact = source.ConvertTo(result.Specification, converter).Amount == result.Amount; }
        catch (MarketDataException e) when (e.Code == "precision_loss") { exact = false; }
        return new { value = result.Amount.ToString(), formatted = text, exact, source = source.Specification, target = result.Specification,
            implementation = UnitConverter.Implementation, rounding = body.DecimalPlaces is null ? null : new { body.DecimalPlaces, body.TieRule } };
    }
    public static string[] Display(ImmutableArray<DisplayNumberRequest> requests)
    {
        if (requests.IsDefault || requests.Length > 256) throw new MarketDataException("display_precision", "Format at most 256 amounts per request.");
        return requests.Select(r =>
        {
            if (r is null) throw new MarketDataException("display_precision", "A display entry cannot be null.");
            var format = r.Tick is null ? new AmountFormat(r.DecimalPlaces ?? throw new MarketDataException("display_precision", "Display precision must be explicit."), CashTieRule.ToEven, true, r.TrimTrailingZeros) : AmountFormat.ForTick(ExactDecimal.Parse(r.Tick));
            if (r.Value is null || r.Value.Length > 256) throw new MarketDataException("numeric_format", "The display value exceeds its supported bound.");
            var ratio = r.Value.Split('/');
            if (ratio.Length == 1) return ExactDecimal.Parse(r.Value).ToString(format);
            if (ratio.Length != 2 || !System.Numerics.BigInteger.TryParse(ratio[0], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ||
                !System.Numerics.BigInteger.TryParse(ratio[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d))
                throw new MarketDataException("numeric_format", "Supply an exact decimal or rational display value.");
            return format.FormatFraction(n, d);
        }).ToArray();
    }
}
