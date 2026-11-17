using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class OperationalBusinessContextChecks
{
    internal static void Run()
    {
        var current=new FacilityQuantityContext("2032-03-14","current",null,FacilityQuantityStore.Convention);
        var selected=OperationalBusinessContext.Select(current,null,"facility_quantity_input");
        if(!selected.Working||selected.At.ToString("O")!="2032-03-14T23:59:59.9999990+00:00")
            throw new Exception("The explicit UTC business-date read clock changed with the operating day's local DST length.");
        var retained=new ScheduleContext("2032-03-14",10,"2032-03-15T03:00:00Z",Guid.Parse("72222222-2222-2222-2222-222222222222"));
        var request=current with{KnowledgeMode="retained",Retained=retained};
        if(OperationalBusinessContext.Select(request,retained,"facility_quantity_input").Working)
            throw new Exception("An exact historical selection became writable.");
        Reject(()=>OperationalBusinessContext.Select(current,retained,"facility_quantity_input"),"temporal_context");
        Reject(()=>OperationalBusinessContext.Select(request,null,"facility_quantity_input"),"temporal_context");
        Reject(()=>OperationalBusinessContext.Select(request,retained with{ContextId=Guid.NewGuid()},"facility_quantity_input"),"temporal_context");
        Reject(()=>OperationalBusinessContext.Select(current with{Convention="assume_local_gas_day"},null,"facility_quantity_input"),"facility_quantity_input");
        Reject(()=>OperationalBusinessContext.Select(current with{KnowledgeMode="latest"},null,"facility_quantity_input"),"temporal_context");
        Console.WriteLine("PASS operational business context: explicit clock, exact historical selection and no writable current fallback");
    }
    static void Reject(Action work,string code)
    {try{work();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
