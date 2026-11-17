using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;

internal static class StorageFacilityChecks
{
    static readonly ReferenceKey Unit=new(new(Guid.Parse("51111111-1111-1111-1111-111111111111"),Guid.Parse("52222222-2222-2222-2222-222222222222")),"synthetic_native_quantity");
    static readonly Guid Port=Guid.Parse("53333333-3333-3333-3333-333333333333");
    static readonly DateTimeOffset Start=DateTimeOffset.Parse("2032-03-14T08:00:00Z");
    static Quantity Q(string n)=>new(ExactDecimal.Parse(n),Unit);
    static StorageFacilityRules Rules()=>new(Guid.NewGuid(),StorageFacilityRules.Version,Unit,[Port],Q("0"),Q("1000"),[
        new(Q("0"),Q("500"),Q("100"),Q("50")),new(Q("500"),Q("1000"),Q("25"),Q("100"))],
        StorageFacilityRules.TotalPhysical,StorageFacilityRules.ElapsedHour,StorageFacilityRules.SeparateDirections,StorageFacilityRules.NoLoss);
    static StorageFacilityOpening Opening(string quantity)=>new(Guid.NewGuid(),Start,Q(quantity));
    static StorageFacilityMovement Move(string direction,string amount,double begin,double end)=>
        new(Guid.NewGuid(),Port,Start.AddHours(begin),Start.AddHours(end),direction,Q(amount));
    static StorageFacilityCoverage Coverage(double hours,string track="planned",string statement=StorageFacilityCoverage.ChosenPlan)=>
        new(Guid.NewGuid(),Start,Start.AddHours(hours),track,statement);
    static StorageFacilityAssessment Assess(string opening,double hours,params StorageFacilityMovement[] flows)=>
        StorageFacilityOperations.Assess(Rules(),Opening(opening),Start,Start.AddHours(hours),[..flows],Coverage(hours));
    internal static void Run()
    {
        // The low-band rate is legal at opening, but traversing the higher band is not.
        var crossing=Assess("450",1,Move("inject","100",0,1));
        Equal(crossing.ClosingLevel!,"550");Breach(crossing,"injection_rate");
        Reject(()=>crossing.RequireFeasible(),"storage_facility_infeasible");
        var edge=Assess("450",0.5,Move("inject","50",0,0.5));edge.RequireFeasible();Equal(edge.ClosingLevel!,"500");
        var leaving=Assess("500",1,Move("inject","25",0,1));leaving.RequireFeasible();Equal(leaving.ClosingLevel!,"525");
        var returning=Assess("500",1,Move("withdraw","50",0,1));returning.RequireFeasible();Equal(returning.ClosingLevel!,"450");
        var overlap=Assess("100",1,Move("inject","60",0,1),Move("inject","50",0,1));
        Equal(overlap.ClosingLevel!,"210");Breach(overlap,"injection_rate");
        var opposing=Assess("100",1,Move("inject","10",0,1),Move("withdraw","10",0,1));
        Equal(opposing.ClosingLevel!,"100");Breach(opposing,"simultaneous_flow");
        var recovered=Assess("950",2,Move("inject","100",0,1),Move("withdraw","100",1,2));
        Equal(recovered.ClosingLevel!,"950");Breach(recovered,"inventory_maximum");
        // Evidence is retained beyond the bound; assessing it never clamps its level.
        Equal(Assess("990",1,Move("inject","20",0,1)).ClosingLevel!,"1010");
        var split=Assess("100",1,Move("inject","10",0,1),Move("inject","1",0,1.0/3));
        split.RequireFeasible();Equal(split.ClosingLevel!,"111");
        var staggeredInjection=Assess("490",2,Move("inject","20",0,2),Move("inject","30",0.5,1.5));
        Equal(staggeredInjection.ClosingLevel!,"540");Breach(staggeredInjection,"injection_rate");
        var staggeredWithdrawal=Assess("510",2,Move("withdraw","20",0,2),Move("withdraw","60",0.5,1.5));
        Equal(staggeredWithdrawal.ClosingLevel!,"430");Breach(staggeredWithdrawal,"withdrawal_rate");
        var falling=Assess("550",1,Move("withdraw","100",0,1));Breach(falling,"withdrawal_rate");
        // A partially elapsed decimal flow remains a rational at another flow's boundary.
        if(split.Intervals[0].Closing.Numerator!="313"||split.Intervals[0].Closing.Denominator!="3")throw new Exception("Intermediate inventory must retain exact rational quantity.");
        var conservationFlows=new[]{Move("inject","40",0,2),Move("inject","20",0.5,1.5),Move("withdraw","15",2,3)};
        var conservation=Assess("100",3,conservationFlows);conservation.RequireFeasible();Equal(conservation.ClosingLevel!,"145");
        var reordered=Assess("100",3,conservationFlows.Reverse().ToArray());reordered.RequireFeasible();Equal(reordered.ClosingLevel!,"145");
        if(conservation.Intervals.Length!=reordered.Intervals.Length||conservation.Intervals.Zip(reordered.Intervals).Any(pair=>pair.First.Opening!=pair.Second.Opening||pair.First.Closing!=pair.Second.Closing||pair.First.InjectionPerHour!=pair.Second.InjectionPerHour))throw new Exception("Activity order cannot change physical assessment.");
        // Elapsed 23/25-hour intervals do not acquire a nominal 24-hour multiplier.
        var shortDay=Assess("0",23,Move("inject","460",0,23));shortDay.RequireFeasible();Equal(shortDay.ClosingLevel!,"460");
        var longDay=Assess("0",25,Move("inject","500",0,25));longDay.RequireFeasible();Equal(longDay.ClosingLevel!,"500");
        var missing=StorageFacilityOperations.Assess(Rules(),null,Start,Start.AddHours(1),[Move("inject","10",0,1)],Coverage(1));
        if(missing.Constraint!=FacilityConstraintState.Unassessed||missing.ClosingLevel is not null)throw new Exception("Missing opening evidence must remain unassessed.");
        Reject(()=>missing.RequireFeasible(),"storage_facility_opening_required");
        var partial=StorageFacilityOperations.Assess(Rules(),Opening("100"),Start,Start.AddHours(1),[Move("inject","10",0,1)],
            Coverage(1,"measured",StorageFacilityCoverage.IncompleteActual));
        if(partial.Constraint!=FacilityConstraintState.Unassessed||partial.ClosingLevel is not null)throw new Exception("Partial measured reporting cannot establish total physical inventory.");
        var partialBelow=StorageFacilityOperations.Assess(Rules(),Opening("-5"),Start,Start.AddHours(1),[],
            Coverage(1,"measured",StorageFacilityCoverage.IncompleteActual));
        if(partialBelow.Constraint!=FacilityConstraintState.Unassessed||partialBelow.ClosingLevel is not null||!partialBelow.Violations.Contains("inventory_minimum"))throw new Exception("Incomplete coverage must preserve a known opening breach without inventing a closing level.");
        var actual=StorageFacilityOperations.Assess(Rules(),Opening("100"),Start,Start.AddHours(1),[],
            Coverage(1,"measured",StorageFacilityCoverage.CompleteActual));actual.RequireFeasible();Equal(actual.ClosingLevel!,"100");
        Reject(()=>StorageFacilityOperations.Assess(Rules(),Opening("100"),Start,Start.AddHours(1),[],Coverage(2)));
        Reject(()=>StorageFacilityOperations.Assess(Rules(),Opening("100"),Start,Start.AddHours(1),[],Coverage(1,"measured")));
        var below=Assess("-5",1);Breach(below,"inventory_minimum");Equal(below.ClosingLevel!,"-5");
        var one=Move("inject","10",0,1);Reject(()=>Assess("100",1,one,one));
        Reject(()=>Assess("100",1,one with{PortId=Guid.NewGuid()}));
        Reject(()=>Assess("100",1,one with{From=Start.AddHours(-1)}));
        Reject(()=>Assess("100",1,one with{To=Start.AddHours(2)}));
        Reject(()=>Assess("100",1,one with{Quantity=Q("-1")}));
        Reject(()=>Assess("100",1,one with{Direction="net"}));
        var rules=Rules();Reject(()=>StorageFacilityOperations.Assess(rules with{Implementation="unknown"},Opening("1"),Start,Start.AddHours(1),[],Coverage(1)));
        Reject(()=>StorageFacilityOperations.Assess(rules with{Ratchets=[new(Q("0"),Q("499"),Q("100"),Q("100")),new(Q("500"),Q("1000"),Q("100"),Q("100"))]},Opening("1"),Start,Start.AddHours(1),[],Coverage(1)));
        Console.WriteLine("PASS storage facility: exact physical conservation, overlapping gross flows, intermediate ratchets/bounds, missing evidence, measured breaches and elapsed 23/25-hour intervals");
    }
    static void Equal(ExactQuantity q,string expected){if(q.ToExactString()!=expected)throw new Exception($"Expected {expected}; got {q.ToExactString()}");}
    static void Breach(StorageFacilityAssessment a,string code){if(a.Constraint!=FacilityConstraintState.Breached||!a.Violations.Contains(code))throw new Exception("Expected physical breach "+code);}
    static void Reject(Action action,string code="storage_facility_input")
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
