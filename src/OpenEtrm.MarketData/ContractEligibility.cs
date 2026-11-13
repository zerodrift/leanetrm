using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData;

public enum ContractOperation { NewTrade=1, LiveCurveOutput }
public enum EligibilityState { Eligible=1, NotYetEligible, EligibilityEnded, Restricted }

// These are resolved, exact-version terms. Source selection owns resolving the
// window and its evidence at the requested business and knowledge coordinates.
public sealed record ContractOperationWindow(ContractKey Contract,ContractOperation Operation,
    DateTimeOffset? From,DateTimeOffset Until,string Implementation)
{
    public const string Version="bounded_contract_operation_v1",MarkingCutoffVersion="contract_marking_cutoff_v1";
}
public sealed record EligibilityContext(DateTimeOffset At,DateTimeOffset KnownAt);
// Id is the stable restriction business key. Native readers resolve corrections
// and withdrawals at KnownAt before supplying one visible interval per key.
public sealed record ContractRestriction(Guid Id,ContractKey Contract,ContractOperation Operation,
    DateTimeOffset From,DateTimeOffset? Until,DateTimeOffset KnownAt,string Reason);
public sealed record EligibilityDecision(ContractOperationWindow Window,EligibilityContext Context,
    EligibilityState State,string Reason,ImmutableArray<Guid> Restrictions)
{
    public bool Eligible=>State==EligibilityState.Eligible;
}

public static class ContractEligibility
{
    public static EligibilityState At(ContractOperationWindow window,DateTimeOffset at)
    {
        if(window is null||window.Implementation is not(ContractOperationWindow.Version or ContractOperationWindow.MarkingCutoffVersion))
            throw new MarketDataException("contract_eligibility_policy","Select a supported explicit contract-operation policy.");
        var bounded=window.Implementation==ContractOperationWindow.Version;
        if(!Valid(window.Contract)||!Enum.IsDefined(window.Operation)||!TradingInstant.IsSupported(window.Until)||!TradingInstant.IsSupported(at)||
            bounded&&(window.From is not {} start||!TradingInstant.IsSupported(start)||start>=window.Until)||
            !bounded&&(window.From is not null||window.Operation!=ContractOperation.LiveCurveOutput))
            throw new MarketDataException("contract_eligibility_context","Supply the exact boundaries required by the selected operation policy.");
        return window.From is {} from&&at<from?EligibilityState.NotYetEligible:at>=window.Until?EligibilityState.EligibilityEnded:EligibilityState.Eligible;
    }
    public static EligibilityDecision Evaluate(ContractOperationWindow window,EligibilityContext context,
        ImmutableArray<ContractRestriction> restrictions)
    {
        if(context is null||!TradingInstant.IsSupported(context.At)||!TradingInstant.IsSupported(context.KnownAt)||restrictions.IsDefault||
            restrictions.Any(r=>r is null||r.Id==Guid.Empty||!Valid(r.Contract)||!Enum.IsDefined(r.Operation)||
                !TradingInstant.IsSupported(r.From)||!TradingInstant.IsSupported(r.KnownAt)||
                r.Until is {} end&&(!TradingInstant.IsSupported(end)||end<=r.From)||string.IsNullOrWhiteSpace(r.Reason))||
            restrictions.Select(r=>r.Id).Distinct().Count()!=restrictions.Length)
            throw new MarketDataException("contract_eligibility_context","Supply exact terms, UTC instants and distinct qualified restriction facts.");
        var state=At(window,context.At);
        if(state==EligibilityState.NotYetEligible)return Decision(state,"eligibility_not_started",[]);
        if(state==EligibilityState.EligibilityEnded)return Decision(state,"eligibility_ended",[]);
        var active=restrictions.Where(r=>r.Contract==window.Contract&&r.Operation==window.Operation&&r.KnownAt<=context.KnownAt&&
            r.From<=context.At&&(r.Until is null||context.At<r.Until)).OrderBy(r=>r.Id).ToImmutableArray();
        if(!active.IsEmpty)return Decision(EligibilityState.Restricted,string.Join(", ",active.Select(r=>r.Reason).Distinct().Order(StringComparer.Ordinal)),[..active.Select(r=>r.Id)]);
        return Decision(EligibilityState.Eligible,"eligible",[]);

        EligibilityDecision Decision(EligibilityState state,string reason,ImmutableArray<Guid> facts)=>new(window,context,state,reason,facts);
    }

    static bool Valid(ContractKey key)=>key.Id!=Guid.Empty&&key.Revision.CatalogId!=Guid.Empty&&key.Revision.RevisionId!=Guid.Empty;
}
