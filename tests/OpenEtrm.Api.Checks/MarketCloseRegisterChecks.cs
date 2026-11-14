using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class MarketCloseRegisterChecks
{
    public static async Task Run(PreviewDatabase database,InputFrame actualFrame,Guid revisionId)
    {
        // D334: the close is recorded for a session over its exact bundle, at the frame's instant, by a granted recorder.
        var cutoff=actualFrame.At.UtcDateTime;
        var day=DateOnly.FromDateTime(cutoff);
        var draft=new CloseSessionDraft("test_indexed_close","Synthetic indexed close","test_utc_every_day",cutoff.ToString("HH:mm",System.Globalization.CultureInfo.InvariantCulture),
            ["daily_gas"],day.AddDays(-1),day.AddDays(1));
        var saved=await database.WithIdentity("editor","WriteMarketDefinitions",(connection,ct)=>CloseSessions.Save(connection,database,new(Guid.NewGuid(),draft),ct),CancellationToken.None);
        await database.WithIdentity("approver","SealMarketDefinitions",(connection,ct)=>CloseSessions.Publish(connection,database.Manifest.CatalogId,
            new(Guid.NewGuid(),draft.Code,saved.GetProperty("version").GetInt32(),saved.GetProperty("generation").GetInt32()),ct),CancellationToken.None);
        await database.WithIdentity("admin","ManageAccess",async(connection,ct)=>
        {
            await using var directory=ScheduleStore.Command(connection,"SELECT security.administration_directory()::text");
            using var members=System.Text.Json.JsonDocument.Parse((string)(await directory.ExecuteScalarAsync(ct))!);
            var approver=members.RootElement.GetProperty("members").EnumerateArray().Single(m=>m.GetProperty("login").GetString()=="approver").GetProperty("id").GetGuid();
            return await CloseSessions.Grant(connection,database.Manifest.CatalogId,new(Guid.NewGuid(),approver,draft.Code,DateTimeOffset.UnixEpoch,new(9999,1,1,0,0,0,TimeSpan.Zero)),ct);
        },CancellationToken.None,admission:Admission.Administration);
        await database.WithIdentity("approver","PublishMarketData",async(connection,ct)=>
        {
            var source=await MarketInputSelection.ExactSource(database,connection,revisionId,ct);
            var data=await database.Read(connection,ct,ppaBundle:source,observationIds:actualFrame.ObservationIds,
                observationScope:ObservationReadScope.ExactPins);
            // The fixture's daily gas curve has actual fixings and live daily
            // forwards. Register only that explicitly selected live view; no
            // expired monthly futures or native observations are rewritten.
            var members=data.DefinitionRows("curve_members","curve_code","daily_gas").OrderBy(row=>row.Number("ordinal"))
                .Select(row=>row.Id("contract_id")).Where(id=>data.ContractRows(id).Single().Expiry>actualFrame.At).ToImmutableArray();
            if(members.IsEmpty)throw new Exception("The registration fixture requires live daily gas contracts.");
            var selected=actualFrame with{ContextId=null,FrozenObservationIds=[],
                CurveContracts=ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("daily_gas",members)};
            var snapshot=data.Snapshot(selected);
            var actuals=snapshot.Selections.Where(pin=>pin.Observation.Role==ObservationRole.IndexFixing).ToImmutableArray();
            if(actuals.IsEmpty||actuals.Any(pin=>pin.Input.Use!=InputUse.FrozenFact))
                throw new Exception("The registration fixture must have actual index fixings without explicit frame markers.");
            var primary=(await new CatalogueStore(database.Manifest.CatalogId).ReadBound(connection,ct,day)).RequireRole("primary");
            var seed=database.Manifest.Frames.Single(f=>f.At==selected.At&&f.PpaBundle is null);
            var anchor=new MarketSourceInputs(primary,[..seed.ObservationIds],[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,[]);
            var daily=new MarketSourceInputs(source,selected.ObservationIds,selected.FrozenObservationIds,selected.CurveContracts,["daily_gas"]);
            var frame=anchor.Project(selected) with
            {
                SourceInputs=new[]{anchor,daily}.ToImmutableDictionary(part=>part.Source.DefinitionRevisionId)
            };
            var request=new MarketCloseRegister(Guid.NewGuid(),frame.Id,"Synthetic indexed close","test_indexed_close",day);
            var close=await MarketCloses.Register(database,connection,PinnedContext.Current(frame),_=>frame,request,ct);
            var replay=await MarketCloses.Register(database,connection,PinnedContext.Current(frame),
                _=>throw new Exception("A command replay must not resolve an ephemeral source frame."),request,ct);
            if(replay!=close)throw new Exception("Exact capture must replay its first command outcome.");
            var retained=await MarketCloses.Frame(connection,database.Manifest.CatalogId,close.Id,null,ct);
            var retainedDaily=retained.SourceInputs![revisionId];
            if(!actuals.All(pin=>retainedDaily.FrozenObservationIds.Contains(pin.Observation.Key.Id)))
                throw new Exception("Registering an actual index fixing lost the authorized snapshot's frozen use.");
            var pins=await PreviewDatabase.Rows(connection,
                "SELECT row_to_json(p)::text FROM market_data.market_close_pins p WHERE catalog_id=$1 AND close_id=$2 AND definition_revision_id=$3",
                [database.Manifest.CatalogId,close.Id,revisionId],ct);
            var expected=snapshot.Selections.Select(pin=>(pin.Input.Series.Id,pin.Observation.Key.Id,
                pin.Input.Use==InputUse.FrozenFact?"frozen":"variable")).ToHashSet();
            if(!pins.Select(pin=>(pin.Id("series_id"),pin.Id("observation_id"),pin.Text("input_use"))).ToHashSet().SetEquals(expected)||
                retained.OriginCloseId!=close.Id||retained.At!=frame.At||retained.Source!=primary||retained.SourceInputs.Count!=2||
                !retained.ObservationIds.ToHashSet().SetEquals(anchor.ObservationIds)||!retained.CurveContracts.IsEmpty||
                !retainedDaily.RootCurves.SequenceEqual(["daily_gas"])||!retainedDaily.CurveContracts["daily_gas"].SequenceEqual(members))
                throw new Exception("Close registration changed exact native pins, their uses or explicit membership.");
            Console.WriteLine("PASS indexed close registration: snapshot-derived frozen fixings and exact native pins/membership at "+selected.At.ToString("O"));
            return true;
        },CancellationToken.None);
    }
}
