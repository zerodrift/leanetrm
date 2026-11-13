using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class CurveDisplayChecks
{
    public static void Run()
    {
        var revision=new DefinitionRevision(Guid.Parse("40000000-0000-0000-0000-000000000001"),Guid.Parse("60000000-0000-0000-0000-000000000001"));
        var at=DateTimeOffset.Parse("2020-12-21T21:00:00Z");
        MarketContract Contract(int ordinal,ContractKind kind,DateTimeOffset expiry,DateOnly from,DateOnly to)=>
            new(new(revision,Guid.Parse($"50000000-0000-0000-0000-{ordinal:000000000000}")),kind,from,to,expiry,null);
        ImmutableArray<MarketContract> contracts=[
            Contract(1,ContractKind.Future,at.AddHours(-1),new(2021,1,1),new(2021,2,1)),
            Contract(2,ContractKind.Forward,at,new(2020,12,20),new(2020,12,21)),
            Contract(3,ContractKind.Forward,at.AddHours(1),new(2020,12,21),new(2020,12,22)),
            Contract(4,ContractKind.FutureOption,at.AddDays(1),new(2021,1,1),new(2021,2,1))];
        var windows=contracts.Select(c=>new ContractOperationWindow(c.Key,ContractOperation.LiveCurveOutput,at.AddYears(-1),c.Expiry,ContractOperationWindow.Version)).ToImmutableArray();
        var live=CurveDisplay.Select(windows,at,new(CurveDisplay.Version,CurveDisplay.Live));
        if(!live.Select(c=>c.Contract).SequenceEqual(contracts.Skip(2).Select(c=>c.Key)))
            throw new Exception("The live view must use exact expiry instants, including equality, independently of delivery month and tenor.");
        var all=CurveDisplay.Select(windows,at,new(CurveDisplay.Version,CurveDisplay.All));
        if(!all.SequenceEqual(windows))throw new Exception("The all-contract view must preserve captured order and identities.");
        if(!CurveDisplay.Expired(contracts[0],at)||!CurveDisplay.Expired(contracts[1],at)||CurveDisplay.Expired(contracts[2],at))
            throw new Exception("Expiry status must be evaluated at the selected close, not the computer clock or delivery date.");
        foreach(var selection in new[]{new CurveDisplaySelection("unknown",CurveDisplay.Live),new CurveDisplaySelection(CurveDisplay.Version,"guess")})
        {
            try{CurveDisplay.Select(windows,at,selection);}
            catch(MarketDataException error)when(error.Code=="curve_display_policy"){continue;}
            throw new Exception("Unknown curve-display contracts must be refused.");
        }
        if(contracts.Length!=4)throw new Exception("A display selection must not mutate the retained universe.");
        CapturedCurve();
        Console.WriteLine("PASS curve display: exact selected-time eligibility, retained roots, live output pricing, inactive status and date qualification");
    }

    static void CapturedCurve()
    {
        var fixture=PreviewDataChecks.Fixture(2);
        static JsonElement Change(JsonElement row,string field,string value)
        {
            var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(row.GetRawText())!;
            fields[field]=JsonSerializer.SerializeToElement(value);return JsonSerializer.SerializeToElement(fields);
        }
        var terms=new Dictionary<string,ImmutableArray<JsonElement>>(fixture.Definitions){
            ["contracts"]=[Change(fixture.Definitions["contracts"][0],"expiry_at","2040-01-05T00:00:00Z"),fixture.Definitions["contracts"][1]]};
        var references=new Dictionary<string,ImmutableArray<JsonElement>>(fixture.References){
            ["currencies"]=[JsonSerializer.SerializeToElement(new{code="usd",display_code="USD"})],
            ["units"]=[JsonSerializer.SerializeToElement(new{code="mwh",symbol="MWh"})],
            ["futures_milestones"]=[..fixture.References["futures_milestones"].Select(row=>
                row.Text("series_code")=="synthetic_0"&&row.Text("milestone")=="last_trading"?Change(row,"event_at","2040-01-05T00:00:00Z"):row)]};
        var observations=ImmutableArray.Create(Change(fixture.Observations[0],"observed_at","2040-01-04T00:00:00Z"),fixture.Observations[1]);
        var data=new PreviewData(fixture.Manifest,terms,references,observations);
        var captured=data.Requests(fixture.Frame,"future");
        var selection=data.Model.SelectLiveOutputs(fixture.Frame.At,captured,CurveModel.LiveOutputsPolicy);
        if(!selection.Selected.SequenceEqual(captured.Skip(1))||selection.Excluded.Length!=1||selection.Excluded[0].Request!=captured[0])
            throw new Exception("The shared domain selector must retain exact candidate identities and explain excluded output roots.");
        try{data.Build(fixture.Frame,"future");throw new Exception("The strict captured request must retain its expired root.");}
        catch(MarketDataException e)when(e.Code=="source_expired"){}
        var exactShape=MarketReads.Shape(data,data.Snapshot(fixture.Frame),fixture.Frame,"future");
        var exactWire=JsonSerializer.SerializeToElement(exactShape,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if(exactWire.TryGetProperty("contractDisplay",out _))
            throw new Exception("An exact shape without a display selection must preserve its existing wire contract.");
        var live=CurveDisplay.Shape(data,data.Snapshot(fixture.Frame),fixture.Frame,"future",new(CurveDisplay.Version,CurveDisplay.Live));
        var all=CurveDisplay.Shape(data,data.Snapshot(fixture.Frame),fixture.Frame,"future",new(CurveDisplay.Version,CurveDisplay.All));
        if(live.Points?.Length!=1||all.Points?.Length!=2||all.Points.Value[0] is not{Price:null,UnavailableReason:"eligibility_ended"}||
            live.Front?.ContractId!=captured[1].Contract.Id||all.Front!=live.Front||live.ContractDisplay?.EligibleCount!=1||all.ContractDisplay?.CapturedCount!=2)
            throw new Exception("Inactive visibility must not alter eligible output prices or silently value the expired root.");
        var incomplete=fixture.Frame with{ObservationIds=fixture.Frame.ObservationIds.Remove(observations[1].Id("id"))};
        foreach(var display in new[]{CurveDisplay.Live,CurveDisplay.All})
        {
            try{CurveDisplay.Result(data,incomplete,"future",new(CurveDisplay.Version,display));}
            catch(MarketDataException e)when(e.Code=="missing_observation"){continue;}
            throw new Exception("A live curve evaluation must refuse a required missing input, rather than return a successful empty price result.");
        }
        var unavailable=CurveDisplay.Shape(data,data.Snapshot(incomplete),incomplete,"future",new(CurveDisplay.Version,CurveDisplay.Live));
        if(unavailable.Front is not null||unavailable.Points is not[{Price:null,UnavailableReason:"missing_observation"}])
            throw new Exception("The browsing shape must retain the missing-input reason without fabricating a price.");
        MarketDates.QualifyCurve(data,fixture.Frame,"future",ImmutableDictionary<string,RateTenorSource>.Empty);
        var liveCapture=MarketCloses.LivePart(data,fixture.Frame,["future"]);
        if(!liveCapture.CurveContracts["future"].SequenceEqual([captured[1].Contract.Id])||
            !liveCapture.ObservationIds.SequenceEqual(fixture.Frame.ObservationIds)||
            !liveCapture.FrozenObservationIds.SequenceEqual(fixture.Frame.FrozenObservationIds.IsDefault?[]:fixture.Frame.FrozenObservationIds))
            throw new Exception("Fresh live capture must trim only output roots and retain every selected source fact.");
        var retained=MarketCloses.LivePart(data,fixture.Frame,[]);
        if(!retained.CurveContracts["future"].SequenceEqual(fixture.Frame.CurveContracts["future"]))
            throw new Exception("A retained source's output window must not be trimmed as an independent root.");
        MarketCloses.QualifyPart(data,fixture.Frame,[]);
        try{MarketCloses.QualifyPart(data,fixture.Frame,default);throw new Exception("An omitted root declaration must not become an empty retained source.");}
        catch(MarketDataException e)when(e.Code=="pilot_close_inputs"){}
        var source=CatalogueSource.From(data);
        var fresh=MarketCloses.FreshFrame(fixture.Frame with{OriginCloseId=Guid.NewGuid(),OriginInputId=Guid.NewGuid(),Close=new("synthetic",1,fixture.Frame.BusinessDate,null)},
            Guid.NewGuid(),ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(source.DefinitionRevisionId,liveCapture),source);
        if(fresh.OriginCloseId is not null||fresh.OriginInputId is not null||fresh.Close is not null||fresh.Id==fixture.Frame.Id)
            throw new Exception("A fresh capture must not impersonate its original recorded selection.");
        MarketCloses.QualifyPart(data,fresh,["future"]);
        try{MarketCloses.LivePart(data,fixture.Frame with{At=DateTimeOffset.Parse("2041-01-01T00:00:00Z")},["future"]);
            throw new Exception("A required empty root must refuse capture rather than roll or disappear.");}
        catch(MarketDataException e)when(e.Code=="empty_live_curve"){}
        var wire=JsonSerializer.SerializeToElement(CurveDisplay.Result(data,fixture.Frame,"future",new(CurveDisplay.Version,CurveDisplay.All)));
        if(wire.GetProperty("points").GetArrayLength()!=1||wire.GetProperty("contractDisplay").GetProperty("contracts").GetArrayLength()!=2)
            throw new Exception("Evaluation must price the eligible output and retain inactive metadata separately.");
        if(!captured.SequenceEqual(data.Requests(fixture.Frame,"future")))throw new Exception("Live inspection changed the retained root set.");
        try{CurveDisplay.Shape(data,data.Snapshot(fixture.Frame) with{ValuationAt=fixture.Frame.At.AddDays(1)},fixture.Frame,"future",new(CurveDisplay.Version,CurveDisplay.Live));}
        catch(MarketDataException e)when(e.Code=="curve_display_context"){return;}
        throw new Exception("A different calculation instant must not impersonate the captured view.");
    }
}
