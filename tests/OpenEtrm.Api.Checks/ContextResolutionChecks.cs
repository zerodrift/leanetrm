using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;
using System.Text.Json;

internal static class ContextResolutionChecks
{
    internal static void Run()
    {
        // The Pacific session's Friday close occurs on Saturday in UTC. A derived
        // pricing frame must retain Friday after dropping recorded-close identity.
        var at=DateTimeOffset.Parse("2020-12-19T00:00:00Z");
        var day=new DateOnly(2020,12,18);
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic Pacific close",at,day,[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty)
        {OriginCloseId=Guid.NewGuid(),Close=new("pacific_close",1,day,null)};
        var source=frame.ForMarketSource([],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,[]);
        if(source.Close is not null||source.OriginCloseId is not null)
            throw new Exception("A source frame must not impersonate a recorded close.");
        if(PreviewSession.BusinessDay(source)!=day)
            throw new Exception("A source frame lost the certified session business date when UTC is on the next day.");
        var working=BusinessTime.UtcSnapshot(at);
        if(working.Date!=new DateOnly(2020,12,19))throw new Exception("A UTC working snapshot has its explicitly declared UTC date.");
        source.Time.RequireDate(day);
        Refused(()=>source.Time.RequireDate(day.AddDays(1)));
        Refused(()=>_=(frame with{BusinessDate=day.AddDays(1)}).Time);
        Refused(()=>_=new BusinessTime(day,at.ToOffset(TimeSpan.FromHours(-8))));
        var context=new ScheduleContext("2020-12-18",1,"2026-11-11T00:00:00Z",Guid.NewGuid());
        var selected=source with{ContextId=context.ContextId};
        var historical=PinnedContext.Retained(context,selected);
        // A history sample is read under the selected cutoff, while retaining
        // its own prior business date; it is not a new selected valuation root.
        _=historical.Require(selected with{BusinessDate=day.AddDays(-1),At=at.AddDays(-1)});
        var wire=JsonSerializer.SerializeToNode(source,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        wire.Remove("businessDate");
        try
        {
            _=wire.Deserialize<InputFrame>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            throw new Exception("A retained input frame silently defaulted its missing business date.");
        }
        catch(JsonException){}
        var fixture=PreviewDataChecks.Fixture(1);
        var finalFields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(fixture.Observations[0].GetRawText())!;
        finalFields["role"]=JsonSerializer.SerializeToElement("final_contract_settlement");
        finalFields["observed_at"]=JsonSerializer.SerializeToElement("2040-01-20T16:00:00Z");
        var final=JsonSerializer.SerializeToElement(finalFields);
        var definitions=new Dictionary<string,ImmutableArray<JsonElement>>(fixture.Definitions)
        {["curve_expiry_rules"]=[JsonSerializer.SerializeToElement(new{curve_code="future",rule_version="freeze_final_settlement_v1"})]};
        var market=new PreviewData(fixture.Manifest,definitions,fixture.References,[final]);
        var instant=DateTimeOffset.Parse("2040-01-21T16:00:00Z");
        var unrelated=Guid.NewGuid();
        var root=fixture.Frame with{At=instant,BusinessDate=new(2040,1,21),ObservationIds=[unrelated],FrozenObservationIds=[unrelated]};
        var secondary=root.ForMarketSource([final.Id("id")],fixture.Frame.CurveContracts,[final.Id("id")]);
        var snapshot=market.Snapshot(secondary);
        if(!secondary.FrozenObservationIds.SequenceEqual([final.Id("id")])||root.FrozenObservationIds[0]!=unrelated||
            snapshot.Selections.Single().Input.Use!=InputUse.FrozenFact||market.Model.QuoteFactors(snapshot).Length!=0||
            market.Build(secondary,"future").Values.Single().Price.Value!=final.Number("value"))
            throw new Exception("A secondary expired contract lost its own final settlement or acquired shock exposure from the root frame.");
        var correctionFields=new Dictionary<string,JsonElement>(finalFields)
        {["id"]=JsonSerializer.SerializeToElement(Guid.NewGuid()),["corrects_id"]=JsonSerializer.SerializeToElement(final.Id("id"))};
        var correction=JsonSerializer.SerializeToElement(correctionFields);
        var revised=new PreviewData(fixture.Manifest,definitions,fixture.References,[final,correction]);
        if(!revised.SelectFrozen([correction.Id("id")],[final.Id("id")]).SequenceEqual([correction.Id("id")]))
            throw new Exception("An explicitly selected final-fact revision lost its declared frozen input identity.");
        var owner=new CatalogueSource(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var prior=Guid.NewGuid();var next=Guid.NewGuid();
        var part=new MarketSourceInputs(owner,[prior],[prior],fixture.Frame.CurveContracts,["future"]);
        var matrix=root with{Source=owner,ObservationIds=[prior],FrozenObservationIds=[prior],
            SourceInputs=ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(owner.DefinitionRevisionId,part)};
        var changed=matrix.SelectRevision(prior,next,"Revised inputs");
        if(changed.SourceInputs?[owner.DefinitionRevisionId] is not {} changedPart||!changedPart.Matches(changed)||
            !changedPart.FrozenObservationIds.SequenceEqual([next]))
            throw new Exception("Changing a primary pin left a stale canonical source matrix.");
        var changedSecondary=matrix.SelectSwapMarketRevision(owner.DefinitionRevisionId,[prior],prior,next,"Revised source");
        if(changedSecondary.SourceInputs?[owner.DefinitionRevisionId] is not {} changedSource||
            !changedSource.ObservationIds.SequenceEqual([next])||!changedSource.FrozenObservationIds.SequenceEqual([next])||!changedSource.Matches(changedSecondary))
            throw new Exception("Changing a secondary pin left stale source inputs or lost final-fact use.");
        var secondaryOwner=owner with{DefinitionRevisionId=Guid.NewGuid()};
        var multiple=matrix with{SourceInputs=matrix.SourceInputs!.Add(secondaryOwner.DefinitionRevisionId,part with{Source=secondaryOwner})};
        var secondaryChanged=multiple.SelectSwapMarketRevision(secondaryOwner.DefinitionRevisionId,[prior],prior,next,"Revised secondary");
        if(!secondaryChanged.ObservationIds.SequenceEqual([prior])||!secondaryChanged.FrozenObservationIds.SequenceEqual([prior])||
            !secondaryChanged.SourceInputs![owner.DefinitionRevisionId].Matches(secondaryChanged)||
            !secondaryChanged.SourceInputs[secondaryOwner.DefinitionRevisionId].FrozenObservationIds.SequenceEqual([next]))
            throw new Exception("Revising another source modified primary pins or lost its own frozen fact.");
        if(!part.Matches(matrix with{ObservationIds=[prior]}))throw new Exception("Exact source projection was rejected.");
        var retained=RetainedFrames.Read(RetainedFrames.Record(matrix));
        if(retained.Source!=matrix.Source||retained.SourceInputs?.GetValueOrDefault(owner.DefinitionRevisionId) is not {} saved||
            saved.Source!=owner||!saved.Matches(matrix))
            throw new Exception("Retained execution lost exact source coordinates or the canonical input matrix.");
        Console.WriteLine("PASS context resolution: derived frames retain the business date independently of recorded-close identity");
    }
    private static void Refused(Action action)
    {
        try{action();}
        catch(TemporalException e)when(e.Code=="temporal_context"){return;}
        throw new Exception("An inconsistent temporal coordinate was accepted.");
    }
}
