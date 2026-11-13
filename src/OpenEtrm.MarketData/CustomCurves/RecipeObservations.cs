using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.CustomCurves;

// Contract observation terms select days only. A kept day resolves every stated position at that day's
// sampling instant; a day that cannot refuses its whole window. No price is read or averaged here.
public static partial class RecipeEngine
{
    public const string ContractObservations="contract_observations";
    public const string ObservationTermsVersion="contract_observation_terms_v1";
    public const string CalendarMonthPeriod="calendar_month_of_delivery_v1";
    public const string TradeMonthPeriod="trade_month_of_delivery_contract_v1";
    public const string CalendarDays="calendar_days_v1";
    public const string BusinessDays="business_days_v1";
    public static ImmutableArray<RecipeDelivery> ObservationTargets(RecipeNode node)=>ObservationSettings(node).Targets;
    public static ImmutableArray<RecipeObservationWindow> ObservationWindows(RecipeNode node,RecipeSource source,DateSet? businessDays,TimeOnly samplingTime)
    {
        var (targets,days,terms)=ObservationSettings(node);
        if((days==BusinessDays)!=(businessDays is not null))throw Invalid("Supply a business-day calendar exactly when the node observes business days.");
        if(source is null||source.Contracts.IsDefaultOrEmpty)throw Missing("Observation windows require a retained contract universe.");
        var contracts=source.Contracts;
        RecipeContract Resolve(DateOnly day,int position)=>
            ResolveContracts(contracts,"positions",position,1,new(day.ToDateTime(samplingTime),TimeSpan.Zero),day,requireLive:false)[0];
        var windows=ImmutableArray.CreateBuilder<RecipeObservationWindow>();
        foreach(var target in targets)
        foreach(var term in terms)
        {
            DateOnly first=target.From,last=target.To.AddDays(-1);Func<DateOnly,bool> member=_=>true;
            if(term.Period==TradeMonthPeriod)
            {
                var index=contracts.Select((c,i)=>(c,i)).Where(p=>p.c.Contract.DeliveryFrom==target.From&&p.c.Contract.DeliveryTo==target.To).Select(p=>p.i).ToArray();
                if(index.Length!=1)throw Missing($"The source has no single delivery contract for {target.From:yyyy-MM}.");
                if(index[0]==0)throw Missing($"The delivery contract for {target.From:yyyy-MM} has no prior contract to start its trade month.");
                var delivery=contracts[index[0]].Contract.Key;
                first=DateOnly.FromDateTime(contracts[index[0]-1].Contract.Expiry.UtcDateTime);last=DateOnly.FromDateTime(contracts[index[0]].Contract.Expiry.UtcDateTime);
                member=day=>Resolve(day,1).Contract.Key==delivery;
            }
            if(businessDays is not null&&(first<businessDays.From||last>=businessDays.To))throw Missing("The business-day calendar does not cover the observation window.");
            var kept=ImmutableArray.CreateBuilder<DateOnly>();
            for(var day=first;day<=last;day=day.AddDays(1))
            {
                if(businessDays?.Contains(day)==false||!member(day))continue;
                _=Resolve(day,term.Position);if(term.LessPosition is {} less)_=Resolve(day,less);
                kept.Add(day);
            }
            if(kept.Count==0)throw Missing($"Term {term.Ordinal} has no observation day for {target.From:yyyy-MM}.");
            windows.Add(new(target,term.Ordinal,kept.ToImmutable()));
        }
        return windows.ToImmutable();
    }
    private static (ImmutableArray<RecipeDelivery> Targets,string Days,ImmutableArray<RecipeObservationTerm> Terms) ObservationSettings(RecipeNode node)
    {
        if(node?.Kind!=ContractObservations)throw Invalid("Choose a contract observation node.");
        if(Required(node,"implementation")!=ObservationTermsVersion||Required(node,"roll")!=ContractRollRule.ExpiryImplementation||
            Required(node,"sampling")!="context_utc_time"||Required(node,"corrections")!="exact_context_frame_history_latest_visible")
            throw Invalid("Choose the contract observation version, the expiry roll and the exact sampling and correction policies.");
        var days=Required(node,"days");if(days is not (CalendarDays or BusinessDays))throw Invalid("Choose calendar or business observation days explicitly.");
        if(!DateOnly.TryParseExact(Required(node,"targetFrom"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)||from.Day!=1||from.Year is <1901 or >2199)
            throw Invalid("Start the target months on the first day of an explicit month.");
        var count=Number(node,"targetCount",1,120);var terms=node.ObservationTerms;
        if(terms.IsDefaultOrEmpty||terms.Length>16||terms.Any(t=>t is null)||!terms.Select(t=>t.Ordinal).Order().SequenceEqual(Enumerable.Range(0,terms.Length)))
            throw Invalid("State one to 16 observation terms with contiguous ordinals from 0.");
        foreach(var term in terms)
        {
            if(!Identity(term.Group)||term.Position is <1 or >120||term.LessPosition is <1 or >120||term.LessPosition==term.Position||term.Period is not (CalendarMonthPeriod or TradeMonthPeriod))
                throw Invalid("Each observation term requires a group, distinct positions from 1 to 120 and a supported period.");
            ExactDecimal weight;
            try{weight=Weight(term.Weight);}catch(MarketDataException error)when(error.Code=="numeric_format"){throw Invalid("Every weight must be explicit exact decimal text.");}
            if(weight.CompareTo(default)<=0)throw Invalid("Every observation term weight must be positive.");
        }
        return([..Enumerable.Range(0,count).Select(i=>new RecipeDelivery(from.AddMonths(i),from.AddMonths(i+1)))],days,[..terms.OrderBy(t=>t.Ordinal)]);
    }
}
