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
    [property:JsonPropertyName("swing_increment")] string? Increment,
    [property:JsonPropertyName("swing_daily_min")] string? DailyMinimum,
    [property:JsonPropertyName("swing_daily_max")] string? DailyMaximum,
    [property:JsonPropertyName("swing_total_min")] string? TotalMinimum,
    [property:JsonPropertyName("swing_total_max")] string? TotalMaximum,
    [property:JsonPropertyName("swing_lead_hours")] string? LeadHours,
    [property:JsonPropertyName("swing_quantity_policy")] string? QuantityPolicy,
    [property:JsonPropertyName("swing_model")] string? Model,
    [property:JsonPropertyName("swing_numerical_policy")] string? NumericalPolicy);

internal sealed record SwingTicketProjection(string Source,string ReferenceQuantity,string PriceUnit,ImmutableArray<SwingDay> Days);

internal static class GasSwingTicket
{
    static TicketField Field(string id,string label,string meaning,string type="choice") =>
        new(id,label,meaning,type,type=="decimal"?"quantity":null,"editable",true,null,type is "choice" or "multiple_choice"?id:null,[],null,null,null);

    internal static readonly TicketTermSection Section=new("swingRights","Swing right",[
        Field("swing_side","Physical source","Independent physical side whose daily quantity is elected.") with{RefreshOn=["link_source"]},
        Field("swing_entries","Sides covered","Select the source and every fixed payment or fee following it.","multiple_choice") with{RefreshOn=["swing_side","cash_source","fee_source"]},
        Field("swing_holder","Held by","Party entitled to elect quantities."),
        Field("swing_increment","Quantity increment","Size of one contractual volume increment.","decimal"),
        Field("swing_daily_min","Daily minimum","Least electable quantity per gas day.","decimal"),
        Field("swing_daily_max","Daily maximum","Most electable quantity per gas day.","decimal"),
        Field("swing_total_min","Term minimum","Minimum quantity across the whole term.","decimal"),
        Field("swing_total_max","Term maximum","Maximum quantity across the whole term.","decimal"),
        Field("swing_lead_hours","Election lead in hours","Whole hours before the first instant of each gas day.","integer"),
        Field("swing_quantity_policy","Valuation quantity","Commercial election and selected delivery quantity policy."),
        Field("swing_model","Pricing model","One-factor lognormal model, actual/365 fixed instant, held log-return curve marks and no discounting."),
        Field("swing_numerical_policy","Numerical policy","Six-hour trinomial grid and half-even rounding to 18 places.")]){MaximumRows=1};

    internal static SwingTicketProjection Projection(PreviewData data,GasCompositionRequest terms,ImmutableArray<GasCompositionRow> members,SwingRight right)
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
        "swing_quantity_policy"=>[new(SwingRight.ElectedDeliveryQuantity,"Elected daily quantity with selected delivery evidence")],
        "swing_model"=>[new(SwingPricingBinding.SupportedModel,"One-factor lognormal · ACT/365 fixed instant · held log-return curve marks · undiscounted")],
        "swing_numerical_policy"=>[new(SwingPricingBinding.SupportedNumericalPolicy,"Trinomial · 6-hour grid · half-even to 18 decimals")],
        _=>throw new TicketFieldException("ticket_input","This swing field has no choices.",null,id)
    };

    internal static ImmutableArray<TicketSwingRightDraft>? Draft(ImmutableArray<GasSwingRightRequest>? terms) =>
        terms is {Length:>0} rows?[..rows.Select(r=>new TicketSwingRightDraft(r.Side,r.Entries,r.Holder,r.Increment,r.DailyMinimum,
            r.DailyMaximum,r.TotalMinimum,r.TotalMaximum,r.LeadHours.ToString(CultureInfo.InvariantCulture),r.QuantityPolicy,r.Pricing.Model,r.Pricing.NumericalPolicy))]:null;

    internal static ImmutableArray<GasSwingRightRequest>? Request(TicketDraft draft)
    {
        if(draft.SwingRights is not {Length:>0} rows)return null;
        TicketFieldException Invalid(string field,string message,int row=0)=>new("gas_composition_input",message,null,field){TermRow=row,TermSection=Section.Id};
        if(rows.Length>1)throw Invalid("swing_side","Select at most one bounded swing right.",1);
        var right=rows[0]??throw Invalid("swing_side","Select the swing source.");
        string Choice(string field,string? value)=>Choices(draft,field).Any(c=>c.Value==value)?value!:throw Invalid(field,"Select one of the offered choices.");
        var source=Choice("swing_side",right.Side);
        var owner=draft.Sides.Single(s=>s.Code==source);
        if(owner.Values.GetValueOrDefault("quantity_policy") is not (null or SwingRight.ElectedDeliveryQuantity))
            throw new TicketFieldException("gas_composition_input","Select the quantity governed by the swing right.",source,"quantity_policy");
        var followers=draft.Sides.Where(s=>s.Values.GetValueOrDefault(s.Kind+"_source")==source&&s.Kind is "cash" or "fee" or "floating").ToArray();
        var expected=followers.Select(s=>s.Code).Append(source).ToHashSet(StringComparer.Ordinal);
        if(right.Entries is not {} entries||entries.Length!=expected.Count||!expected.SetEquals(entries)||
            followers.Any(s=>s.Kind=="floating"||s.Values[s.Kind+"_part"]!="whole"||s.Values[s.Kind+"_start"]!=owner.Values["delivery_from"]||
                s.Values[s.Kind+"_end"]!=owner.Values["delivery_to"])||draft.ExerciseRights?.Any(r=>r.Sides?.Any(expected.Contains)==true)==true)
            throw Invalid("swing_entries","Cover the source and every full-term fixed payment or fee exactly once, outside any other right.");
        if(owner.Values["quantity_basis"]!="per_operating_day")
            throw new TicketFieldException("gas_composition_input","The swing source requires a daily reference quantity.",source,"quantity_basis");
        var days=DateWorkspace.Date(owner.Values["delivery_to"]).DayNumber-DateWorkspace.Date(owner.Values["delivery_from"]).DayNumber;
        if(days is <1 or >31)throw new TicketFieldException("gas_composition_input","Select one to 31 whole gas days.",source,"delivery_to");
        ExactDecimal Amount(string field,string? value)
        {
            try{if(value is not null&&!value.StartsWith('-'))return ExactDecimal.Parse(value);}
            catch(MarketDataException e){throw Invalid(field,e.Message);}
            throw Invalid(field,"Enter a nonnegative quantity.");
        }
        var increment=Amount("swing_increment",right.Increment);
        if(increment.CompareTo(default)<=0)throw Invalid("swing_increment","Enter a positive contractual increment.");
        long Volume(string field,string? value)
        {
            var amount=Amount(field,value);
            try{return GasSwingComponent.QuantityLots(amount,increment);}
            catch(MarketDataException e){throw Invalid(field,e.Message);}
        }
        if(!owner.Values.TryGetValue("quantity",out var referenceQuantity))
            throw new TicketFieldException("gas_composition_input","Enter the physical daily reference quantity.",source,"quantity");
        int reference;
        try{reference=GasSwingComponent.QuantityLots(ExactDecimal.Parse(referenceQuantity),increment);}
        catch(MarketDataException e){throw Invalid("swing_increment",e.Message);}
        if(reference==0)throw Invalid("swing_increment","The reference quantity must be positive.");
        var minimum=Volume("swing_daily_min",right.DailyMinimum);
        var maximum=Volume("swing_daily_max",right.DailyMaximum);
        var totalMinimum=Volume("swing_total_min",right.TotalMinimum);
        var totalMaximum=Volume("swing_total_max",right.TotalMaximum);
        if(minimum>reference)throw Invalid("swing_daily_min","The daily minimum exceeds the reference quantity.");
        if(maximum<reference)throw Invalid("swing_daily_max","The daily maximum is below the reference quantity.");
        if(totalMinimum>totalMaximum||totalMinimum>maximum*days)throw Invalid("swing_total_min","The daily limits and term maximum cannot complete this term minimum.");
        if(totalMaximum<minimum*days)throw Invalid("swing_total_max","The daily minimum exceeds this term budget.");
        if(!int.TryParse(right.LeadHours,NumberStyles.None,CultureInfo.InvariantCulture,out var hours))
            throw Invalid("swing_lead_hours","Enter a nonnegative whole number of hours.");
        return [new(source,entries,Choice("swing_holder",right.Holder),right.Increment!,right.DailyMinimum!,right.DailyMaximum!,
            right.TotalMinimum!,right.TotalMaximum!,hours,Choice("swing_quantity_policy",right.QuantityPolicy),
            new(Choice("swing_model",right.Model),OptionNumericalPolicies.ActualOver365FixedInstant,
                Choice("swing_numerical_policy",right.NumericalPolicy),ExerciseMarks.HeldLogReturn))];
    }
}
