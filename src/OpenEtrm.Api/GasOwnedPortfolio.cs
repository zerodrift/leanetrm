using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedValuationPolicy(string Implementation,string Scope,string CashDiscounting,string TimingPolicy)
{
    public const string Version="gas_owned_mark_to_market_v1",WholeBooks="all_independent_owners_in_selected_books_v1",
        CurrencyDefault="captured_currency_default_v1";
    public void Validate()
    {
        if(Implementation!=Version||Scope!=WholeBooks||CashDiscounting!=CurrencyDefault||TimingPolicy!=RateDiscountTiming.Version)
            throw new MarketDataException("gas_owned_policy","Select whole-book independent gas ownership, captured currency discount mapping and exact-instant timing explicitly.");
    }
}
