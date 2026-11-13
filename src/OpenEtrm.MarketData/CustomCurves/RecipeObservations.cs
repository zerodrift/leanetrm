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
        RecipeContract Resolve(DateOnly day,int position)=>ObservedContract(contracts,day,samplingTime,position);
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
                var delivery=contracts[index[0]].Contract;
                first=DateOnly.FromDateTime(contracts[index[0]-1].Contract.Expiry.UtcDateTime);last=DateOnly.FromDateTime(delivery.Expiry.UtcDateTime);
                member=day=>Instant(day,samplingTime)<delivery.Expiry&&Resolve(day,1).Contract.Key==delivery.Key;
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
    // One cell per target month: the sum of its groups, each the weighted mean of its terms' values, each value an equally
    // weighted mean of one leaf per observation day, less the mean of its spread position. A day before the context date
    // reads its retained sample; the context date and later days read today's live sample of the contract selected for that day.
    private static Panel ObservationPanel(RecipeNode node,RecipeInputs inputs,Func<string,int,string,DateOnly,RecipeContract,int,DateOnly,DateTimeOffset,Cell> leaf,
        Func<CurveKey,DateOnly,DateTimeOffset> sampledAt)
    {
        var (targets,_,terms)=ObservationSettings(node);var windows=inputs.Windows[node.Id];
        if(windows.IsDefault||windows.Any(w=>w is null||w.Target is null||w.Days.IsDefaultOrEmpty||w.Days.Distinct().Count()!=w.Days.Length||!w.Days.SequenceEqual(w.Days.Order()))||
            windows.Length!=targets.Length*terms.Length||!windows.Select(w=>(w.Target,w.Term)).ToHashSet().SetEquals(targets.SelectMany(t=>terms.Select(term=>(t,term.Ordinal)))))
            throw Missing("Supply exactly one nonempty ascending observation window for every target and term.");
        var sourceNode=node.Inputs["source"];var source=inputs.Sources[sourceNode];var time=TimeOnly.FromDateTime(inputs.ValuationAt.UtcDateTime);
        var one=ExactDecimal.Parse("1");var index=0;var cells=ImmutableArray.CreateBuilder<Cell>();
        foreach(var (target,position) in targets.Select((t,i)=>(t,i+1)))
        {
            var leaves=new List<Cell>();
            CellFormula Mean(ImmutableArray<DateOnly> days,int stated)
            {
                var parts=days.Select(day=>
                {
                    var instant=Instant(day,time);var contract=ObservedContract(source.Contracts,day,time,stated);
                    if(day<inputs.ContextDate&&sampledAt(source.Key,day)!=instant)throw Missing($"Observation day {day:yyyy-MM-dd} must be sampled at the context time of day.");
                    var cell=day<inputs.ContextDate?leaf(node.Id,index++,sourceNode,day,contract,position,day,instant):
                        leaf(node.Id,index++,sourceNode,inputs.ContextDate,contract,position,day,inputs.ValuationAt);
                    leaves.Add(cell);return cell.Formula;
                }).ToImmutableArray();
                return new(node.Id,index++,null,parts,[..parts.Select(_=>one)],WeightedMean);
            }
            var groups=terms.GroupBy(t=>t.Group,StringComparer.Ordinal).Select(group=>
            {
                var values=group.Select(term=>
                {
                    var days=windows.Single(w=>w.Target==target&&w.Term==term.Ordinal).Days;var mean=Mean(days,term.Position);
                    if(term.LessPosition is not {} less)return mean;
                    var lessMean=Mean(days,less);return new CellFormula(node.Id,index++,null,[mean,lessMean],[one,-one],SignedSum);
                }).ToImmutableArray();
                return new CellFormula(node.Id,index++,null,values,[..group.Select(t=>Weight(t.Weight))],WeightedMean);
            }).ToImmutableArray();
            cells.Add(new(position,[..windows.Where(w=>w.Target==target).SelectMany(w=>w.Days).Distinct().Order()],inputs.ContextDate,source.Unit,
                [..leaves.SelectMany(c=>c.Inputs)],new(node.Id,index++,null,groups,[..groups.Select(_=>one)],SignedSum),target.From,target.To));
        }
        return new(cells.ToImmutable());
    }
    // The contract at a stated position at one observation day's sampling instant; selection and sampling share that instant.
    private static RecipeContract ObservedContract(ImmutableArray<RecipeContract> contracts,DateOnly day,TimeOnly samplingTime,int position)=>
        ResolveContracts(contracts,"positions",position,1,Instant(day,samplingTime),day,requireLive:false)[0];
    private static DateTimeOffset Instant(DateOnly day,TimeOnly time)=>new(day.ToDateTime(time),TimeSpan.Zero);
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
