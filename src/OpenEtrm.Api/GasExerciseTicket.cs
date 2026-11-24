using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketExerciseRightDraft(
    [property:JsonPropertyName("right_sides")] ImmutableArray<string>? Sides,
    [property:JsonPropertyName("right_period")] string? Period,
    [property:JsonPropertyName("right_holder")] string? Holder,
    [property:JsonPropertyName("right_settlement")] string? Settlement,
    [property:JsonPropertyName("right_lead_hours"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? LeadHours);

internal static class GasExerciseTicket
{
    static TicketField Field(string id,string label,string meaning,string type="choice")=>
        new(id,label,meaning,type,null,"editable",true,null,type=="integer"?null:id,[],null,null,null);

    internal static readonly TicketTermSection Section=new("exerciseRights","Exercise right",[
        Field("right_sides","Sides covered","Sides governed by this exercise right.","multiple_choice"),
        Field("right_period","Exercise period","Delivery periods decided by this right."),
        Field("right_holder","Held by","Party holding the exercise right."),
        Field("right_settlement","Settlement","How exercise is determined."),
        Field("right_lead_hours","Exercise lead in hours","Whole hours before the first gas day of the period.","integer")
            with{VisibleWhen=new("right_settlement",["physical"])}]);

    internal static ImmutableArray<TicketChoice> Choices(TicketDraft draft,string field)=>field switch
    {
        "right_sides"=>[..draft.Sides.Where(s=>s.Kind!=GasTicket.Charge).Select(s=>new TicketChoice(s.Code,s.Code))],
        "right_period"=>[new("delivery_day","Each gas day"),new("delivery_month","Each delivery month")],
        "right_holder"=>[new("us","Us"),new("counterparty","Counterparty")],
        "right_settlement"=>[new("physical","Physical, by election"),new("cash_automatic","Cash, by published prices")],
        _=>throw new TicketFieldException("ticket_input","The selected exercise field has no choices.",null,field)
    };

    internal static ImmutableArray<TicketExerciseRightDraft>? Draft(ImmutableArray<GasExerciseRightRequest>? rights)=>
        rights is {Length:>0} rows?[..rows.Select(r=>new TicketExerciseRightDraft(r.Entries,r.Period,r.Holder,r.Settlement,r.LeadHours?.ToString(CultureInfo.InvariantCulture)))]:null;

    internal static ImmutableArray<GasExerciseRightRequest>? Request(TicketDraft draft)
    {
        if(draft.ExerciseRights is not {Length:>0} rows)return null;
        var result=ImmutableArray.CreateBuilder<GasExerciseRightRequest>(rows.Length);
        var ordered=draft.Sides.Where(s=>s.Kind!=GasTicket.Charge).OrderByDescending(s=>s.Kind=="physical");
        var slots=ordered.Select((side,slot)=>(side.Code,slot)).ToDictionary(e=>e.Code,e=>e.slot,StringComparer.Ordinal);
        var physicalCount=draft.Sides.Count(s=>s.Kind=="physical");
        var charges=draft.Sides.Where(s=>s.Kind==GasTicket.Charge).Select(s=>s.Code).ToHashSet(StringComparer.Ordinal);
        var claimed=new HashSet<int>();
        foreach(var row in rows)
        {
            TicketFieldException Invalid(string field,string message)=>new("gas_composition_input",message,null,field){TermRow=result.Count};
            string Choice(string field,string? value)=>Choices(draft,field).Any(c=>c.Value==value)?value!:
                throw Invalid(field,"Select one of the offered choices.");
            if(row?.Sides is not {Length:>0} entries)
                throw Invalid("right_sides","Select existing sides. Each side may be covered only once.");
            if(entries.Any(charges.Contains))
                throw Invalid("right_sides","A charge is independent and cannot be covered by an exercise right.");
            if(draft.Sides.Any(side=>side.Kind=="physical"&&side.Values.ContainsKey("link_source")))
                throw Invalid("right_sides","Exercise rights require independent physical sources without quantity links.");
            var period=Choice("right_period",row.Period);
            var holder=Choice("right_holder",row.Holder);
            var settlement=Choice("right_settlement",row.Settlement);
            int? lead=null;
            if(settlement=="physical")
            {
                if(!int.TryParse(row.LeadHours,NumberStyles.None,CultureInfo.InvariantCulture,out var hours))
                    throw Invalid("right_lead_hours","Enter a nonnegative whole number of hours.");
                lead=hours;
            }
            else if(row.LeadHours is not null)throw Invalid("right_lead_hours","Cash exercise takes no lead in hours.");

            try{GasExerciseRight.ClaimEntries(result.Count,entries,settlement=="physical",slots,physicalCount,claimed);}
            catch(GasExerciseTermException error){throw Invalid("right_sides",error.Message);}
            result.Add(new(entries,period,holder,settlement,lead));
        }
        return result.MoveToImmutable();
    }
}
