using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;

internal static class BookedProgramStorageChecks
{
    internal static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned disposable preview runner.");
        await using var database=new PreviewDatabase(root);
        using var fixture=JsonDocument.Parse(File.ReadAllText(path));
        var entries=fixture.RootElement.GetProperty("instruments").EnumerateArray().ToArray();
        if(entries.Length<2)throw new Exception("Direct loading requires independently booked instruments and shared sides.");
        var families=new HashSet<string>(StringComparer.Ordinal);
        await database.WithIdentity("trader","ReadTrades",async(connection,ct)=>
        {
            var store=new RetainedScheduleStore(database);
            foreach(var entry in entries)
            {
                var context=entry.GetProperty("contextId").GetGuid();
                var at=entry.GetProperty("asOf").GetDateTimeOffset();
                var programs=await store.LoadPrograms(connection,entry.GetProperty("instrumentId").GetGuid(),context,at,ct);
                var expected=entry.GetProperty("sides").EnumerateArray().ToArray();
                if(programs.Length!=expected.Length)throw new Exception("A direct read lost an instrument membership.");
                for(var slot=0;slot<programs.Length;slot++)
                {
                    var loaded=programs[slot];var wanted=expected[slot];var document=loaded.Program.Document;
                    if(loaded.Slot!=slot||loaded.SideId!=wanted.GetProperty("sideId").GetGuid()||loaded.ScheduleId!=wanted.GetProperty("scheduleId").GetGuid()||
                        loaded.Identity.ContentDigest!=wanted.GetProperty("digest").GetString()||
                        loaded.Identity.ScheduleManifest!=document.ScheduleManifest)
                        throw new Exception("Direct loading must retain exact published bytes, side membership and operation addresses.");
                    families.Add(document.Family);
                    if(entry.TryGetProperty("minimumPayments",out var payments)&&
                        document.Instructions.Count(instruction=>instruction.Output.Operation=="output_guard")<payments.GetInt32())
                        throw new Exception("A directly loaded whole-term Side must retain all independently dated payments.");
                    if(entry.TryGetProperty("fixedUnitPv",out var pv)&&document.Family=="fixed")
                    {
                        var selected=new ResolvedCalculationContext(context,document.ScheduleManifest,at,
                            entry.GetProperty("knownAt").GetDateTimeOffset(),at,at);
                        var demand=loaded.Program.Inputs(selected);
                        var columns=new Dictionary<ScheduleInputAddress,ScheduleInputValue<ScheduleValueColumn>>();
                        foreach(var source in demand.Sources)
                        {
                            var row=document.Instructions[source.Instruction].Output;
                            if(row.Operation!="discount"||source.Uses.Length!=1)throw new Exception("The independent fixed fixture requires only one explicit discount input.");
                            columns.Add(new(row.Ordinal,"base"),new(selected,"base",ScheduleValueColumn.Input(row,[ScheduleNumber.Parse("1")],[default])));
                        }
                        if(loaded.Program.Execute(demand,"base","base",columns,ct).PresentValue.ToExactString()!=pv.GetString())
                            throw new Exception("Stored fixed schedule must compute its independently specified exact unit PV.");
                    }
                }
            }
            return true;
        },CancellationToken.None);
        if(fixture.RootElement.GetProperty("requiredFamilies").EnumerateArray().Any(value=>!families.Contains(value.GetString()!)))
            throw new Exception("The direct-read fixture must cover every declared dated/native Side family.");
        Console.WriteLine("PASS booked program direct loading: published bytes and exact fixed PV with all normalized-content reads disabled");
    }
}
