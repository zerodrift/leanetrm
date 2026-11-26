using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData.Gas;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketSwingRightDraft(
    [property:JsonPropertyName("swing_side")] string? Side,
    [property:JsonPropertyName("swing_entries")] ImmutableArray<string>? Entries,
    [property:JsonPropertyName("swing_holder")] string? Holder,
    [property:JsonPropertyName("swing_planning_step")] string? PlanningStep,
    [property:JsonPropertyName("swing_daily_min")] string? DailyMinimum,
    [property:JsonPropertyName("swing_daily_max")] string? DailyMaximum,
    [property:JsonPropertyName("swing_total_min")] string? TotalMinimum,
    [property:JsonPropertyName("swing_total_max")] string? TotalMaximum,
    [property:JsonPropertyName("swing_lead_hours")] string? LeadHours,
    [property:JsonPropertyName("swing_limit_period")] string? LimitPeriod,
    [property:JsonPropertyName("swing_total_basis")] string? TotalBasis,
    [property:JsonPropertyName("swing_change_count")] string? ChangeCount,
    [property:JsonPropertyName("swing_count_rule")] string? CountRule,
    [property:JsonPropertyName("swing_plan_rule")] string? PlanRule,
    [property:JsonPropertyName("swing_deadline_rule")] string? DeadlineRule,
    [property:JsonPropertyName("swing_model")] string? Model,
    [property:JsonPropertyName("swing_numerical_policy")] string? NumericalPolicy);

internal sealed record SwingTicketProjection(string Source,string ReferenceQuantity,string PriceUnit,ImmutableArray<SwingDay> Days);

internal static class GasSwingTicket
{
    internal static string FieldId(string field)=>field switch
    {
        "planningStep"=>"swing_planning_step","dailyMinimum"=>"swing_daily_min","dailyMaximum"=>"swing_daily_max",
        "totalMinimum"=>"swing_total_min","totalMaximum"=>"swing_total_max","limitPeriod"=>"swing_limit_period",
        "totalBasis"=>"swing_total_basis","changeCount"=>"swing_change_count","countRule"=>"swing_count_rule",
        "planRule"=>"swing_plan_rule","deadlineRule"=>"swing_deadline_rule","leadHours"=>"swing_lead_hours",
        "numericalPolicy"=>"swing_numerical_policy","quantityPolicy"=>"quantity_policy","side"=>"swing_side",_=>field
    };

    static TicketField Field(string id,string label,string meaning,string type="choice") =>
        new(id,label,meaning,type,type=="decimal"?"quantity":null,"editable",true,null,type is "choice" or "multiple_choice"?id:null,[],null,null,null);

    internal static readonly TicketTermSection Section=new("swingRights","Swing right",[
        Field("swing_side","Physical source","Independent physical side whose daily quantity is elected.") with{RefreshOn=["link_source"]},
        Field("swing_entries","Sides covered","Select the source and every fixed payment or fee following it.","multiple_choice") with{RefreshOn=["swing_side","cash_source","fee_source"]},
        Field("swing_holder","Held by","Party entitled to elect quantities."),
        Field("swing_planning_step","Planning step","Spacing between quantities considered for future decisions.","decimal"),
        Field("swing_daily_min","Daily minimum","Least electable quantity per gas day.","decimal"),
        Field("swing_daily_max","Daily maximum","Most electable quantity per gas day.","decimal"),
        Field("swing_limit_period","Limit period","Period over which totals and changed days are counted."),
        Field("swing_total_min","Minimum total","Lower quantity bound on the selected basis.","decimal") with {Required=false},
        Field("swing_total_max","Maximum total","Upper quantity bound on the selected basis.","decimal") with {Required=false},
        Field("swing_total_basis","Total basis","Select a basis when either total is supplied.") with {Required=false},
        Field("swing_change_count","Changed days","Maximum days differing from the reference per period.","integer") with {Required=false},
        Field("swing_count_rule","Count rule","Select how days count against the limit.") with {Required=false},
        Field("swing_plan_rule","Planning rule","Select the comparison schedule on forwards."),
        Field("swing_deadline_rule","Deadline rule","Select the deadline for each retained day."),
        Field("swing_lead_hours","Election lead in hours","Whole hours before the first instant of each gas day.","integer"),
        Field("swing_model","Pricing model","One-factor lognormal model, actual/365 fixed instant, held log-return curve marks and no discounting."),
        Field("swing_numerical_policy","Numerical policy","Six-hour trinomial grid and half-even rounding to 18 places.")]){MaximumRows=1};

    internal static SwingTicketProjection Projection(PreviewData data,GasCompositionRequest terms,ImmutableArray<GasCompositionRow> members,SwingQuantityRight right)
    {
        var source=members.Single(m=>m.Slot==right.PhysicalSlot);
        return new(source.Code,terms.PhysicalSides.Single(s=>s.Code==source.Code).Quantity!,
            data.UnitLabel(PriceUnit.FromQuote(source.Assignment.Quote)),right.Days);
    }

    internal static bool Governs(TicketDraft draft,string? code) => code is not null && draft.SwingRights?.Any(right=>right?.Side==code)==true;

    internal static ImmutableArray<TicketChoice> Choices(TicketDraft draft,string id) => id switch
    {
        "swing_side"=>[..draft.Sides.Where(s=>s.Kind=="physical"&&!s.Values.ContainsKey("link_source")&&
            !draft.Sides.Any(p=>p.Values.GetValueOrDefault("link_source")==s.Code)).Select(s=>new TicketChoice(s.Code,s.Code))],
        "swing_entries"=>[..draft.Sides.Where(s=>Governs(draft,s.Code)||s.Kind is "cash" or "fee"&&Governs(draft,s.Values.GetValueOrDefault(s.Kind+"_source"))).Select(s=>new TicketChoice(s.Code,s.Code))],
        "swing_holder"=>GasExerciseTicket.Choices(draft,"right_holder"),
        "swing_limit_period"=>[new("term","Whole term"),new("delivery_month","Delivery month")],
        "swing_total_basis"=>[new("per_period_v1","Per period"),new("per_day_v1","Per retained day")],
        "swing_count_rule"=>[new("differs_from_base_v1","Any quantity differing from reference")],
        "swing_plan_rule"=>[new("best_plan_on_forwards_v1","Best feasible schedule on forwards")],
        "swing_deadline_rule"=>[new("lead_before_first_gas_day_v1","Lead before each gas day")],
        "swing_model"=>[new(SwingPricingBinding.SupportedModel,"One-factor lognormal · ACT/365 fixed instant · held log-return curve marks · undiscounted")],
        "swing_numerical_policy"=>[new("swing_trinomial_6h_unit_reference_v2","Trinomial · 6-hour grid · half-even to 18 decimals")],
        _=>throw new TicketFieldException("ticket_input","This swing field has no choices.",null,id)
    };

    internal static ImmutableArray<TicketSwingRightDraft>? Draft(ImmutableArray<GasSwingRightRequest>? terms) =>
        terms is {Length:>0} rows?[..rows.Select(r=>new TicketSwingRightDraft(r.Side,r.Entries,r.Holder,r.PlanningStep,r.DailyMinimum,
            r.DailyMaximum,r.TotalMinimum,r.TotalMaximum,r.LeadHours.ToString(CultureInfo.InvariantCulture),r.LimitPeriod,r.TotalBasis,r.ChangeCount?.ToString(CultureInfo.InvariantCulture),r.CountRule,r.PlanRule,r.DeadlineRule,r.Pricing.Model,r.Pricing.NumericalPolicy))]:null;

    internal static ImmutableArray<GasSwingRightRequest>? Request(TicketDraft draft)
    {
        if(draft.SwingRights is not {Length:>0} rows)return null;
        TicketFieldException Invalid(string field,string message,int row=0)=>new("swing_right",message,null,field){TermRow=row,TermSection=Section.Id};
        if(rows.Length>1)throw Invalid("swing_side","Select at most one swing right.",1);
        var right=rows[0]??throw Invalid("swing_side","Select the swing source.");
        string Choice(string field,string? value)=>Choices(draft,field).Any(c=>c.Value==value)?value!:throw Invalid(field,"Select one of the offered choices.");
        var source=Choice("swing_side",right.Side);
        var owner=draft.Sides.Single(s=>s.Code==source);
        if(owner.Values.GetValueOrDefault("quantity_policy") is not ("contract" or GasDeliveryQuantityStore.Policy))
            throw new TicketFieldException("swing_right","Select the quantity governed by the swing right.",source,"quantity_policy");
        var followers=draft.Sides.Where(s=>s.Values.GetValueOrDefault(s.Kind+"_source")==source&&s.Kind is "cash" or "fee" or "floating").ToArray();
        var expected=followers.Select(s=>s.Code).Append(source).ToHashSet(StringComparer.Ordinal);
        if(right.Entries is not {} entries||entries.Length!=expected.Count||!expected.SetEquals(entries)||
            followers.Any(s=>s.Kind=="floating"||s.Values[s.Kind+"_part"]!="whole"||s.Values[s.Kind+"_start"]!=owner.Values["delivery_from"]||
                s.Values[s.Kind+"_end"]!=owner.Values["delivery_to"])||draft.ExerciseRights?.Any(r=>r.Sides?.Any(expected.Contains)==true)==true)
            throw Invalid("swing_entries","Cover the source and every full-term fixed payment or fee exactly once, outside any other right.");
        if(owner.Values["quantity_basis"]!="per_operating_day")
            throw new TicketFieldException("gas_composition_input","The swing source requires a daily reference quantity.",source,"quantity_basis");
        var days=DateWorkspace.Date(owner.Values["delivery_to"]).DayNumber-DateWorkspace.Date(owner.Values["delivery_from"]).DayNumber;
        if(days is <1 or >GasQuantityTerms.MaximumOperatingDays)throw new TicketFieldException("gas_composition_input","Select a supported whole gas-day delivery term.",source,"delivery_to");
        ExactDecimal Amount(string field,string? value)
        {
            try{if(value is not null&&!value.StartsWith('-'))return ExactDecimal.Parse(value);}
            catch(MarketDataException e){throw Invalid(field,e.Message);}
            throw Invalid(field,"Enter a nonnegative quantity.");
        }
        var step=Amount("swing_planning_step",right.PlanningStep);
        if(step.CompareTo(default)<=0)throw Invalid("swing_planning_step","Enter a positive planning step.");
        var reference=Amount("quantity",owner.Values.GetValueOrDefault("quantity"));
        if(Amount("swing_daily_min",right.DailyMinimum).CompareTo(reference)>0)throw Invalid("swing_daily_min","The minimum exceeds the reference.");
        if(Amount("swing_daily_max",right.DailyMaximum).CompareTo(reference)<0)throw Invalid("swing_daily_max","The maximum is below the reference.");
        if(right.TotalMinimum is not null)Amount("swing_total_min",right.TotalMinimum);
        if(right.TotalMaximum is not null)Amount("swing_total_max",right.TotalMaximum);
        var bounded=right.TotalMinimum is not null || right.TotalMaximum is not null;
        if(bounded!=(right.TotalBasis is not null))throw Invalid("swing_total_basis","Select a basis exactly when supplying a total.");
        if(right.TotalBasis is {} basis)Choice("swing_total_basis",basis);
        int? count=null;
        if(right.ChangeCount is {} text)
        {
            if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var entered))throw Invalid("swing_change_count","Enter a nonnegative whole number of days.");
            count=entered;
            Choice("swing_count_rule",right.CountRule);
        }
        else if(right.CountRule is not null)throw Invalid("swing_count_rule","A count rule requires a changed-day limit.");
        if(!int.TryParse(right.LeadHours,NumberStyles.None,CultureInfo.InvariantCulture,out var hours))
            throw Invalid("swing_lead_hours","Enter a nonnegative whole number of hours.");
        return [new(source,entries,Choice("swing_holder",right.Holder),right.PlanningStep!,right.DailyMinimum!,right.DailyMaximum!,
            Choice("swing_limit_period",right.LimitPeriod),right.TotalMinimum,right.TotalMaximum,right.TotalBasis,count,right.CountRule,
            Choice("swing_plan_rule",right.PlanRule),Choice("swing_deadline_rule",right.DeadlineRule),hours,
            owner.Values["quantity_policy"]=="contract"?SwingQuantityRight.Elected:SwingQuantityRight.ElectedDeliveryQuantity,
            new(Choice("swing_model",right.Model),OptionNumericalPolicies.ActualOver365FixedInstant,
                Choice("swing_numerical_policy",right.NumericalPolicy),ExerciseMarks.HeldLogReturn))];
    }
}
