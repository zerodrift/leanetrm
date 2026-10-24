using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData;

// Precision and ties are explicit. Formatting never changes a retained amount;
// a conversion may explicitly select the same precision for its final rounding.
public sealed record AmountFormat
{
    public int DecimalPlaces { get; }
    public CashTieRule TieRule { get; }
    public bool GroupThousands { get; }
    public bool TrimTrailingZeros { get; }
    public AmountFormat(int decimalPlaces, CashTieRule tieRule, bool groupThousands, bool trimTrailingZeros)
    {
        if (decimalPlaces is < 0 or > 18 || !Enum.IsDefined(tieRule))
            throw new MarketDataException("display_precision", "Choose 0–18 decimal places and an explicit tie rule.");
        DecimalPlaces = decimalPlaces; TieRule = tieRule; GroupThousands = groupThousands; TrimTrailingZeros = trimTrailingZeros;
    }
    public static AmountFormat ForTick(ExactDecimal tick)
    {
        if (tick.CompareTo(default) <= 0) throw new MarketDataException("display_precision", "A positive decimal tick is required.");
        var text = tick.ToString(); var point = text.IndexOf('.');
        return new(point < 0 ? 0 : text.Length - point - 1, CashTieRule.ToEven, true, false);
    }
    internal BigInteger RoundedInteger(BigInteger numerator, BigInteger denominator)
    {
        if (denominator <= 0) throw new MarketDataException("numeric_division", "A positive denominator is required.");
        return IntegerRounding.RoundQuotient(numerator * BigInteger.Pow(10, DecimalPlaces), denominator,
            TieRule == CashTieRule.ToEven ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);
    }
    internal ExactDecimal RoundFraction(BigInteger numerator, BigInteger denominator) =>
        ExactDecimal.FromScaled(RoundedInteger(numerator, denominator) * BigInteger.Pow(10, 18 - DecimalPlaces));
    public string FormatFraction(BigInteger numerator, BigInteger denominator)
    {
        if (numerator.GetBitLength() > 4096 || denominator.GetBitLength() > 4096)
            throw new MarketDataException("numeric_range", "Display fractions exceed the supported bound.");
        var rounded = RoundedInteger(numerator, denominator);
        var whole = BigInteger.DivRem(BigInteger.Abs(rounded), BigInteger.Pow(10, DecimalPlaces), out var fraction);
        var integral = whole.ToString(GroupThousands ? "N0" : "0", CultureInfo.InvariantCulture);
        var suffix = DecimalPlaces == 0 ? "" : fraction.ToString("D" + DecimalPlaces, CultureInfo.InvariantCulture);
        if (TrimTrailingZeros) suffix = suffix.TrimEnd('0');
        return (rounded.Sign < 0 ? "-" : "") + integral + (suffix.Length == 0 ? "" : "." + suffix);
    }
    public string Format(ExactDecimal amount) => FormatFraction(amount.ScaledValue, ExactDecimal.WorkingScale);
}

public sealed record AmountLabel
{
    public const int MaximumSymbolLength = 80;
    public ReferenceKey Key { get; }
    public string Symbol { get; }
    public static bool IsSupportedSymbol(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return false;
        var remaining = symbol.AsSpan();
        var length = 0; var hasNonWhitespace = false;
        while (!remaining.IsEmpty)
        {
            if (++length > MaximumSymbolLength ||
                Rune.DecodeFromUtf16(remaining, out var character, out var consumed) != OperationStatus.Done || Rune.IsControl(character)) return false;
            hasNonWhitespace |= !Rune.IsWhiteSpace(character);
            remaining = remaining[consumed..];
        }
        return hasNonWhitespace;
    }
    public AmountLabel(ReferenceKey key, string symbol)
    {
        Money.RequireKey(key);
        if (!IsSupportedSymbol(symbol))
            throw new MarketDataException("display_reference", "Supply a resolved currency or quantity label.");
        Key = key; Symbol = symbol;
    }
    internal string Require(ReferenceKey key) => key == Key ? Symbol :
        throw new MarketDataException("display_reference", "The display label must identify the amount's exact reference.");
}
