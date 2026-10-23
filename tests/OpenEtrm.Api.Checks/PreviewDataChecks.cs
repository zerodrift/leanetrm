using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class PreviewDataChecks
{
    public static void Run()
    {
        var count=0;
        void Check(string name,Action test){test();count++;Console.WriteLine("PASS preview data: "+name);}
        void Reject(string code,Action test)
        {try{test();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
        var fixture=Fixture(405);
        PreviewData Data(ImmutableArray<JsonElement>? observations=null,Dictionary<string,ImmutableArray<JsonElement>>? references=null,
            Dictionary<string,ImmutableArray<JsonElement>>? definitions=null)=>new(fixture.Manifest,definitions??fixture.Definitions,references??fixture.References,observations??fixture.Observations);
        PreviewSession Seeded(InputFrame frame)
        {
            var seed=new PreviewSeedFrame(frame.At,frame.ObservationIds.ToArray(),frame.CurveContracts.ToDictionary(pair=>pair.Key,pair=>pair.Value.ToArray()));
            var session=new PreviewSession(fixture.Manifest with{Frames=[seed]});
            var id=session.InitialFrames.Single();session.Frames[id]=frame with{Id=id};return session;
        }
        var data=Data();
        Check("all independently keyed futures retain their exact prices and input identities",()=>
        {
            var result=data.Build(fixture.Frame,"future");
            if(data.FuturesSeries.Length!=405||result.Values.Length!=405||result.EvaluatedConstituents!=405)throw new Exception("Selected future coverage changed.");
            for(var i=0;i<405;i++)
            {
                var value=result.Values[i];var row=fixture.Observations[i];
                if(value.Request.Contract.Id!=fixture.Frame.CurveContracts["future"][i]||value.Price.Value!=row.Number("value")||
                    !value.Lineage.Select(key=>key.Id).SequenceEqual([row.Id("id")]))throw new Exception("A price or exact observation identity changed.");
            }
        });
        Check("input pins determine selection independently of loaded row order",()=>
        {
            var reversed=Data([..fixture.Observations.Reverse()]);
            var expected=data.Build(fixture.Frame,"future");var actual=reversed.Build(fixture.Frame,"future");
            if(!actual.Values.Select(v=>(v.Request,v.Price,v.Lineage.Single())).SequenceEqual(expected.Values.Select(v=>(v.Request,v.Price,v.Lineage.Single()))))
                throw new Exception("Loaded row order selected another price.");
        });
        Check("missing and duplicated exact observation pins remain errors",()=>
        {
            Reject("ambiguous_reference",()=>data.Observation(Guid.NewGuid()));
            Reject("ambiguous_reference",()=>Data([..fixture.Observations,fixture.Observations[0]]).Snapshot(fixture.Frame));
            Reject("ambiguous_reference",()=>Data(fixture.Observations.RemoveAt(0)).Snapshot(fixture.Frame));
        });
        Check("corrections are explicit and conflicting successors cannot be chosen",()=>
        {
            var original=fixture.Observations[0];var second=Change(original,"id",Guid.NewGuid());
            second=Change(Change(second,"corrects_id",original.Id("id")),"value","111.001");
            var third=Change(Change(Change(second,"id",Guid.NewGuid()),"corrects_id",second.Id("id")),"value","112.001");
            var corrected=Data([..fixture.Observations,second,third]);
            if(corrected.SelectPinned(fixture.Frame).ObservationIds[0]!=third.Id("id")||
                corrected.Build(fixture.Frame,"future").Values[0].Price.Value!=original.Number("value"))
                throw new Exception("An exact pin was silently replaced or its explicit successor was lost.");
            var branch=Change(second,"id",Guid.NewGuid());
            Reject("revision_conflict",()=>Data([..fixture.Observations,second,branch]).SelectPinned(fixture.Frame));
        });
        Check("frozen settlement identity and use survive latest variable correction selection",()=>
        {
            var final=Change(fixture.Observations[0],"role","final_contract_settlement");
            var correction=Change(Change(Change(final,"id",Guid.NewGuid()),"corrects_id",final.Id("id")),"value","101.001");
            var corrected=Data([..fixture.Observations.SetItem(0,final),correction]);
            var selected=corrected.SelectPinned(fixture.Frame with{FrozenObservationIds=[final.Id("id")]});
            var frozen=corrected.Snapshot(selected).Selections.Single(s=>s.Observation.Key.Id==final.Id("id"));
            if(selected.ObservationIds[0]!=final.Id("id")||frozen.Input.Use!=InputUse.FrozenFact||frozen.Observation.Value!=final.Number("value"))
                throw new Exception("A retained frozen settlement was corrected or became a variable market factor.");
        });
        Check("index actuals and explicitly retained final settlements keep independent frozen uses",()=>
        {
            var actual=Change(fixture.Observations[0],"role","index_fixing");
            var final=Change(fixture.Observations[1],"role","final_contract_settlement");
            var selected=Data(fixture.Observations.SetItem(0,actual).SetItem(1,final)).Snapshot(
                fixture.Frame with{FrozenObservationIds=[final.Id("id")]});
            if(selected.Selections.Single(s=>s.Observation.Key.Id==actual.Id("id")).Input.Use!=InputUse.FrozenFact||
                selected.Selections.Single(s=>s.Observation.Key.Id==final.Id("id")).Input.Use!=InputUse.FrozenFact||
                selected.Selections.Count(s=>s.Input.Use==InputUse.Variable)!=403)
                throw new Exception("Index actuals and retained final settlements must stay frozen without changing live inputs.");
        });
        Check("retained worker transport preserves frozen final facts and exact scenario identity",()=>
        {
            var final=Change(fixture.Observations[0],"role","final_contract_settlement");
            var selected=fixture.Frame with{ContextId=Guid.NewGuid(),OriginCloseId=Guid.NewGuid(),FrozenObservationIds=[final.Id("id")],ResolvesLatestCorrection=true};
            var retained=RiskJobs.ReadFrame(RiskJobs.PinFrame(selected));
            var frozenData=Data(fixture.Observations.SetItem(0,final));
            if(retained.ContextId!=selected.ContextId||retained.OriginCloseId!=selected.OriginCloseId||retained.ResolvesLatestCorrection||
                !frozenData.Snapshot(retained).Selections.SequenceEqual(frozenData.Snapshot(selected).Selections)||
                frozenData.Snapshot(retained).Selections[0].Input.Use!=InputUse.FrozenFact)
                throw new Exception("Worker transport changed retained facts, provenance or input use.");
            foreach(var bad in new[]{"missing","unselected"})
            {
                var envelope=RiskJobs.PinFrame(selected);
                if(bad=="missing")envelope.Remove("frozenObservationIds");
                else envelope["frozenObservationIds"]=System.Text.Json.Nodes.JsonNode.Parse("[\""+Guid.NewGuid()+"\"]");
                Reject("risk_job_context",()=>RiskJobs.ReadFrame(envelope));
            }
        });
        Check("an explicitly selected frozen revision remains frozen without claiming the original stored close",()=>
        {
            var original=Change(fixture.Observations[0],"role","final_contract_settlement");
            var replacement=Change(Change(Change(original,"id",Guid.NewGuid()),"corrects_id",original.Id("id")),"value","102.001");
            var revised=Data([..fixture.Observations.SetItem(0,original),replacement]);
            var identity=Guid.NewGuid();
            var stored=fixture.Frame with{Id=identity,OriginCloseId=identity,FrozenObservationIds=[original.Id("id")]};
            var edited=stored.SelectRevision(original.Id("id"),replacement.Id("id"),"Explicit synthetic revision");
            if(edited.Id==stored.Id||edited.OriginCloseId is not null||stored.ObservationIds[0]!=original.Id("id")||
                revised.Snapshot(edited).Selections.Single(s=>s.Observation.Key.Id==replacement.Id("id")).Input.Use!=InputUse.FrozenFact)
                throw new Exception("An explicit revision changed frozen use or masqueraded as the original stored close.");
        });
        Check("required futures joins preserve missing and ambiguous reference errors",()=>
        {
            foreach(var table in new[]{"futures_series","futures_products","futures_product_terms","futures_milestones"})
            {
                var rows=fixture.References[table];
                Reject("ambiguous_reference",()=>Data(references:new(fixture.References){[table]=rows.RemoveAt(0)}));
                Reject("ambiguous_reference",()=>Data(references:new(fixture.References){[table]=[..rows,rows[0]]}));
            }
        });
        Check("duplicate typed definitions retain their domain error",()=>
        {
            Reject("duplicate_definition",()=>Data(definitions:new(fixture.Definitions){["contracts"]=[..fixture.Definitions["contracts"],fixture.Definitions["contracts"][0]]}));
            Reject("duplicate_definition",()=>Data(references:new(fixture.References){["quote_specifications"]=[..fixture.References["quote_specifications"],fixture.References["quote_specifications"][0]]}));
        });
        Check("different authorized reads never reuse each other's source values",()=>
        {
            var changed=fixture.Observations.SetItem(0,Change(fixture.Observations[0],"value","201.001"));
            if(Data(changed).Build(fixture.Frame,"future").Values[0].Price.Value.ToString()!="201.001"||
                data.Build(fixture.Frame,"future").Values[0].Price.Value!=fixture.Observations[0].Number("value"))
                throw new Exception("A different read shared selected data.");
        });
        Check("durable catalog planning preserves manual inputs and only commits cache changes explicitly",()=>
        {
            var session=new PreviewSession(fixture.Manifest);
            session.Frames.Add(fixture.Frame.Id,fixture.Frame);
            var identity=Guid.NewGuid();var stored=fixture.Frame with{Id=identity,OriginCloseId=identity,FrozenObservationIds=[fixture.Observations[0].Id("id")]};
            var plan=session.PlanFrames(new([stored],127,false),null,[]);
            if(session.Frames.Count!=1||session.Frames.ContainsKey(identity))throw new Exception("An uncommitted discovery changed session state.");
            session.ApplyFrames(plan);
            if(session.PersistentFrames.Length!=1||session.VisibleFrames.Length!=2||!session.Frames[identity].FrozenObservationIds.SequenceEqual(stored.FrozenObservationIds))
                throw new Exception("A discovery replaced manual inputs or frozen uses.");
            session.ApplyFrames(session.PlanFrames(new([],127,false),null,[]));
            if(session.Frames.Count!=1||session.Frames[fixture.Frame.Id]!=fixture.Frame)throw new Exception("Refreshing visible stored closes removed a manual selection.");
        });
        Check("same-instant durable closes retain separate origins and stable exact historical aliases",()=>
        {
            var session=new PreviewSession(fixture.Manifest);
            var context=new ScheduleContext("2040-01-10",1,"2040-01-10T00:00:00Z",Guid.NewGuid());
            var first=Guid.NewGuid();var second=Guid.NewGuid();
            var stored=new[]{first,second}.Select(id=>fixture.Frame with{Id=id,OriginCloseId=id,ContextId=context.ContextId}).ToImmutableArray();
            var plan=session.PlanFrames(new(stored,128,false),context,[]);session.ApplyFrames(plan);
            var retained=plan.Frames.ToDictionary(frame=>frame.OriginCloseId!.Value,frame=>frame.Id);
            if(retained.Count!=2||retained.Any(pair=>pair.Key==pair.Value)||plan.Frames.Any(frame=>!frame.ObservationIds.SequenceEqual(fixture.Frame.ObservationIds)))
                throw new Exception("Historical close aliases collided or changed recorded inputs.");
            var replay=session.PlanFrames(new(stored,128,false),context,[]);
            if(replay.Frames.Any(frame=>retained[frame.OriginCloseId!.Value]!=frame.Id))throw new Exception("Refreshing one retained context changed its explicit frame identities.");
            session.ForgetDiscoveredFrames();
            if(session.PlanFrames(new(stored,128,false),context,[]).Frames.Any(frame=>retained[frame.OriginCloseId!.Value]!=frame.Id))
                throw new Exception("Returning to a stored context changed its exact close alias identity.");
        });
        Check("initial certified aliases survive context replay without adopting manual current variants",()=>
        {
            var session=Seeded(fixture.Frame);
            var context=new ScheduleContext("2040-01-10",1,"2040-01-10T00:00:00Z",Guid.NewGuid());
            var plan=session.PlanFrames(new([],126,false),context,session.SelectContextAliases(data,context));
            session.ApplyFrames(plan);var identity=plan.Frames.Single().Id;
            session.ForgetDiscoveredFrames();
            var replay=session.PlanFrames(new([],126,false),context,session.SelectContextAliases(data,context));
            if(replay.Frames.Single().Id!=identity)throw new Exception("An exact certified replay lost its original frame identity.");
            var correction=Change(Change(Change(fixture.Observations[0],"id",Guid.NewGuid()),"corrects_id",fixture.Observations[0].Id("id")),"value","111.001");
            var updated=Data([..fixture.Observations,correction]);
            if(session.PlanFrames(new([],126,false),context,session.SelectContextAliases(updated,context)).Frames.Single().Id==identity)
                throw new Exception("A changed resolved pin reused an earlier snapshot identity.");
            session.ForgetDiscoveredFrames(resetAliasIdentities:true);
            if(session.PlanFrames(new([],126,false),context,session.SelectContextAliases(data,context)).Frames.Single().Id==identity)
                throw new Exception("Changing identity retained the previous user's alias map.");
            var manual=fixture.Frame.SelectRevision(fixture.Observations[0].Id("id"),correction.Id("id"),"Explicit synthetic revision");
            session.Frames.Add(manual.Id,manual);
            if(session.SelectContextAliases(updated,context).Length!=1||session.PersistentFrames.Length!=2||session.Frames[manual.Id]!=manual)
                throw new Exception("Historical alias selection adopted or removed a manual current variant.");
        });
        Check("certified initial aliases keep their directory order across authorized refreshes",()=>
        {
            var seed=new PreviewSeedFrame(fixture.Frame.At,fixture.Frame.ObservationIds.ToArray(),fixture.Frame.CurveContracts.ToDictionary(pair=>pair.Key,pair=>pair.Value.ToArray()));
            var session=new PreviewSession(fixture.Manifest with{Frames=[seed with{At=seed.At.AddDays(-1)},seed]});
            var context=new ScheduleContext("2040-01-10",1,"2040-01-10T00:00:00Z",Guid.NewGuid());
            session.ApplyFrames(session.PlanFrames(new([],124,false),context,session.SelectContextAliases(data,context)));
            var initial=session.ContextAliases(context).Select(frame=>frame.Id).ToArray();
            session.ApplyFrames(session.PlanFrames(new([],124,false),context,session.ContextAliases(context)));
            if(!session.ContextAliases(context).Select(frame=>frame.Id).SequenceEqual(initial))
                throw new Exception("Refreshing an unchanged context reordered its initial snapshot directory.");
        });
        Check("an unavailable exact manual pin removes its whole certified alias without changing the saved frame",()=>
        {
            var session=Seeded(fixture.Frame);var saved=session.PersistentFrames.Single();
            var context=new ScheduleContext("2040-01-10",1,"2040-01-10T00:00:00Z",Guid.NewGuid());
            if(!session.SelectContextAliases(Data(fixture.Observations.RemoveAt(0)),context).IsEmpty||session.PersistentFrames.Single()!=saved)
                throw new Exception("An unavailable manual pin was trimmed, replaced or erased from the session.");
        });
        Check("certified aliases reject malformed frozen markers and retained membership",()=>
        {
            var context=new ScheduleContext("2040-01-10",1,"2040-01-10T00:00:00Z",Guid.NewGuid());
            void Invalid(InputFrame frame)
            {
                var session=Seeded(frame);
                Reject("snapshot_inputs",()=>session.SelectContextAliases(data,context));
            }
            Invalid(fixture.Frame with{FrozenObservationIds=[Guid.NewGuid()]});
            Invalid(fixture.Frame with{ObservationIds=[..fixture.Frame.ObservationIds,fixture.Frame.ObservationIds[0]]});
            Invalid(fixture.Frame with{CurveContracts=fixture.Frame.CurveContracts.SetItem("future",[fixture.Frame.CurveContracts["future"][0],fixture.Frame.CurveContracts["future"][0]])});
            Invalid(fixture.Frame with{CurveContracts=fixture.Frame.CurveContracts.SetItem("future",[Guid.NewGuid()])});
            Invalid(fixture.Frame with{CurveContracts=fixture.Frame.CurveContracts.Add("undefined_curve",[])});
        });
        Check("explicit empty market views retain usable curves and do not manufacture missing inputs",()=>
        {
            var definitions=new Dictionary<string,ImmutableArray<JsonElement>>(fixture.Definitions)
            {
                ["curves"]=[..fixture.Definitions["curves"],Change(fixture.Definitions["curves"].Single(),"code","completed_future")],
                ["curve_members"]=[..fixture.Definitions["curve_members"],..fixture.Definitions["curve_members"].Select(row=>Change(row,"curve_code","completed_future"))]
            };
            var selectedData=Data(definitions:definitions);
            var context=new ScheduleContext("2040-01-10",1,"2040-01-10T00:00:00Z",Guid.NewGuid());
            InputFrame Alias(InputFrame seed)
            {
                var session=Seeded(seed);
                return session.SelectContextAliases(selectedData,context).Single();
            }
            var mixed=Alias(fixture.Frame with{CurveContracts=fixture.Frame.CurveContracts.Add("completed_future",[])});
            if(selectedData.Build(mixed,"future").Values.Length!=405||Row(selectedData.Result(mixed,"completed_future")).Text("availability")!="no_remaining_contracts")
                throw new Exception("An explicit empty view prevented another curve from retaining its exact inputs.");
            var auxiliary=Alias(fixture.Frame with{CurveContracts=ImmutableDictionary<string,ImmutableArray<Guid>>.Empty});
            Reject("missing_curve_view",()=>selectedData.Build(auxiliary,"future"));
            var empty=Alias(fixture.Frame with{ObservationIds=[],CurveContracts=ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("completed_future",[])});
            if(Row(selectedData.Result(empty,"completed_future")).Text("availability")!="no_remaining_contracts")
                throw new Exception("An explicit empty view invented an observation requirement.");
        });
        Check("retained catalog capacity is explicit and never evicts a manual selection",()=>
        {
            var session=new PreviewSession(fixture.Manifest);
            for(var i=0;i<PreviewSession.MaximumFrames;i++)
            {var frame=fixture.Frame with{Id=Guid.NewGuid()};session.Frames.Add(frame.Id,frame);}
            Reject("preview_limit",()=>session.PlanFrames(new([],0,true),null,[]));
            if(session.Frames.Count!=PreviewSession.MaximumFrames)throw new Exception("Capacity failure evicted an explicit input selection.");
        });
        Console.WriteLine(count+" preview data checks passed.");
    }

    private static JsonElement Row(object value)=>JsonSerializer.SerializeToElement(value);
    private static JsonElement Change(JsonElement row,string key,object? value)
    {var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(row.GetRawText())!;fields[key]=Row(value!);return Row(fields);}
    private static (PreviewManifest Manifest,Dictionary<string,ImmutableArray<JsonElement>> Definitions,
        Dictionary<string,ImmutableArray<JsonElement>> References,ImmutableArray<JsonElement> Observations,InputFrame Frame) Fixture(int count)
    {
        var cat=Guid.NewGuid();var rev=Guid.NewGuid();var rcat=Guid.NewGuid();var rrev=Guid.NewGuid();
        var contract=Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToArray();
        var series=Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToArray();
        var at=DateTimeOffset.Parse("2040-01-10T00:00:00Z",CultureInfo.InvariantCulture);
        Dictionary<string,ImmutableArray<JsonElement>> definitions=new()
        {
            ["revisions"]=[Row(new{catalog_id=cat,id=rev,reference_catalog_id=rcat,reference_revision_id=rrev,state="sealed"})],
            ["contracts"]=[..contract.Select((id,i)=>Row(new{id,kind="future",delivery_from="2040-02-01",delivery_to="2040-03-01",expiry_at="2040-01-20T00:00:00Z",futures_series_code="synthetic_"+i}))],
            ["curves"]=[Row(new{code="future",quote_code="price",location_code="hub",profile_code="uniform",settlement_target_code="target",rule="direct",direct_role="daily_settlement",direct_source_code="synthetic"})],
            ["curve_members"]=[..contract.Select((id,i)=>Row(new{curve_code="future",contract_id=id,ordinal=i}))],
            ["series"]=[..series.Select((id,i)=>Row(new{id,contract_id=contract[i],quote_code="price",value_specification_code=(string?)null}))],
            ["curve_inputs"]=[],["constituent_mappings"]=[],["rate_curves"]=[],["rate_curve_points"]=[],["revision_periods"]=[],["contract_intervals"]=[],["curve_expiry_rules"]=[]
        };
        Dictionary<string,ImmutableArray<JsonElement>> references=new()
        {
            ["quantity_specifications"]=[Row(new{code="quantity",commodity_code="gas",basis_code="energy",unit_code="mwh",dimension_code="energy"})],
            ["quote_specifications"]=[Row(new{code="price",kind="outright",currency_code="usd",quantity_code="quantity",multiplier="1",trading_tick="0.001",clearing_increment="0.001",minimum="0",maximum="1000"})],
            ["settlement_targets"]=[Row(new{code="target",kind="future_series",index_a_code=(string?)null,index_b_code=(string?)null})],
            ["futures_series"]=[..Enumerable.Range(0,count).Select(i=>Row(new{code="synthetic_"+i,product_code="product",delivery_from="2040-02-01",delivery_to="2040-03-01"}))],
            ["futures_products"]=[Row(new{code="product",commodity_code="gas",quote_currency_code="usd",quote_multiplier="1",outright_tick_size="0.001",location_code="hub",quote_unit_code="mwh",lot_unit_code="mwh",contract_size="40",lot_basis="fixed_total",settlement_method="cash"})],
            ["futures_milestones"]=[..Enumerable.Range(0,count).SelectMany(i=>new[]{
                Row(new{series_code="synthetic_"+i,milestone="first_trading",value_state="known",event_at="2040-01-01T00:00:00Z"}),
                Row(new{series_code="synthetic_"+i,milestone="last_trading",value_state="known",event_at="2040-01-20T00:00:00Z"})})],
            ["futures_product_terms"]=[Row(new{product_code="product",profile_code="uniform",index_code=(string?)null})],
            ["futures_lot_resolutions"]=[],["futures_basis_settlements"]=[],["value_specifications"]=[]
        };
        ImmutableArray<JsonElement> observations=[..series.Select((id,i)=>Row(new{id=Guid.NewGuid(),series_id=id,source_code="synthetic",role="daily_settlement",publication_state="final",observed_at="2040-01-09T00:00:00Z",value="100."+i.ToString("D3",CultureInfo.InvariantCulture),corrects_id=(Guid?)null}))];
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic selected futures",at,[..observations.Select(row=>row.Id("id"))],
            ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[..contract]));
        return(new("local-synthetic-preview",cat,rev,rcat,rrev,Guid.NewGuid(),Guid.NewGuid(),[],null),definitions,references,observations,frame);
    }
}
