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
        foreach(var entry in entries)
        await database.WithIdentity("trader","ReadTrades",async(connection,ct)=>
        {
            var store=new RetainedScheduleStore(database);
            {
                var context=entry.GetProperty("contextId").GetGuid();
                var at=entry.GetProperty("asOf").GetDateTimeOffset();
                var selectedContext=(await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,context,ct)).Context;
                await PinnedContext.Retained(selectedContext).Bind(connection,database.Manifest.CatalogId,ct);
                var retained=await store.LoadInstrumentPrograms(connection,entry.GetProperty("instrumentId").GetGuid(),context,at,ct);
                var programs=retained.Members;
                if(entry.GetProperty("native").GetBoolean())
                {
                    var native=await store.LoadPpaInstrumentPrograms(connection,entry.GetProperty("instrumentId").GetGuid(),context,at,ct);
                    if(!native.Execution.Members.Select(member=>member.SideId).SequenceEqual(retained.Members.Select(member=>member.SideId))||
                        native.Members.Any(member=>member.Coefficient is not (-1 or 1))||native.Delivery.From>=native.Delivery.To||
                        native.Members.Any(member=>member.Normalization.Basis!=native.UnitQuantity))
                        throw new Exception("Native executable assembly must retain exact signed membership, delivery and normalization without Side terms.");
                    if(entry.TryGetProperty("expectedMetadata",out var wantedMetadata))
                    {
                        var delivery=wantedMetadata.GetProperty("delivery");
                        if(native.Currency.Code!=wantedMetadata.GetProperty("currency").GetString()||
                            native.UnitQuantity.Code!=wantedMetadata.GetProperty("unitQuantity").GetString()||
                            native.Discounting!=wantedMetadata.GetProperty("discounting").GetBoolean()||
                            native.Delivery.Clock.Code!=delivery.GetProperty("clock").GetString()||
                            native.Delivery.IntervalMinutes!=delivery.GetProperty("intervalMinutes").GetInt32()||
                            native.Delivery.From!=delivery.GetProperty("from").Deserialize<DateOnly>()||
                            native.Delivery.To!=delivery.GetProperty("to").Deserialize<DateOnly>()||
                            !native.Members.Select(member=>member.Coefficient).SequenceEqual(wantedMetadata.GetProperty("members").EnumerateArray().Select(member=>member.GetProperty("coefficient").GetInt32())))
                            throw new Exception("Executable PPA metadata must reproduce the independent booking inputs exactly.");
                    }
                }

                if(!entry.GetProperty("native").GetBoolean())
                {
                    var swap=await store.LoadSwapInstrumentPrograms(connection,entry.GetProperty("instrumentId").GetGuid(),context,at,ct);
                    var ratio=entry.TryGetProperty("secondRatio",out var expectedRatio)?expectedRatio.GetString():"1/1";
                    if(swap.Members.Length!=2||swap.Members[0].Coefficient!=-1||swap.Members[1].Coefficient!=1||
                        swap.Members[0].QuantityRatio!=OpenEtrm.Shared.Numerics.ExactRatio.One||
                        $"{swap.Members[1].QuantityRatio.Numerator}/{swap.Members[1].QuantityRatio.Denominator}"!=ratio||
                        !swap.Members.Select(member=>member.Program.SideId).SequenceEqual(programs.Select(member=>member.SideId)))
                        throw new Exception("Stored swap membership must preserve exact directions, native sizes and program identities.");
                    if(entry.TryGetProperty("factorValue",out var expectedFactor))
                    {
                        if(swap.Factor is not {} factor||factor.Value.ToString()!=expectedFactor.GetString()||swap.FactorId is null||
                            swap.Members[0].UnitQuantity.Code!=entry.GetProperty("firstUnit").GetString()||
                            swap.Members[1].UnitQuantity.Code!=entry.GetProperty("secondUnit").GetString()||
                            factor.DenominatorQuantity.Code!=entry.GetProperty("factorDenominator").GetString()||
                            factor.NumeratorQuantity.Code!=entry.GetProperty("factorNumerator").GetString())
                            throw new Exception("A stored factor swap must retain its explicit typed factor and different native units.");
                    }
                    else if(swap.Factor is not null||swap.FactorId is not null)throw new Exception("An ordinary swap must not infer a quantity factor.");
                }
                foreach(var member in programs)
                foreach(var instruction in member.Program.Document.Instructions.Where(instruction=>instruction.Bindings.Any(binding=>binding.Kind=="sibling_quantity")))
                {
                    var grid=member.Program.Document.Grids[instruction.Output.Grid!.Value];
                    var connections=retained.Connections.Where(link=>link.InputSide==member.SideId&&link.InputInstruction==instruction.Output.Ordinal).ToArray();
                    if(!connections.SelectMany(link=>link.InputCells).Order().SequenceEqual(Enumerable.Range(0,grid.CellCount)))
                        throw new Exception("The direct loader must retain every input cell exactly once without rebuilding the schedule.");
                    foreach(var link in connections)
                    {
                        var source=programs.Single(program=>program.SideId==link.OutputSide).Program.Document;
                        var root=source.Roots.Single(root=>root.Name==link.Output&&root.Measure=="schedule_quantity");
                        var row=source.Instructions[root.Row].Output;var sourceGrid=source.Grids[row.Grid!.Value];
                        if(row.Unit!=instruction.Output.Unit||grid.Clock!=sourceGrid.Clock||link.InputCells.Length!=link.OutputCells.Length||
                            link.InputCells.Where((cell,n)=>grid.Interval(cell)!=sourceGrid.Interval(link.OutputCells[n])).Any())
                            throw new Exception("Saved instrument mappings must preserve independently inspected exact units and cell geometry.");
                    }
                }
                if(retained.Connections.Any(link=>!programs.Any(member=>member.SideId==link.InputSide)||!programs.Any(member=>member.SideId==link.OutputSide)))
                    throw new Exception("A connection cannot escape its exact instrument membership.");
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
                    if(document.Instructions.Any(instruction=>instruction.Output is{Operation:"price_lookup",Grid:null}))
                    {
                        var revision=document.Instructions.SelectMany(instruction=>instruction.Bindings).First(binding=>binding.Port=="market").Market!.Value.Curve.Revision;
                        var reference=document.Instructions[document.Roots.Single(root=>root.Name=="pv").Row].Output.Currency!.Value.Revision;
                        var data=await database.Read(connection,ct,observationIds:[],
                            ppaBundle:new(revision.RevisionId,reference.CatalogId,reference.RevisionId));
                        var lookups=BookedScheduleLookups.Dated(data,loaded.Program);
                        if(lookups.IsEmpty||lookups.Any(lookup=>lookup.Contract.Kind!=ContractKind.Forward))
                            throw new Exception("Direct dated sources must resolve exact forward metadata without loading Side terms.");
                        foreach(var lookup in lookups.Where(value=>value.Fixing is not null))
                        {
                            var fixing=lookup.Fixing!;
                            var definition=data.Definitions["index_fixing_observations"].Single(row=>row.Text("index_code")==lookup.Index.Code&&
                                row.Id("series_id")==fixing.Series.Id&&row.Text("source_code")==fixing.Source.Code);
                            if(fixing.FixingAt!=definition.Instant("fixing_at"))
                                throw new Exception("A stored lookup must use its exact qualified publication timestamp.");
                        }
                    }
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
