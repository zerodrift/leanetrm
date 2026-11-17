using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasTransportCapacitySlice(DateTimeOffset From,DateTimeOffset Through,Quantity MaximumPerHour);
public sealed record GasTransportCapacityPath(Guid RouteVersionId,ReferenceKey ReceiptPoint,ReferenceKey DeliveryPoint);
public sealed record GasTransportPointCapacity(ReferenceKey Point,ImmutableArray<GasTransportCapacitySlice> Limits);
public sealed record GasTransportRouteCapacity(Guid RouteVersionId,ImmutableArray<GasTransportCapacitySlice> Limits);
public sealed record GasTransportCapacityProfile(Guid VersionId,string Implementation,ReferenceKey Quantity,DateTimeOffset From,DateTimeOffset Through,
    string RateBasis,string FlowShape,string AgreementBasis,string DeliveryBasis,ImmutableArray<GasTransportCapacityPath> Paths,
    ImmutableArray<GasTransportCapacitySlice> Agreement,ImmutableArray<GasTransportPointCapacity> Receipts,
    ImmutableArray<GasTransportPointCapacity> Deliveries,ImmutableArray<GasTransportRouteCapacity> Routes)
{
    public const string Version="gas_transport_gross_uniform_capacity_v1";
    public const string ElapsedHour="native_quantity_per_elapsed_hour_v1";
    public const string UniformStages="uniform_each_explicit_receipt_delivery_interval_v1";
    public const string GrossReceipts="agreement_receipt_route_gross_receipts_v1";
    public const string GrossDeliveries="delivery_point_gross_deliveries_v1";
}

// Receipt and delivery clocks are independent explicit inputs. Capacity does
// not supply route conservation, fuel, transit, title or financial valuation.
public sealed record GasTransportCapacityUse(Guid Id,Guid RouteVersionId,ReferenceKey ReceiptPoint,ReferenceKey DeliveryPoint,
    DateTimeOffset ReceiptFrom,DateTimeOffset ReceiptTo,Quantity ReceiptQuantity,
    DateTimeOffset DeliveryFrom,DateTimeOffset DeliveryTo,Quantity DeliveryQuantity);
public sealed record GasTransportCapacityInterval(string Axis,ReferenceKey? Point,Guid? RouteVersionId,
    DateTimeOffset From,DateTimeOffset Through,ImmutableArray<Guid> Uses,ExactQuantity UsedPerHour,ExactQuantity MaximumPerHour,bool WithinLimit);
public sealed record GasTransportCapacityAssessment(Guid VersionId,bool Feasible,ImmutableArray<GasTransportCapacityInterval> Intervals)
{
    public GasTransportCapacityAssessment RequireFeasible()=>Feasible?this:
        throw new MarketDataException("gas_transport_capacity_exceeded","The selected gross receipt or delivery intervals exceed their saved agreement, point or route capacity.");
}

public static class GasTransportCapacity
{
    public const int MaximumPaths=128,MaximumUses=1024,MaximumSlicesPerScope=256;
    sealed record Stage(Guid Id,DateTimeOffset From,DateTimeOffset Through,Quantity Quantity);
    sealed record Scope(string Axis,ReferenceKey? Point,Guid? Route,ImmutableArray<GasTransportCapacitySlice> Limits,ImmutableArray<Stage> Stages);

    // Assess all declared claims under one admitted complete entitlement scope.
    // Raw observed breaches remain in the result; prospective admission is an
    // explicit caller choice through RequireFeasible, never quantity clipping.
    public static GasTransportCapacityAssessment Assess(GasTransportCapacityProfile profile,ImmutableArray<GasTransportCapacityUse> uses)
    {
        Validate(profile,uses);
        Stage Receipt(GasTransportCapacityUse use)=>new(use.Id,use.ReceiptFrom,use.ReceiptTo,use.ReceiptQuantity);
        Stage Delivery(GasTransportCapacityUse use)=>new(use.Id,use.DeliveryFrom,use.DeliveryTo,use.DeliveryQuantity);
        var scopes=new List<Scope>{new("agreement",null,null,profile.Agreement,[..uses.Select(Receipt)])};
        scopes.AddRange(profile.Receipts.Select(p=>new Scope("receipt",p.Point,null,p.Limits,[..uses.Where(u=>u.ReceiptPoint==p.Point).Select(Receipt)])));
        scopes.AddRange(profile.Deliveries.Select(p=>new Scope("delivery",p.Point,null,p.Limits,[..uses.Where(u=>u.DeliveryPoint==p.Point).Select(Delivery)])));
        scopes.AddRange(profile.Routes.Select(p=>new Scope("route",null,p.RouteVersionId,p.Limits,[..uses.Where(u=>u.RouteVersionId==p.RouteVersionId).Select(Receipt)])));
        var result=ImmutableArray.CreateBuilder<GasTransportCapacityInterval>();
        foreach(var scope in scopes)
        {
            var limits=scope.Limits.OrderBy(l=>l.From).ToArray();var current=0;
            var boundaries=limits.SelectMany(l=>new[]{l.From,l.Through}).Concat(scope.Stages.SelectMany(s=>new[]{s.From,s.Through})).Distinct().Order().ToArray();
            for(var i=1;i<boundaries.Length;i++)
            {
                var from=boundaries[i-1];var through=boundaries[i];
                while(limits[current].Through<=from)current++;
                var active=scope.Stages.Where(s=>s.From<=from&&s.Through>=through).OrderBy(s=>s.Id).ToArray();
                var rate=SignedRatio.Sum(active.Select(s=>Value(s.Quantity)*new SignedRatio(TimeSpan.TicksPerHour,(s.Through-s.From).Ticks)));
                var maximum=Value(limits[current].MaximumPerHour);
                result.Add(new(scope.Axis,scope.Point,scope.Route,from,through,[..active.Select(s=>s.Id)],Exact(rate,profile.Quantity),Exact(maximum,profile.Quantity),rate.CompareTo(maximum)<=0));
            }
        }
        return new(profile.VersionId,result.All(i=>i.WithinLimit),result.ToImmutable());
    }

    static SignedRatio Value(Quantity quantity)=>new(quantity.Amount.ScaledValue,ExactDecimal.WorkingScale);
    static ExactQuantity Exact(SignedRatio value,ReferenceKey specification)=>new(value.Numerator,value.Denominator,specification);
    static MarketDataException Invalid()=>new("gas_transport_capacity_input","Supply the installed capacity law, exact eligible paths, complete nonoverlapping dated limits and explicit native receipt and delivery intervals.");

    static void Validate(GasTransportCapacityProfile profile,ImmutableArray<GasTransportCapacityUse> uses)
    {
        if(profile is null||profile.VersionId==Guid.Empty||profile.Implementation!=GasTransportCapacityProfile.Version||
            profile.RateBasis!=GasTransportCapacityProfile.ElapsedHour||profile.FlowShape!=GasTransportCapacityProfile.UniformStages||
            profile.AgreementBasis!=GasTransportCapacityProfile.GrossReceipts||profile.DeliveryBasis!=GasTransportCapacityProfile.GrossDeliveries||
            !TradingInstant.IsSupported(profile.From)||!TradingInstant.IsSupported(profile.Through)||profile.From>=profile.Through||
            profile.Paths.IsDefaultOrEmpty||profile.Paths.Length>MaximumPaths||profile.Paths.Any(p=>p is null)||
            profile.Paths.Select(p=>p.RouteVersionId).Distinct().Count()!=profile.Paths.Length||
            profile.Receipts.IsDefaultOrEmpty||profile.Deliveries.IsDefaultOrEmpty||profile.Routes.IsDefaultOrEmpty||
            profile.Receipts.Length>MaximumPaths||profile.Deliveries.Length>MaximumPaths||profile.Routes.Length>MaximumPaths||
            profile.Receipts.Any(p=>p is null)||profile.Deliveries.Any(p=>p is null)||profile.Routes.Any(p=>p is null)||
            profile.Receipts.Select(p=>p.Point).Distinct().Count()!=profile.Receipts.Length||profile.Deliveries.Select(p=>p.Point).Distinct().Count()!=profile.Deliveries.Length||
            profile.Routes.Select(p=>p.RouteVersionId).Distinct().Count()!=profile.Routes.Length||
            uses.IsDefault||uses.Length>MaximumUses||uses.Any(u=>u is null)||uses.Select(u=>u.Id).Distinct().Count()!=uses.Length)throw Invalid();
        void Key(ReferenceKey key)
        {
            try{Money.RequireKey(key);}catch(MarketDataException){throw Invalid();}
            if(key.Revision!=profile.Quantity.Revision)throw Invalid();
        }
        Key(profile.Quantity);
        foreach(var path in profile.Paths)
        {
            if(path.RouteVersionId==Guid.Empty)throw Invalid();
            Key(path.ReceiptPoint);Key(path.DeliveryPoint);
        }
        if(!profile.Paths.Select(p=>p.ReceiptPoint).ToHashSet().SetEquals(profile.Receipts.Select(p=>p.Point))||
            !profile.Paths.Select(p=>p.DeliveryPoint).ToHashSet().SetEquals(profile.Deliveries.Select(p=>p.Point))||
            !profile.Paths.Select(p=>p.RouteVersionId).ToHashSet().SetEquals(profile.Routes.Select(p=>p.RouteVersionId)))throw Invalid();
        bool Quantity(Quantity? value)=>value is not null&&value.Specification==profile.Quantity&&value.Amount.CompareTo(default)>=0;
        void Schedule(ImmutableArray<GasTransportCapacitySlice> limits)
        {
            if(limits.IsDefaultOrEmpty||limits.Length>MaximumSlicesPerScope||limits.Any(l=>l is null))throw Invalid();
            var previous=profile.From;
            foreach(var limit in limits.OrderBy(l=>l.From))
            {
                if(limit.From!=previous||!TradingInstant.IsSupported(limit.From)||!TradingInstant.IsSupported(limit.Through)||
                    limit.Through<=limit.From||limit.Through>profile.Through||!Quantity(limit.MaximumPerHour))throw Invalid();
                previous=limit.Through;
            }
            if(previous!=profile.Through)throw Invalid();
        }
        Schedule(profile.Agreement);
        foreach(var limits in profile.Receipts.Select(p=>p.Limits).Concat(profile.Deliveries.Select(p=>p.Limits)).Concat(profile.Routes.Select(p=>p.Limits)))Schedule(limits);
        bool Window(DateTimeOffset from,DateTimeOffset through)=>TradingInstant.IsSupported(from)&&TradingInstant.IsSupported(through)&&from>=profile.From&&through<=profile.Through&&from<through;
        foreach(var use in uses)
            if(use.Id==Guid.Empty||!profile.Paths.Any(p=>p.RouteVersionId==use.RouteVersionId&&p.ReceiptPoint==use.ReceiptPoint&&p.DeliveryPoint==use.DeliveryPoint)||
                !Window(use.ReceiptFrom,use.ReceiptTo)||!Window(use.DeliveryFrom,use.DeliveryTo)||!Quantity(use.ReceiptQuantity)||!Quantity(use.DeliveryQuantity))throw Invalid();
    }
}
