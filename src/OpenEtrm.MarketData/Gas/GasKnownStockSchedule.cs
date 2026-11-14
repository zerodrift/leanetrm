using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public enum GasKnownStockUse { Reserve=1,Release,Consume }
public sealed record GasKnownStockOperation(Guid Id,DateTimeOffset At,GasKnownStockUse Use,Guid? ReservationId,Quantity? Quantity)
{
    internal void Require(ReferenceKey unit)
    {
        if(Id==Guid.Empty||!TradingInstant.IsSupported(At)||!Enum.IsDefined(Use)||ReservationId==Guid.Empty||
            (Use==GasKnownStockUse.Release?ReservationId is null||Quantity is not null:
                Quantity is null||Quantity.Specification!=unit||Quantity.Amount.CompareTo(default)<=0)||
            Use==GasKnownStockUse.Reserve&&ReservationId is null)
            throw new MarketDataException("gas_projection_supply","Retain the exact native identity, time, quantity and reservation for each accepted stock movement.");
    }
}
public sealed record GasKnownStockSchedule(ImmutableArray<GasKnownStockOperation> Operations);

public sealed partial record ProjectedGasStockValuation
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public GasKnownStockSchedule? KnownSchedule{get;init;}
    // Replaying the actual resource schedule protects precisely the stock it
    // needs. Summing reservations would count released and reused stock twice.
    public void RequireKnownSupply()
    {
        if(KnownSchedule is not {} schedule)return;
        RequireSchedule(schedule);var future=this with{KnownSchedule=null};future.Validate();
        foreach(var operation in schedule.Operations)future=future.ApplyKnown(operation);
    }
    public ProjectedGasStockValuation AcceptKnown(GasKnownStockOperation operation)
    {
        if(KnownSchedule is not {} schedule||schedule.Operations.IsDefaultOrEmpty||schedule.Operations[0]!=operation)
            throw Error("gas_projection_supply","Apply the exact next accepted stock movement once, preserving its native identity and order.");
        RequireSchedule(schedule);Validate();
        var next=ApplyKnown(operation) with{KnownSchedule=schedule.Operations.Length==1?null:new(schedule.Operations.RemoveAt(0))};
        next.Validate();return next;
    }
    private void RequireSchedule(GasKnownStockSchedule schedule)
    {
        if(schedule.Operations.IsDefaultOrEmpty||schedule.Operations.Any(p=>p is null)||
            schedule.Operations.Select(p=>p.Id).Distinct().Count()!=schedule.Operations.Length||
            schedule.Operations.Zip(schedule.Operations.Skip(1)).Any(p=>p.First.At>p.Second.At))
            throw Error("gas_projection_supply","Retain a complete ordered schedule of exact accepted stock reservations, releases and consumptions.");
        foreach(var operation in schedule.Operations)operation.Require(Quantity.Specification);
    }
    private ProjectedGasStockValuation ApplyKnown(GasKnownStockOperation operation)
    {
        switch(operation.Use)
        {
            case GasKnownStockUse.Consume:return ConsumeQuantity(operation.Quantity!,operation.At,operation.ReservationId);
            case GasKnownStockUse.Reserve:
                if(Reservations.ContainsKey(operation.ReservationId!.Value)||
                    (Quantity.Amount-Reservations.Values.Aggregate(default(ExactDecimal),(a,b)=>a+b)).CompareTo(operation.Quantity!.Amount)<0)
                    throw Error("gas_projection_supply","Hypothetical supply cannot consume stock required by an accepted future reservation.");
                return this with{Reservations=Reservations.Add(operation.ReservationId.Value,operation.Quantity.Amount)};
            case GasKnownStockUse.Release:
                if(!Reservations.ContainsKey(operation.ReservationId!.Value))
                    throw Error("gas_projection_supply","Release only the exact existing accepted stock reservation.");
                return this with{Reservations=Reservations.Remove(operation.ReservationId.Value)};
            default:throw Error("gas_projection_supply","Select an explicitly supported accepted stock movement.");
        }
    }
}
