using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Physical;

// Physical operating rules have no title, entitlement, charge or financial mark.
// The native reader supplies exact versions under the selected temporal context.
public sealed record StorageFacilityRules(Guid VersionId,string Implementation,ReferenceKey Quantity,
    ImmutableArray<Guid> Ports,Quantity Minimum,Quantity Maximum,ImmutableArray<StorageRatchetBand> Ratchets,
    string InventoryBasis,string RateBasis,string SimultaneousFlow,string LossTreatment)
{
    public const string Version="storage_total_physical_step_ratchets_v1";
    public const string TotalPhysical="total_physical_inventory_v1";
    public const string ElapsedHour="native_quantity_per_elapsed_hour_v1";
    public const string SeparateDirections="separate_injection_withdrawal_v1";
    public const string NoLoss="no_loss_v1";
}

public sealed record StorageRatchetBand(Quantity Lower,Quantity Upper,Quantity InjectionPerHour,Quantity WithdrawalPerHour);
public sealed record StorageFacilityOpening(Guid RevisionId,DateTimeOffset At,Quantity Level);
public sealed record StorageFacilityMovement(Guid RevisionId,Guid PortId,DateTimeOffset From,DateTimeOffset To,string Direction,Quantity Quantity);
public sealed record StorageFacilityCoverage(Guid RevisionId,DateTimeOffset From,DateTimeOffset Through,string Track,string Statement)
{
    public const string ChosenPlan="chosen_plan_idle_between_activities_v1";
    public const string CompleteActual="complete_observed_window_v1";
    public const string IncompleteActual="incomplete_observed_window_v1";
}
public sealed record StorageFacilityIntervalAssessment(DateTimeOffset From,DateTimeOffset To,ImmutableArray<Guid> Activities,
    ExactQuantity Opening,ExactQuantity Closing,ExactQuantity InjectionPerHour,ExactQuantity WithdrawalPerHour,
    ImmutableArray<int> Bands,ImmutableArray<string> Violations);

public sealed record StorageFacilityAssessment(Guid RulesVersion,Guid? OpeningRevision,Guid CoverageRevision,DateTimeOffset From,DateTimeOffset Through,
    FacilityConstraintState Constraint,ExactQuantity? ClosingLevel,ImmutableArray<StorageFacilityIntervalAssessment> Intervals,
    ImmutableArray<string> Violations)
{
    public StorageFacilityAssessment RequireFeasible()
    {
        if(Constraint==FacilityConstraintState.Unassessed)
            throw new MarketDataException("storage_facility_opening_required","Select dated opening evidence and an explicit complete activity window before admitting an operating plan.");
        if(Constraint!=FacilityConstraintState.WithinLimit)
            throw new MarketDataException("storage_facility_infeasible","The operating plan breaches the selected physical inventory, gross-flow or ratchet rules.");
        return this;
    }
}

// Assess a chosen physical plan or observed evidence without changing either.
// Event boundaries retain exact instants; inventory and rates remain rational.
// Ratchet intersections are tested directly, without rounded crossing times.
public static class StorageFacilityOperations
{
    public const int MaximumActivities=1024,MaximumRatchets=128,MaximumPorts=64;

    public static StorageFacilityAssessment Assess(StorageFacilityRules rules,StorageFacilityOpening? opening,
        DateTimeOffset from,DateTimeOffset through,ImmutableArray<StorageFacilityMovement> activities,StorageFacilityCoverage coverage)
    {
        Require(rules,opening,from,through,activities,coverage);
        var violations=ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var minimum=Value(rules.Minimum);var maximum=Value(rules.Maximum);
        if(opening is not null)Bounds(Value(opening.Level),minimum,maximum,violations);
        if(opening is null||coverage.Statement==StorageFacilityCoverage.IncompleteActual)
            return new(rules.VersionId,opening?.RevisionId,coverage.RevisionId,from,through,FacilityConstraintState.Unassessed,null,[],[..violations.Order(StringComparer.Ordinal)]);
        var level=Value(opening.Level);
        var intervals=ImmutableArray.CreateBuilder<StorageFacilityIntervalAssessment>();
        var times=activities.SelectMany(a=>new[]{a.From,a.To}).Append(from).Append(through).Distinct().Order().ToArray();
        for(var i=1;i<times.Length;i++)
        {
            var start=times[i-1];var end=times[i];
            var active=activities.Where(a=>a.From<=start&&a.To>=end).OrderBy(a=>a.RevisionId).ToImmutableArray();
            var injected=SignedRatio.Zero;var withdrawn=SignedRatio.Zero;
            foreach(var activity in active)
            {
                var rate=Value(activity.Quantity)*new SignedRatio(TimeSpan.TicksPerHour,(activity.To-activity.From).Ticks);
                if(activity.Direction=="inject")injected+=rate;else withdrawn+=rate;
            }
            var next=level+(injected-withdrawn)*new SignedRatio((end-start).Ticks,TimeSpan.TicksPerHour);
            var failed=ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            Bounds(level,minimum,maximum,failed);Bounds(next,minimum,maximum,failed);
            if(injected.Numerator.Sign>0&&withdrawn.Numerator.Sign>0)failed.Add("simultaneous_flow");
            var bands=CrossedBands(rules.Ratchets,level,next,maximum);
            foreach(var index in bands)
            {
                var band=rules.Ratchets[index];
                if(injected.CompareTo(Value(band.InjectionPerHour))>0)failed.Add("injection_rate");
                if(withdrawn.CompareTo(Value(band.WithdrawalPerHour))>0)failed.Add("withdrawal_rate");
            }
            violations.UnionWith(failed);
            intervals.Add(new(start,end,[..active.Select(a=>a.RevisionId)],Exact(level,rules.Quantity),Exact(next,rules.Quantity),
                Exact(injected,rules.Quantity),Exact(withdrawn,rules.Quantity),bands,[..failed.Order(StringComparer.Ordinal)]));
            level=next;
        }
        return new(rules.VersionId,opening.RevisionId,coverage.RevisionId,from,through,
            violations.Count==0?FacilityConstraintState.WithinLimit:FacilityConstraintState.Breached,Exact(level,rules.Quantity),
            intervals.ToImmutable(),[..violations.Order(StringComparer.Ordinal)]);
    }

    private static ImmutableArray<int> CrossedBands(ImmutableArray<StorageRatchetBand> bands,SignedRatio opening,SignedRatio closing,SignedRatio maximum)
    {
        var low=opening.CompareTo(closing)<=0?opening:closing;var high=opening.CompareTo(closing)<=0?closing:opening;
        var crossed=ImmutableArray.CreateBuilder<int>();
        for(var i=0;i<bands.Length;i++)
        {
            var lower=Value(bands[i].Lower);var upper=Value(bands[i].Upper);
            // A traversed band has positive inventory extent. Merely arriving at
            // a boundary does not spend time flowing in the next band.
            if(low!=high?low.CompareTo(upper)<0&&high.CompareTo(lower)>0:
                low.CompareTo(lower)>=0&&(low.CompareTo(upper)<0||low==maximum&&i==bands.Length-1))crossed.Add(i);
        }
        return crossed.ToImmutable();
    }

    private static void Bounds(SignedRatio level,SignedRatio minimum,SignedRatio maximum,ImmutableHashSet<string>.Builder violations)
    {
        if(level.CompareTo(minimum)<0)violations.Add("inventory_minimum");
        if(level.CompareTo(maximum)>0)violations.Add("inventory_maximum");
    }

    private static SignedRatio Value(Quantity value)=>new(value.Amount.ScaledValue,ExactDecimal.WorkingScale);
    private static ExactQuantity Exact(SignedRatio value,ReferenceKey unit)=>new(value.Numerator,value.Denominator,unit);

    public static void ValidateRules(StorageFacilityRules rules)
    {
        if(rules is null||rules.VersionId==Guid.Empty||rules.Implementation!=StorageFacilityRules.Version||
            rules.InventoryBasis!=StorageFacilityRules.TotalPhysical||rules.RateBasis!=StorageFacilityRules.ElapsedHour||
            rules.SimultaneousFlow!=StorageFacilityRules.SeparateDirections||rules.LossTreatment!=StorageFacilityRules.NoLoss||
            rules.Ports.IsDefaultOrEmpty||rules.Ports.Length>MaximumPorts||rules.Ports.Any(p=>p==Guid.Empty)||rules.Ports.Distinct().Count()!=rules.Ports.Length||
            rules.Ratchets.IsDefaultOrEmpty||rules.Ratchets.Length>MaximumRatchets)throw Invalid();
        Money.RequireKey(rules.Quantity);
        bool Same(Quantity? q)=>q is not null&&q.Specification==rules.Quantity;
        if(!Same(rules.Minimum)||!Same(rules.Maximum)||rules.Minimum.Amount.CompareTo(default)<0||
            rules.Maximum.Amount.CompareTo(rules.Minimum.Amount)<=0)throw Invalid();
        var preceding=rules.Minimum.Amount;
        foreach(var band in rules.Ratchets)
        {
            if(band is null||!Same(band.Lower)||!Same(band.Upper)||!Same(band.InjectionPerHour)||!Same(band.WithdrawalPerHour)||
                band.Lower.Amount!=preceding||band.Upper.Amount.CompareTo(band.Lower.Amount)<=0||band.Upper.Amount.CompareTo(rules.Maximum.Amount)>0||
                band.InjectionPerHour.Amount.CompareTo(default)<0||band.WithdrawalPerHour.Amount.CompareTo(default)<0)throw Invalid();
            preceding=band.Upper.Amount;
        }
        if(preceding!=rules.Maximum.Amount)throw Invalid();
    }

    private static void Require(StorageFacilityRules rules,StorageFacilityOpening? opening,DateTimeOffset from,
        DateTimeOffset through,ImmutableArray<StorageFacilityMovement> activities,StorageFacilityCoverage coverage)
    {
        ValidateRules(rules);
        if(coverage is null||coverage.RevisionId==Guid.Empty||coverage.From!=from||coverage.Through!=through||
            !(coverage.Track=="planned"&&coverage.Statement==StorageFacilityCoverage.ChosenPlan||
                coverage.Track=="measured"&&coverage.Statement is StorageFacilityCoverage.CompleteActual or StorageFacilityCoverage.IncompleteActual)||
            !TradingInstant.IsSupported(from)||!TradingInstant.IsSupported(through)||through<=from||
            activities.IsDefault||activities.Length>MaximumActivities||activities.Any(a=>a is null)||
            activities.Select(a=>a.RevisionId).Distinct().Count()!=activities.Length)throw Invalid();
        bool Same(Quantity? q)=>q is not null&&q.Specification==rules.Quantity;
        if(opening is not null&&(opening.RevisionId==Guid.Empty||opening.At!=from||!Same(opening.Level)))throw Invalid();
        foreach(var a in activities)
            if(a.RevisionId==Guid.Empty||!rules.Ports.Contains(a.PortId)||!TradingInstant.IsSupported(a.From)||!TradingInstant.IsSupported(a.To)||
                a.From<from||a.To>through||a.To<=a.From||a.Direction is not("inject" or "withdraw")||!Same(a.Quantity)||a.Quantity.Amount.CompareTo(default)<0)throw Invalid();
    }

    private static MarketDataException Invalid()=>new("storage_facility_input",
        "Select exact storage rules, ports and native units, a covered ratchet grid, explicit physical policies, dated opening evidence and distinct interval movements.");
}
