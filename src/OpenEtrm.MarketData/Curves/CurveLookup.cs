using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Curves;

public enum CurveLookupMethod{Exact=1,ContainingDelivery,Previous,Next,Linear}
public enum CurveLookupAxis{DeliveryStart=1,DeliveryEnd,ExplicitDate}
public sealed record CurveLookupSettings(CurveLookupMethod Method,CurveLookupAxis Axis,int MaxGapDays,string LeftBoundary,string RightBoundary,string Implementation)
{
    public const string Version="asset_curve_lookup_v1";
    public void Validate()
    {
        if(!Enum.IsDefined(Method)||!Enum.IsDefined(Axis)||MaxGapDays is <1 or >3660||LeftBoundary!="reject"||RightBoundary!="reject"||Implementation!=Version)
            throw new MarketDataException("curve_policy","Select a supported lookup, explicit date axis, 1–3660 day span and rejecting boundaries.");
    }
}
public sealed record CurveLookupMember<T>(T Id,DateOnly From,DateOnly To,DateOnly? SampleDate=null);
public sealed record CurveLookupWeight<T>(T Id,ExactDecimal Weight);

// Select topology, never prices. Both the facade and recipe operator use this
// planner; the connected graph owns the weighted arithmetic and scenarios.
public sealed class CurveLookupGrid<T> where T:notnull
{
    private readonly ImmutableArray<CurveLookupMember<T>> members;
    private readonly (CurveLookupMember<T> Member,DateOnly Date)[] points;
    public CurveLookupSettings Settings{get;}
    public CurveLookupGrid(ImmutableArray<CurveLookupMember<T>> members,CurveLookupSettings settings)
    {
        settings.Validate();Settings=settings;
        if(members.IsDefaultOrEmpty||members.Length>50000||members.Any(m=>m is null||m.From>=m.To)||members.Select(m=>m.Id).Distinct().Count()!=members.Length)
            throw Error("A lookup needs distinct retained members with nonempty delivery periods.");
        this.members=members;
        DateOnly Date(CurveLookupMember<T> m)=>settings.Axis switch
        {CurveLookupAxis.DeliveryStart=>m.From,CurveLookupAxis.DeliveryEnd=>m.To,CurveLookupAxis.ExplicitDate=>m.SampleDate??throw Error("Every point requires its explicitly selected sample date."),_=>throw Error("Unsupported date axis.")};
        points=members.Select(m=>(Member:m,Date:Date(m))).OrderBy(p=>p.Date).ToArray();
        if(settings.Method!=CurveLookupMethod.ContainingDelivery&&points.Select(p=>p.Date).Distinct().Count()!=points.Length)
            throw Error("Point-date lookup requires one unambiguous sample at each date; reduce historical rows explicitly first.");
    }
    public ImmutableArray<CurveDomain> Coverage=>Settings.Method==CurveLookupMethod.ContainingDelivery
        ?[..members.Select(m=>new CurveDomain(m.From,m.To,false))]
        :[..points.Select(p=>new CurveDomain(p.Date,p.Date,true)).Concat(Settings.Method==CurveLookupMethod.Exact?[]:
            points.Zip(points.Skip(1)).Where(p=>p.Second.Date.DayNumber-p.First.Date.DayNumber<=Settings.MaxGapDays)
                .Select(p=>new CurveDomain(p.First.Date,p.Second.Date,true)))];
    public ImmutableArray<CurveLookupWeight<T>> Select(DateOnly date,bool delivery)
    {
        if(delivery)
        {
            if(Settings.Method!=CurveLookupMethod.ContainingDelivery)throw Error("A delivery-date request requires containing-delivery lookup.");
            var matches=members.Where(m=>m.From<=date&&date<m.To).Take(2).ToArray();
            return matches.Length==1?[new(matches[0].Id,ExactDecimal.Parse("1"))]:throw Error("Delivery coverage is missing or ambiguous.");
        }
        if(Settings.Method==CurveLookupMethod.ContainingDelivery)throw Error("Containing-delivery lookup needs a delivery-date request.");
        var low=0;var high=points.Length;
        while(low<high){var middle=low+(high-low)/2;if(points[middle].Date<date)low=middle+1;else high=middle;}
        if(low<points.Length&&points[low].Date==date)return [new(points[low].Member.Id,ExactDecimal.Parse("1"))];
        if(Settings.Method==CurveLookupMethod.Exact||low==0||low==points.Length)throw Error("The query is outside exact or bracketed point coverage; extrapolation is rejected.");
        var left=points[low-1];var right=points[low];
        if(right.Date.DayNumber-left.Date.DayNumber>Settings.MaxGapDays)throw Error("The interpolation span exceeds the selected bound.");
        return Settings.Method switch
        {
            CurveLookupMethod.Previous=>[new(left.Member.Id,ExactDecimal.Parse("1"))],
            CurveLookupMethod.Next=>[new(right.Member.Id,ExactDecimal.Parse("1"))],
            CurveLookupMethod.Linear=>[new(left.Member.Id,Days(right.Date.DayNumber-date.DayNumber)),new(right.Member.Id,Days(date.DayNumber-left.Date.DayNumber))],
            _=>throw Error("Unsupported point lookup.")
        };
    }
    private static ExactDecimal Days(int value)=>ExactDecimal.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static MarketDataException Error(string text)=>new("curve_coverage",text);
}
