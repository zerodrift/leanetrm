using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasKnownStockChecks
{
    internal static void Run()
    {
        var f=new Fixture();var at=f.Snapshot().ValuationAt;var unit=f.Quotes[0].Quantity;
        var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),Guid.NewGuid());
        Quantity Q(string value)=>new(ExactDecimal.Parse(value),unit);
        var reservation=Guid.NewGuid();
        var reserve=new GasKnownStockOperation(Guid.NewGuid(),at.AddHours(2),GasKnownStockUse.Reserve,reservation,Q("5"));
        var consume=new GasKnownStockOperation(Guid.NewGuid(),at.AddHours(3),GasKnownStockUse.Consume,reservation,Q("3"));
        var release=new GasKnownStockOperation(Guid.NewGuid(),at.AddHours(4),GasKnownStockUse.Release,reservation,null);
        var later=new GasKnownStockOperation(Guid.NewGuid(),at.AddHours(5),GasKnownStockUse.Consume,null,Q("4"));
        var resource=new ProjectedGasStockValuation(owner,Q("12"),at,ImmutableDictionary<Guid,ExactDecimal>.Empty){
            KnownSchedule=new([reserve,consume,release,later])};
        // Twelve owned, with seven ultimately consumed. The temporary five-unit
        // reservation and three-unit reserved use are not two separate demands.
        var allocated=resource.Consume(Q("5"),at.AddHours(1),null);
        Reject(()=>resource.Consume(Q("6"),at.AddHours(1),null));
        allocated=allocated.AcceptKnown(reserve).AcceptKnown(consume).AcceptKnown(release).AcceptKnown(later);
        if(allocated.Quantity.Amount!=default||!allocated.Reservations.IsEmpty||allocated.KnownSchedule is not null)
            throw new Exception("Accepted movements did not consume exactly their seven committed units.");
        // A repeated sequential reservation can reuse the same owned units.
        var second=reserve with{Id=Guid.NewGuid(),ReservationId=Guid.NewGuid(),At=at.AddHours(6)};
        var repeated=resource with{Quantity=Q("5"),KnownSchedule=new([reserve,release,second])};
        repeated.RequireKnownSupply();
        Reject(()=>repeated.Consume(Q("0.0001"),at,null));
        var active=resource.AcceptKnown(reserve);
        _=active.Consume(Q("2"),at.AddHours(2),reservation);
        Reject(()=>active.Consume(Q("2.0001"),at.AddHours(2),reservation));
        Reject(()=>resource.AcceptKnown(consume));
        Reject(()=>resource.AcceptKnown(reserve with{Quantity=Q("4")}));
        Reject(()=>resource.AcceptKnown(reserve).AcceptKnown(reserve));
        Reject(()=>(resource with{KnownSchedule=new([consume,reserve])}).RequireKnownSupply());
        Reject(()=>(resource with{KnownSchedule=new([reserve,reserve])}).RequireKnownSupply());
        Reject(()=>(resource with{AvailableAt=at.AddHours(4)}).RequireKnownSupply());
        Reject(()=>resource.Consume(Q("1"),at.AddHours(3),null));
        var legacy=resource with{KnownSchedule=null};
        if(legacy.Consume(Q("12"),at,null).Quantity.Amount!=default)
            throw new Exception("An explicit empty accepted schedule changed existing uncommitted stock behavior.");
        Console.WriteLine("PASS accepted stock commitments: chronological reservations, partial uses, releases, direct consumption, exact first-event identity and protected hypothetical supply");
    }
    private static void Reject(Action action)
    {try{action();}catch(MarketDataException e)when(e.Code=="gas_projection_supply"){return;}throw new Exception("Expected committed stock protection.");}
}
