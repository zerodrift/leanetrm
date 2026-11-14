namespace OpenEtrm.MarketData.Trading;

// Execution policy is separate from product economics, financial evidence and
// personal display preferences. Every invocation selects one exact saved version.
public enum LifecycleExecutionMode { Manual=1,Review,Automatic }
public sealed record LifecycleActionPolicy(Guid Id,int Revision,LifecycleExecutionMode Mode,
    DateTimeOffset From,DateTimeOffset Until,string Implementation)
{
    public const string Version="explicit_lifecycle_action_policy_v1";
    public void Require(DateTimeOffset at,bool automatic,bool reviewed)
    {
        if(Id==Guid.Empty||Revision<1||Implementation!=Version||!Enum.IsDefined(Mode)||
            !TradingInstant.IsSupported(From)||!TradingInstant.IsSupported(Until)||From>=Until)
            throw Error("lifecycle_action_policy","Select a complete exact saved action policy.");
        if(!TradingInstant.IsSupported(at)||at<From||at>=Until)
            throw Error("lifecycle_action_window","The action lies outside its explicitly configured half-open window.");
        if(automatic&&Mode!=LifecycleExecutionMode.Automatic)
            throw Error("lifecycle_automation_policy","This saved action policy does not authorize automatic execution.");
        if(!reviewed&&!automatic)
            throw Error("lifecycle_review_required","Review the exact financial proposal before executing it.");
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}

public static class LifecycleActionCatalog
{
    public static void RequireAutomatic(string action)
    {
        if(action is not ("option.expiry" or "swap.determine" or "futures.margin"))
            throw new MarketDataException("lifecycle_automation_unsupported","This product action requires explicit operator evidence or instruction.");
    }
}
