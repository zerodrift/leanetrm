namespace OpenEtrm.MarketData.Gas;

public sealed record GasSourceAllocationChange(Quantity PreviouslyUnallocated,Quantity Unallocated);

// Source capacity is a bound on explicit native allocations. Unallocated
// capacity is evidence; it is never titled inventory or an asset valuation.
public static class GasSourceAllocation
{
    public const string Initial="initial_native_source_enrollment_v1";
    public const string Preserve="preserve_unallocated_source_quantity_v1";
    public const string Withdraw="withdraw_all_source_allocations_v1";
    public static GasSourceAllocationChange Plan(Quantity oldCapacity,Quantity oldAllocated,Quantity capacity,Quantity allocated,string policy)
    {
        if(!Valid(oldCapacity)||!Valid(oldAllocated)||!Valid(capacity)||!Valid(allocated)||oldAllocated.Specification!=oldCapacity.Specification||
            capacity.Specification!=oldCapacity.Specification||allocated.Specification!=oldCapacity.Specification||
            oldAllocated.Amount.CompareTo(oldCapacity.Amount)>0||allocated.Amount.CompareTo(capacity.Amount)>0||policy is not(Preserve or Withdraw))throw Error();
        var prior=oldCapacity.Amount-oldAllocated.Amount;var unused=capacity.Amount-allocated.Amount;
        if(policy==Preserve&&unused!=prior||policy==Withdraw&&(capacity.Amount!=default||allocated.Amount!=default))throw Error();
        return new(new(prior,capacity.Specification),new(unused,capacity.Specification));
    }
    public static Quantity Enroll(Quantity capacity,Quantity allocated)
    {
        if(!Valid(capacity)||!Valid(allocated)||allocated.Amount==default||capacity.Specification!=allocated.Specification||allocated.Amount.CompareTo(capacity.Amount)>0)throw Error();
        return new(capacity.Amount-allocated.Amount,capacity.Specification);
    }
    private static bool Valid(Quantity? quantity)=>quantity is not null&&quantity.Amount.CompareTo(default)>=0;
    private static MarketDataException Error()=>new("gas_source_allocation","Select exact bounded source allocations and an explicit qualified preservation or withdrawal policy.");
}
