using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record SwapTermChange(string Category,string Field,string? Before,string? After);
public sealed record SwapChangeReview(string Action,int? BookedRevision,ImmutableArray<SwapTermChange> Changes);

public sealed partial class SwapStore
{
    // This reviews saved trade terms, not market-observation corrections. Every change
    // here uses the existing amendment operation, including a saved custom projection.
    public static SwapChangeReview ReviewChanges(SwapWriteRequest booked,SwapWriteRequest proposed)
    {
        static string? Number(string? value)=>value is null?null:ExactDecimal.Parse(value).ToString();
        static object Scheduled(SwapScheduledInput input)
        {
            var normalized=CanonicalScheduled(input);
            static object Side(SwapScheduledSideInput side)=>new {
                side.Basis,side.Quantity,side.Amount,side.Allocation,side.PriceApplication,side.Power,
                periods=side.Periods.Select(period=>new {period.Amount,period.Fixed,floating=CanonicalFloating(period.Floating)})
            };
            return new {normalized.Implementation,normalized.Factor,first=Side(normalized.First),second=Side(normalized.Second)};
        }
        static JsonElement Terms(SwapWriteRequest body)=>JsonSerializer.SerializeToElement(new {
            tradeDetails=new { body.BookId,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision,body.Memo },
            quantity=body.Scheduled is null?Number(body.Quantity):null,direction=Number(body.FirstCoefficient??body.FixedCoefficient),body.Discounting,body.DefinitionRevisionId,body.Commodity,
            fixedSide=body.Fixed is {} fixedSide?fixedSide with { Price=Number(fixedSide.Price) }:null,
            firstFloatingSide=CanonicalFloating(body.FirstFloating),floatingSide=CanonicalFloating(body.Floating),
            scheduled=body.Scheduled is {} scheduled?Scheduled(scheduled):null
        },new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var changes=ImmutableArray.CreateBuilder<SwapTermChange>();
        void Compare(JsonElement before,JsonElement after,string path)
        {
            if(before.ValueKind==JsonValueKind.Object&&after.ValueKind==JsonValueKind.Object)
            {
                foreach(var key in before.EnumerateObject().Select(p=>p.Name).Concat(after.EnumerateObject().Select(p=>p.Name)).Distinct(StringComparer.Ordinal))
                {
                    before.TryGetProperty(key,out var a);after.TryGetProperty(key,out var b);
                    Compare(a,b,path.Length==0?key:path+" / "+key);
                }
                return;
            }
            if(before.ValueKind==JsonValueKind.Array&&after.ValueKind==JsonValueKind.Array)
            {
                for(var n=0;n<Math.Max(before.GetArrayLength(),after.GetArrayLength());n++)
                    Compare(n<before.GetArrayLength()?before[n]:default,n<after.GetArrayLength()?after[n]:default,path+" / "+(n+1));
                return;
            }
            static string? Text(JsonElement value)=>value.ValueKind switch {
                JsonValueKind.Null or JsonValueKind.Undefined=>null,
                JsonValueKind.String=>value.GetString(),
                _=>value.GetRawText()
            };
            var oldValue=Text(before);var newValue=Text(after);
            if(oldValue==newValue)return;
            var category=path.StartsWith("tradeDetails /",StringComparison.Ordinal)?"trade_details":
                path.Contains("customProjection",StringComparison.Ordinal)?"forecast_source":"contract_terms";
            var label=string.Join(" / ",path.Split(" / ").Select(part=>part switch {
                "averaging"=>"Observation convention","customProjection"=>"Projected price source","from"=>"Delivery from","to"=>"Delivery to",
                _=>Regex.Replace(part,"([a-z])([A-Z])","$1 $2")
            }));
            changes.Add(new(category,char.ToUpperInvariant(label[0])+label[1..],oldValue,newValue));
        }
        Compare(Terms(booked),Terms(proposed),"");
        return new(changes.Count==0?"keep_booked_terms":"amend_trade",proposed.ExpectedRevision,changes.ToImmutable());
    }
}
