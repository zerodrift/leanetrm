using OpenEtrm.MarketData;

namespace OpenEtrm.Api.Presentation;

public static class PriceUnitLabel
{
    private static readonly ExactDecimal One=ExactDecimal.Parse("1");

    /// <summary>A price unit as "currency / quantity", led by its quote multiplier unless that is one.</summary>
    public static string Format(PriceUnit unit,string currency,string quantity)=>
        unit.Multiplier.CompareTo(One)==0?$"{currency} / {quantity}":$"{unit.Multiplier} {currency} / {quantity}";
}
