using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class GasTransportCapacityPresentationChecks
{
    static Guid Id(int n)=>Guid.Parse($"73333333-3333-3333-3333-{n:D12}");
    static readonly ReferenceRevision Reference=new(Id(1),Id(2));
    static readonly ReferenceKey Unit=new(Reference,"synthetic_quantity"),Receipt=new(Reference,"synthetic_receipt"),Delivery=new(Reference,"synthetic_delivery");
    static readonly DateTimeOffset From=TradingInstant.Parse("2032-03-14T08:00:00Z","synthetic_time");
    static ExactQuantity Q(string value)=>ExactQuantity.FromQuantity(new(ExactDecimal.Parse(value),Unit));
    static GasTransportCapacityInterval Interval(string axis,ReferenceKey? point,Guid? path,ImmutableArray<Guid> claims,string used,string maximum,bool within)=>
        new(axis,point,path,From,From.AddHours(1),claims,Q(used),Q(maximum),within);
    static GasTransportCapacityAssessment Assessment()=>new(Id(3),false,[
        Interval("agreement",null,null,[Id(22),Id(21),Id(20)],"110","100",false),
        Interval("receipt",Receipt,null,[Id(21),Id(20)],"60","80",true),
        Interval("delivery",Delivery,null,[Id(22),Id(20),Id(21)],"110","100",false),
        Interval("route",null,Id(10),[Id(20),Id(21)],"60","60",true)]);
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,GasOwnedOperations.Json);
    static JsonElement Facts()=>Json(new{paths=new[]{new{id=Id(10),path_key="synthetic_path_a"},new{id=Id(11),path_key="synthetic_path_d"}},
        plans=new[]{new{id=Id(30),business_key="synthetic_plan_a"},new{id=Id(31),business_key="synthetic_plan_b"}},
        claims=new[]{new{id=Id(20),plan_id=Id(30)},new{id=Id(21),plan_id=Id(30)},new{id=Id(22),plan_id=Id(31)}}});
    static void Refuse(Action work)
    {
        try{work();}catch(MarketDataException error)when(error.Code=="gas_transport_capacity_source"){return;}
        throw new Exception("Unresolved or conflicting path/plan membership cannot become a UUID display fallback.");
    }
    static string Cell(JsonElement table,int row,int column)=>table.GetProperty("rows")[row][column].GetString()!;

    internal static void Run()
    {
        if(GasTransportCapacityPresentation.Tables(null,null,null).Length!=0)throw new Exception("An unavailable assessment cannot invent a display table.");
        var assessment=Assessment();var facts=Facts();var before=facts.GetRawText();
        var table=Json(GasTransportCapacityPresentation.Tables(assessment,facts,null))[0];
        if(Cell(table,0,8)!="-10"||Cell(table,1,8)!="20"||Cell(table,3,8)!="0")
            throw new Exception("Signed headroom uses the retained maximum and chosen quantity for each native interval.");
        if(table.GetProperty("columns")[7].GetString()!="Chosen plans"||!table.GetProperty("instantColumns").EnumerateArray().Select(v=>v.GetInt32()).SequenceEqual([2,3]))
            throw new Exception("The operating table must identify chosen plans while retaining exact instant columns.");
        if(Cell(table,3,0)!="Path"||Cell(table,3,1)!="synthetic_path_a"||Cell(table,0,1)!="Whole entitlement"||Cell(table,1,1)!="synthetic_receipt")
            throw new Exception("Path identities need their immutable business references; point codes and the whole-entitlement label remain exact.");
        if(Cell(table,0,7)!="synthetic_plan_a, synthetic_plan_b"||Cell(table,1,7)!="synthetic_plan_a"||Cell(table,3,7)!="synthetic_plan_a")
            throw new Exception("Contributor labels must resolve all independent plans and deduplicate multiple claims owned by one plan.");
        if(Cell(table,0,2)!="2032-03-14T08:00:00.000000Z"||Cell(table,0,3)!="2032-03-14T09:00:00.000000Z"||
            Cell(table,0,4)!="110"||Cell(table,0,5)!="100"||Cell(table,0,6)!="Capacity exceeded"||Cell(table,3,6)!="Within limit")
            throw new Exception("Label projection must preserve exact time, numeric rates, limits and computed breach status.");
        foreach(var id in new[]{Id(10),Id(11),Id(20),Id(21),Id(22),Id(30),Id(31)})
            if(table.GetRawText().Contains(id.ToString(),StringComparison.OrdinalIgnoreCase))throw new Exception("Internal path, claim and plan UUIDs must not replace display references.");
        if(facts.GetRawText()!=before||assessment.Intervals[0].Uses.Length!=3)throw new Exception("Presentation must not replace authoritative raw identities or mutate source facts.");

        var proposed=Json(new{kind="plan",businessKey="synthetic_plan_a",claims=new[]{new{claimId=Id(23)}}});
        var replacement=assessment with{Intervals=[Interval("route",null,Id(11),[Id(23),Id(22)],"90","100",true)]};
        var preview=Json(GasTransportCapacityPresentation.Tables(replacement,facts,proposed))[0];
        if(Cell(preview,0,1)!="synthetic_path_d"||Cell(preview,0,7)!="synthetic_plan_a, synthetic_plan_b"||Cell(preview,0,4)!="90")
            throw new Exception("A proposed replacement resolves its new claim IDs from the submitted plan without losing retained other-plan references.");
        var definition=Json(new{kind="define",paths=new[]{new{pathId=Id(12),pathKey="synthetic_new_path"}}});
        var fractional=ExactQuantity.FromQuantity(new(ExactDecimal.Parse("1"),Unit)).Scale(new ExactRatio(3600,7));
        var newDefinition=new GasTransportCapacityAssessment(Id(4),true,[new("route",null,Id(12),From,From.AddSeconds(7),[],fractional,Q("600"),true)]);
        var newTable=Json(GasTransportCapacityPresentation.Tables(newDefinition,null,definition))[0];
        if(Cell(newTable,0,1)!="synthetic_new_path"||Cell(newTable,0,4)!=fractional.ToExactString()||Cell(newTable,0,7)!=""||Cell(newTable,0,3)!="2032-03-14T08:00:07.000000Z")
            throw new Exception("New-definition review resolves command paths and retains exact fractional rates without invented contributors.");

        Refuse(()=>GasTransportCapacityPresentation.Tables(assessment with{Intervals=[Interval("route",null,Id(99),[],"0","100",true)]},facts,null));
        Refuse(()=>GasTransportCapacityPresentation.Tables(assessment with{Intervals=[Interval("agreement",null,null,[Id(99)],"1","100",true)]},facts,null));
        var orphan=JsonNode.Parse(facts.GetRawText())!.AsObject();orphan["claims"]![0]!["plan_id"]=Id(99);
        Refuse(()=>GasTransportCapacityPresentation.Tables(assessment,Json(orphan),null));
        var conflict=Json(new{kind="define",paths=new[]{new{pathId=Id(10),pathKey="different_path_reference"}}});
        Refuse(()=>GasTransportCapacityPresentation.Tables(assessment,facts,conflict));
        var falseOwner=Json(new{kind="plan",businessKey="different_plan_reference",claims=new[]{new{claimId=Id(22)}}});
        Refuse(()=>GasTransportCapacityPresentation.Tables(assessment,facts,falseOwner));
        Console.WriteLine("PASS Transport capacity presentation: immutable path/plan labels, proposed memberships, exact unchanged values and no unresolved UUID fallback");
    }
}
