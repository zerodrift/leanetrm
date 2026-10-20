using OpenEtrm.MarketData;

namespace OpenEtrm.Api.Presentation;

public static class PriceUnitLabel
{
    public static string Format(PriceUnit unit,string currency,string quantity)=>$"{unit.Multiplier} {currency} / {quantity}";
}
