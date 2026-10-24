using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class MarketCloseRegisterChecks
{
    public static async Task Run(PreviewDatabase database,InputFrame actualFrame,Guid revisionId)
    {
        await using var runtime=NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_RUNTIME")!);
        await using var selectedDatabase=new PreviewDatabase(database.Manifest with{RevisionId=revisionId},runtime);
        await database.WithIdentity("approver","PublishMarketData",async(connection,ct)=>
        {
            var data=await selectedDatabase.Read(connection,ct,observationIds:actualFrame.ObservationIds,
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
            var request=new MarketCloseRegister(Guid.NewGuid(),selected.Id,"Synthetic indexed close");
            var close=await MarketCloses.Register(selectedDatabase,connection,PinnedContext.Current(selected),selected,request,ct);
            var retained=await MarketCloses.Frame(connection,database.Manifest.CatalogId,close.Id,null,ct);
            if(!actuals.All(pin=>retained.FrozenObservationIds.Contains(pin.Observation.Key.Id)))
                throw new Exception("Registering an actual index fixing lost the authorized snapshot's frozen use.");
            var pins=await PreviewDatabase.Rows(connection,
                "SELECT row_to_json(p)::text FROM market_data.market_close_pins p WHERE catalog_id=$1 AND close_id=$2",
                [database.Manifest.CatalogId,close.Id],ct);
            var expected=snapshot.Selections.Select(pin=>(pin.Input.Series.Id,pin.Observation.Key.Id,
                pin.Input.Use==InputUse.FrozenFact?"frozen":"variable")).ToHashSet();
            if(!pins.Select(pin=>(pin.Id("series_id"),pin.Id("observation_id"),pin.Text("input_use"))).ToHashSet().SetEquals(expected)||
                retained.OriginCloseId!=close.Id||retained.At!=selected.At||!retained.CurveContracts["daily_gas"].SequenceEqual(members))
                throw new Exception("Close registration changed exact native pins, their uses or explicit membership.");
            Console.WriteLine("PASS indexed close registration: snapshot-derived frozen fixings and exact native pins/membership at "+selected.At.ToString("O"));
            return true;
        },CancellationToken.None);
    }
}
