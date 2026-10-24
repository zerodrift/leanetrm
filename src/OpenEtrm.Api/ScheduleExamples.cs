using System.Collections.Immutable;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public sealed record MonthlyScheduleExample(MonthlyDraft Definition,string Selection,string EffectiveFrom,string EffectiveTo,string ContextDate,string Expression);
public sealed record ContractExampleChoice(string Code,string Name,string Description,bool RequiresSchedule);
public sealed record ContractExampleRequest(string? Code,string? Schedule,ScheduleContext? Context);
public sealed record ContractDateExample(PublishedContractRequest Definition,string Description);

// Explicit illustrative terms for the example loader. These are not exchange
// rules or admitted instrument definitions; normal preview/save validation applies.
public static class ScheduleExamples
{
    public static MonthlyScheduleExample MonthlyGas => new(
        new("Synthetic gas delivery months","oe.sample_gas_months","2020-12","2021-07","day",1,"reject","unadjusted",null),
        "context_month","2020-12-01","2021-07-01","2021-01-15","oe.sample_gas_months[0] - 3bd");
    public static ImmutableArray<ContractExampleChoice> ContractChoices => [
        new("monthly_gas","Monthly gas","Two monthly candidate contracts referencing the selected published schedule. Illustrative three-business-day offset and 15:30 UTC cutoff; no exchange rules.",true),
        new("daily_power","Daily power · exact dates","Two daily candidate contracts with fixed date literals and 16:00 UTC cutoffs. This does not create a daily schedule generator or publish a curve.",false)];
    public static object Catalog => new {monthly=MonthlyGas,contracts=ContractChoices};

    public static ContractDateExample Contracts(ContractExampleRequest request,IEnumerable<ScheduleVersion> versions,ScheduleContext context)
    {
        _=PublishedSchedules.ContextDate(context);
        var choice=ContractChoices.SingleOrDefault(c=>c.Code==request.Code)??throw Error("schedule_example","Choose a supported contract-date example.");
        ContractFieldDraft[] contracts;
        if(choice.Code=="monthly_gas")
        {
            if(string.IsNullOrWhiteSpace(request.Schedule))throw Error("schedule_example","Choose an explicit published monthly schedule.");
            if(!PublishedSchedules.Catalog(versions,context).Any(v=>v.Symbol==request.Schedule))
                throw Error("schedule_unpublished","The selected schedule is not published in this context.");
            contracts=Enumerable.Range(0,2).Select(i=>new ContractFieldDraft($"Gas month {i+1}",$"{request.Schedule}[{i}]",$"{request.Schedule}[{i+1}]",$"{request.Schedule}[{i}] - 3bd","15:30")).ToArray();
        }
        else {
            if(request.Schedule is not null)throw Error("schedule_example","The literal daily example does not consume a schedule reference.");
            contracts=[new("Power day 1","2021-01-05","2021-01-06","2021-01-04","16:00"),new("Power day 2","2021-01-06","2021-01-07","2021-01-05","16:00")];
        }
        return new(new("sample_"+choice.Code,choice.Name,context,contracts),choice.Description);
    }
    private static TemporalException Error(string code,string message)=>new(code,message);
}
