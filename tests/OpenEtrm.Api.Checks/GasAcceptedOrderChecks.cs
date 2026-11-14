using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

internal static class GasAcceptedOrderChecks
{
    internal static void Run()
    {
        var at=DateTimeOffset.Parse("2026-11-14T12:00:00Z");var unit=new ReferenceKey(new(Guid.NewGuid(),Guid.NewGuid()),"synthetic_unit");
        var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),Guid.NewGuid());
        var reservation=Guid.NewGuid();var quantity=new Quantity(ExactDecimal.Parse("5"),unit);
        var consume=new GasKnownQuantityTransition(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),at,at.AddHours(1),[],
            [new(owner,new(Guid.NewGuid(),at,GasKnownStockUse.Consume,reservation,quantity),null)],[],[]);
        var release=new GasKnownQuantityTransition(Guid.Parse("00000000-0000-0000-0000-000000000001"),at,at.AddHours(2),[],
            [new(owner,new(Guid.NewGuid(),at,GasKnownStockUse.Release,reservation,null),null)],[],[]);
        var ordered=GasAcceptedQuantityOrder.Merge([new(release,2),new(consume,1)]);
        var resource=new ProjectedGasStockValuation(owner,quantity,at.AddHours(-1),ImmutableDictionary<Guid,ExactDecimal>.Empty.Add(reservation,quantity.Amount))
            {KnownSchedule=new([..ordered.SelectMany(e=>e.Stock).Select(s=>s.Operation)])};
        foreach(var step in ordered.SelectMany(e=>e.Stock))resource=resource.AcceptKnown(step.Operation);
        if(resource.Quantity.Amount!=default||!resource.Reservations.IsEmpty||resource.KnownSchedule is not null||
            !ordered.Single(e=>e.Id==release.Id).Requires.Contains(consume.Id))
            throw new Exception("A same-instant physical use must precede its later native release, independently of input array order, financial knowledge revisions or identifier sorting.");
        var reserve=new GasKnownQuantityTransition(Guid.NewGuid(),at,at.AddHours(3),[],
            [new(owner,new(Guid.NewGuid(),at,GasKnownStockUse.Reserve,reservation,quantity),null)],[],[]);
        ordered=GasAcceptedQuantityOrder.Merge([new(consume,1),new(reserve,2)]);
        resource=new(owner,quantity,at.AddHours(-1),ImmutableDictionary<Guid,ExactDecimal>.Empty)
            {KnownSchedule=new([..ordered.SelectMany(e=>e.Stock).Select(s=>s.Operation)])};
        foreach(var step in ordered.SelectMany(e=>e.Stock))resource=resource.AcceptKnown(step.Operation);
        if(resource.Quantity.Amount!=default||ordered[0].Id!=reserve.Id||!ordered[1].Requires.Contains(reserve.Id))
            throw new Exception("The exact reservation foreign key precedes consumption even when a later native correction created that reservation.");
        var definition=new DefinitionRevision(unit.Revision.CatalogId,Guid.NewGuid());
        var mark=new CurveRequest(new(definition,"synthetic_gas"),new(definition,Guid.NewGuid()));
        var instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,unit,new(unit.Revision,"synthetic_currency"),null,GasHoldingCarrying.Nominal);
        var account=new GasProjectionAccount(Guid.NewGuid(),owner.BookId,new(unit.Revision.CatalogId,Guid.NewGuid(),1),new(unit.Revision,"synthetic_location"));
        var stock=new GasProjectionHolding(owner,account,GasProjectionHoldingKind.OwnedStock,instrument,quantity,at,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        var receipt=new GasKnownQuantityTransition(Guid.NewGuid(),at,at.AddHours(3),[],[],[],[new(owner with{BindingId=Guid.NewGuid()},stock)]);
        var unreserved=consume with{Stock=[consume.Stock.Single() with{Operation=consume.Stock.Single().Operation with{ReservationId=null}}]};
        ordered=GasAcceptedQuantityOrder.Merge([new(unreserved,1),new(receipt,3)]);
        if(ordered[0].Id!=receipt.Id||!ordered[1].Requires.Contains(receipt.Id))throw new Exception("Accepted title must precede its exact same-instant use.");
        resource=new(owner,quantity,at,ImmutableDictionary<Guid,ExactDecimal>.Empty)
            {KnownSchedule=new([..ordered.SelectMany(e=>e.Stock).Select(s=>s.Operation)])};
        resource=resource.AcceptKnown(unreserved.Stock.Single().Operation);
        if(resource.Quantity.Amount!=default)throw new Exception("Receipt and consumption changed the accepted stock amount.");
        Reject([new(consume,0)]);
        Reject([new(consume,1),new(consume,2)]);
        Reject([new(consume with{Requires=[Guid.NewGuid()]},1)]);
        Reject([new(consume with{Requires=[reserve.Id,reserve.Id]},1),new(reserve,2)]);
        Reject([new(consume with{Id=Guid.Empty},1)]);
        Reject([new(consume,1),new(release,1)]);
        Reject([new(consume with{Requires=[release.Id]},1),new(release with{Requires=[consume.Id]},2)]);
        Reject([new(unreserved,1),new(receipt with{At=at.AddTicks(10)},3)]);
        Console.WriteLine("PASS accepted quantity merge: native resource order and explicit same-instant prerequisites preserve the complete committed stock schedule");
    }
    private static void Reject(ImmutableArray<GasAcceptedQuantityEvent> events)
    {try{_=GasAcceptedQuantityOrder.Merge(events);}catch(MarketDataException e)when(e.Code=="gas_projection_source"){return;}throw new Exception("Ambiguous, missing, backwards or cyclic native event authority must refuse before financial projection.");}
}
