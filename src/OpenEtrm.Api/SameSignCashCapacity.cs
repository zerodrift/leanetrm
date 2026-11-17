using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// The selected custody/Transport fixed-fee laws allow same-sign allocations
// bounded by one immutable obligation. This is not a cash netting/refund policy.
internal static class SameSignCashCapacity
{
    internal static bool IsWithin(Money obligation,ImmutableArray<AccountingFlow> history)
    {
        if(history.IsDefault)return false;
        var sign=obligation.Amount.CompareTo(default);
        if(sign==0)return history.All(f=>f is not null&&f.Amount is not null&&f.Amount.Currency==obligation.Currency&&f.Amount.Numerator=="0");
        var direction=ExactDecimal.Parse(sign<0?"-1":"1");
        var limit=ExactMoney.FromMoney(obligation).Scale(direction);var zero=ExactMoney.Parse("0",obligation.Currency);
        if(history.Any(f=>f is null||f.Amount is null||f.Amount.Currency!=obligation.Currency||
            f.Amount.Scale(direction).CompareTo(zero)<0||f.Amount.Scale(direction).CompareTo(limit)>0))return false;
        var heads=new Dictionary<Guid,AccountingFlow>();var total=zero;
        // Every simultaneous correction is applied before checking the boundary;
        // no allocation enumeration order creates an unobservable intermediate total.
        foreach(var boundary in history.GroupBy(f=>f.KnownAt).OrderBy(g=>g.Key))
        {
            foreach(var update in boundary)
            {
                if(heads.TryGetValue(update.AllocationId,out var previous))total=total.Subtract(previous.Amount);
                heads[update.AllocationId]=update;total=total.Add(update.Amount);
            }
            if(total.Scale(direction).CompareTo(limit)>0)return false;
        }
        return true;
    }
}
