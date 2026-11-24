using System.Globalization;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Options;

public sealed record HolderChoiceInputs(SignedRatio Receives, SignedRatio Pays, SignedRatio Certain,
    LatticeDecimal? ReceivesVolatility, LatticeDecimal? PaysVolatility, LatticeDecimal? Correlation, LatticeDecimal Years);

// The holder may take the whole undiscounted net amount at exercise or walk away.
public static class HolderChoice
{
    private static readonly LatticeDecimal One = LatticeDecimal.Parse("1");
    private static readonly LatticeDecimal NegativeOne = LatticeDecimal.Parse("-1");
    private static readonly BigInteger LatticeScale = BigInteger.Pow(10, LatticeDecimal.FractionDigits);

    public static ExactDecimal Value(HolderChoiceInputs inputs)
    {
        if (inputs is null)
            throw InvalidInputs();
        var (receives, pays, certain, receivesVolatility, paysVolatility, correlation, years) = inputs;
        var hasReceives = receives.Numerator.Sign > 0;
        var hasPays = pays.Numerator.Sign > 0;
        if (!ValidLeg(receives, receivesVolatility) || !ValidLeg(pays, paysVolatility) ||
            certain.Denominator.Sign <= 0 || years.CompareTo(default) < 0 ||
            correlation.HasValue != (hasReceives && hasPays) ||
            (correlation is { } rho && (rho.CompareTo(NegativeOne) < 0 || rho.CompareTo(One) > 0)))
            throw InvalidInputs();

        if (years == default)
        {
            var intrinsic = receives - pays + certain;
            return intrinsic.Numerator.Sign > 0 ? ToExact(intrinsic) : default;
        }

        if (certain.Numerator.Sign <= 0)
        {
            if (!hasReceives)
                return default;
            if (hasPays)
                return Call(receives, pays, SignedRatio.Zero - certain,
                    receivesVolatility!.Value, paysVolatility!.Value, correlation!.Value);
            if (certain.Numerator.IsZero)
                return ToExact(receives);
            return Call(receives, SignedRatio.Zero - certain, SignedRatio.Zero,
                receivesVolatility!.Value, default, default);
        }

        if (!hasPays)
            return ToExact(receives + certain);

        // Reverse the legs for a positive certain amount, then apply put-call parity exactly.
        var reverseCall = hasReceives
            ? Call(pays, receives, certain, paysVolatility!.Value, receivesVolatility!.Value, correlation!.Value)
            : Call(pays, certain, SignedRatio.Zero, paysVolatility!.Value, default, default);
        return reverseCall + ToExact(receives - pays + certain);

        ExactDecimal Call(SignedRatio longPrice, SignedRatio shortPrice, SignedRatio strike,
            LatticeDecimal longVolatility, LatticeDecimal shortVolatility, LatticeDecimal rho) =>
            KirkSpreadCall.UnitPrice(new(ToLatticeAmount(longPrice), ToLatticeAmount(shortPrice), ToLatticeAmount(strike),
                longVolatility, shortVolatility, rho, years, One)).ToExact();
    }

    private static bool ValidLeg(SignedRatio amount, LatticeDecimal? volatility) =>
        amount.Denominator.Sign > 0 && amount.Numerator.Sign >= 0 &&
        volatility.HasValue == (amount.Numerator.Sign > 0) &&
        (volatility is null || volatility.Value.CompareTo(default) >= 0);

    private static MarketDataException InvalidInputs() =>
        new("holder_choice_inputs",
            "The holder's choice requires nonnegative legs and time, a certain amount, nonnegative volatility for " +
            "each positive leg, and correlation from -1 to 1 only when both legs are positive.");

    private static LatticeDecimal ToLatticeAmount(SignedRatio nonnegative)
    {
        if (!nonnegative.TryScaleToInteger(LatticeScale, out var scaled))
            throw new MarketDataException("precision_loss", "The spread call amount must be exact at 30 fractional digits.");
        var places = LatticeDecimal.FractionDigits;
        var digits = scaled.ToString(CultureInfo.InvariantCulture).PadLeft(places + 1, '0');
        return LatticeDecimal.Parse(digits.Insert(digits.Length - places, "."));
    }

    private static ExactDecimal ToExact(SignedRatio value)
    {
        if (!value.TryScaleToInteger(ExactDecimal.WorkingScale, out var scaled))
            throw new MarketDataException("precision_loss", "The exact holder's choice amount exceeds 18 fractional digits.");
        return ExactDecimal.FromScaled(scaled);
    }
}
