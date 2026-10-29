using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;

// Two separate payments consume the same explicit estimated-price lookup.
// The first is overdue at the selected context; the second is not yet due.
// This is a numerical schedule fixture, not a new bookable product convention.
internal sealed class ScheduleProgramFixture
{
    internal ReferenceRevision Reference { get; }=new(Guid.NewGuid(),Guid.NewGuid());
    internal DefinitionRevision Definitions { get; }=new(Guid.NewGuid(),Guid.NewGuid());
    internal ReferenceKey R(string code)=>new(Reference,code);
    internal DateTimeOffset From { get; }=new(2031,11,1,0,0,0,TimeSpan.Zero);
    internal ResolvedCalculationContext Context=>new(Guid.NewGuid(),"synthetic_estimated_payment_inputs_v1",From.AddDays(2),From.AddDays(2),From.AddDays(2),From.AddDays(2));
    internal static ImmutableSortedDictionary<string,string> P(params(string Key,string Value)[] values)=>
        values.ToImmutableSortedDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
    internal ScheduleDefinition Definition()
    {
        ScheduleDefinitionRow Row(int ordinal,string key,string role,string operation,string kind,int? grid,
            ReferenceKey? unit,ReferenceKey? currency,string? quote,DateTimeOffset? at,int? payment,ImmutableSortedDictionary<string,string> parameters)=>
            new(ordinal,key,role,operation,1,kind,unit,currency,quote,null,null,null,null,null,at,payment,null,null,parameters){Grid=grid};
        var rows=new[]{
            Row(0,"quantity","source","quantity_literal","quantity",null,R("synthetic_mwh"),null,null,null,null,P(("numerator","1"),("denominator","2"))),
            Row(1,"price","source","price_lookup","price",null,R("synthetic_mwh"),R("synthetic_usd"),"1",null,null,P(("selection","interval_attribute_lookup_v1"))),
            Row(2,"amount","contribution","amount","money",null,null,R("synthetic_usd"),null,null,null,P()),
            Row(3,"first/discount","source","discount","factor",null,null,null,null,From.AddDays(1),0,P(("policy","context_required_v1"))),
            Row(4,"first/discounted","intermediate","pv","money",null,null,R("synthetic_usd"),null,From.AddDays(1),0,P()),
            Row(5,"first/value","root","output_guard","money",0,null,R("synthetic_usd"),null,From.AddDays(1),0,Guard()),
            Row(6,"second/discount","source","discount","factor",null,null,null,null,From.AddDays(3),1,P(("policy","context_required_v1"))),
            Row(7,"second/discounted","intermediate","pv","money",null,null,R("synthetic_usd"),null,From.AddDays(3),1,P()),
            Row(8,"second/value","root","output_guard","money",0,null,R("synthetic_usd"),null,From.AddDays(3),1,Guard()),
            Row(9,"first/total","intermediate","sum_cells","money",null,null,R("synthetic_usd"),null,null,null,P()),
            Row(10,"second/total","intermediate","sum_cells","money",null,null,R("synthetic_usd"),null,null,null,P()),
            Row(11,"side/pv","root","sum","money",null,null,R("synthetic_usd"),null,null,null,P())};
        return new(Guid.NewGuid(),"synthetic_payments","synthetic_two_payment_rows_v1","",ScheduleDefinition.CurrentSchema,ScheduleDefinition.CurrentGenerator,rows,
            [new(2,"quantity",0,0),new(2,"price",0,1),new(4,"amount",0,2),new(4,"discount",0,3),new(5,"amount",0,2),new(5,"discounted",0,4),
             new(7,"amount",0,2),new(7,"discount",0,6),new(8,"amount",0,2),new(8,"discounted",0,7),new(9,"input",0,5),new(10,"input",0,8),new(11,"input",0,9),new(11,"input",1,10)],
            [new(1,"curve","curve_interval",null,null,null,null,null,null,null,Definitions,"synthetic_energy")],
            [new("first/cash",2,"contractual_amount"),new("first/pv",5,"contribution_pv"),new("second/cash",2,"contractual_amount"),new("second/pv",8,"contribution_pv"),new("pv",11,"pv")],[],
            [new(0,"two_hours",R("synthetic_utc_clock"),new(2031,11,1),0,From,TimeSpan.TicksPerHour,2,[0,1],[1,1],[new DateOnly(2031,11,1).DayNumber,new DateOnly(2031,11,1).DayNumber],[0,60],[1,1])]);
    }
    private static ImmutableSortedDictionary<string,string> Guard()=>P(("measure","value_excluding_overdue_v1"),("lifecycle","interval_payment_v1"),
        ("pricePolicy","selected_context_v1"),("averaging","none"),("firstFixingAt","none"),("paymentRule","synthetic_explicit_dates_v1"),("unadjustedPayment","none"));
}
