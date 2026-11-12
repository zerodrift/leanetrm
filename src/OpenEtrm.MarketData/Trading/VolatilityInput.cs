using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Exact calibrated input, retaining up to 30 decimal places without imposing a
// model's numerical type on the common valuation contract.
public sealed record VolatilityInput
{
    public const string Annualized="annualized_decimal_volatility_exact_series_v1";
    public Guid Id{get;}
    public string Value{get;}
    public string Convention{get;}
    public VolatilityInput(Guid id,string value,string convention)
    {
        if(id==Guid.Empty||convention!=Annualized||value is null||value.Length>60||
            !SignedRatio.TryParse(value,out var ratio)||ratio.Numerator.Sign<0||ratio.Format(30,MidpointRounding.ToEven)!=value)
            throw new MarketDataException("volatility_input","Supply an exact nonnegative annualized series volatility with at most 30 decimal places.");
        Id=id;Value=value;Convention=convention;
    }
    internal SignedRatio Ratio {get{SignedRatio.TryParse(Value,out var ratio);return ratio;}}
}
