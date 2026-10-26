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
        Check("history keeps one sample per instant: identical versions collapse, different ones are a conflict (D229, D230)",()=>
        {
            var at=new DateTimeOffset(2020,12,17,16,0,0,TimeSpan.Zero);
            HistorySample Sample(string? value,DateTimeOffset? known,string? reason=null)=>new(at,value,Guid.NewGuid(),[],reason,known);
            var early=at.AddDays(1);var late=at.AddDays(2);
            var one=HistoryRevisionPolicy.OnePerInstant([Sample("1.5",early),Sample("1.5",late),new HistorySample(at.AddDays(1),"2",Guid.NewGuid(),[],null)]).ToArray();
            if(one.Length!=2||one[0].KnownAt!=late||one[0].Value!="1.5")throw new Exception("Identical samples must count once, keeping the latest knowledge.");
            Reject("history_conflict",()=>HistoryRevisionPolicy.OnePerInstant([Sample("1.5",early),Sample("1.6",late)]).ToArray());
            Reject("history_conflict",()=>HistoryRevisionPolicy.OnePerInstant([Sample("1.5",early),Sample(null,early,"source_expired")]).ToArray());
        });
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
        Check("a preliminary index correction cannot displace the latest eligible final fixing",()=>
        {
            var original=Change(fixture.Observations[0],"role","index_fixing");
            var revised=Change(Change(Change(original,"id",Guid.NewGuid()),"corrects_id",original.Id("id")),"value","111.001");
            var preliminary=Change(Change(Change(revised,"id",Guid.NewGuid()),"corrects_id",revised.Id("id")),"publication_state","preliminary");
            var inputs=fixture.Observations.SetItem(0,original);
            var selected=Data([..inputs,revised,preliminary]).SelectPinned(fixture.Frame);
            if(selected.ObservationIds[0]!=revised.Id("id"))throw new Exception("A preliminary correction displaced a final index fixing.");
            if(Data([..inputs,revised,preliminary]).Snapshot(fixture.Frame).Selections[0].Observation.Key.Id!=original.Id("id"))
                throw new Exception("Original exact snapshot was reinterpreted.");
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
        Check("a curve's commodity is the code and name of its reference, and a curve without one fails the read",()=>
        {
            ImmutableArray<JsonElement> Commodities(params string[] codes)=>[..codes.Select(code=>Row(new{code,name="Synthetic "+code}))];
            ImmutableArray<JsonElement> Curve(string? commodity)=>[Change(fixture.Definitions["curves"].Single(),"commodity_code",commodity)];
            PreviewData Held(ImmutableArray<JsonElement> commodities,ImmutableArray<JsonElement> curves)=>
                Data(references:new(fixture.References){["commodities"]=commodities},definitions:new(fixture.Definitions){["curves"]=curves});
            if(Held(Commodities("gas","power"),Curve("gas")).Commodity("future") is not {Code:"gas",Name:"Synthetic gas"})throw new Exception("A curve lost the commodity its reference names.");
            Reject("ambiguous_reference",()=>Held(Commodities("gas"),Curve("gas")).Commodity("unlisted"));
            Reject("ambiguous_reference",()=>Held(Commodities("power"),Curve("gas")).Commodity("future"));
            Reject("ambiguous_reference",()=>Held(Commodities("gas","gas"),Curve("gas")).Commodity("future"));
            Reject("ambiguous_reference",()=>Held(Commodities("gas"),Curve(null)).Commodity("future"));
        });
        // The reference rows that name a quote's currency and unit, which the fixture leaves out, and a quote multiplier its futures keep.
        Dictionary<string,ImmutableArray<JsonElement>> Quoting(string multiplier="1")=>new(fixture.References)
        {
            ["currencies"]=[Row(new{code="usd",display_code="USD"})],["units"]=[Row(new{code="mwh",symbol="MWh"})],
            ["quote_specifications"]=[Change(fixture.References["quote_specifications"].Single(),"multiplier",multiplier)],
            ["futures_products"]=[Change(fixture.References["futures_products"].Single(),"quote_multiplier",multiplier)]
        };
        Check("a curve's price unit names its quote's currency and unit, then a multiplier other than one",()=>
        {
            if(Data(references:Quoting()).PriceUnit("future")!="USD/MWh"||Data(references:Quoting("100")).PriceUnit("future")!="USD/MWh × 100")
                throw new Exception("A curve's price unit changed.");
            Reject("ambiguous_reference",()=>Data(references:Quoting()).PriceUnit("unlisted"));
        });
        Check("a curve shape prices every contract its view selects exactly as the curve itself does",()=>
        {
            var priced=Data(references:Quoting());
            var shape=MarketReads.Shape(priced,priced.Snapshot(fixture.Frame),fixture.Frame,"future");
            var own=Row(priced.Result(fixture.Frame,"future")).GetProperty("points").EnumerateArray().ToArray();
            if(shape is not {Curve:"future",Unit:"USD/MWh",UnavailableReason:null,Points:{} points}||points.Length!=405||!points.SequenceEqual(own.Select(p=>
                new CurveShapePoint(p.Id("contractId"),p.Text("deliveryFrom"),p.Text("deliveryTo"),p.Text("value"),null))))
                throw new Exception("A curve shape differs from the prices and delivery periods of its curve.");
        });
        // The first contracts expire before the frame's instant, and the one after them, if asked, has no pinned price.
        (PreviewData Priced,InputFrame Frame) Unpriceable(int expired,bool unpinned,ImmutableArray<JsonElement>? observations=null)
        {
            var expiring=Enumerable.Range(0,expired).Select(i=>"synthetic_"+i).ToHashSet();
            var priced=Data(observations,references:new(Quoting()){["futures_milestones"]=[..fixture.References["futures_milestones"].Select(row=>
                    row.Text("milestone")=="last_trading"&&expiring.Contains(row.Text("series_code"))?Change(row,"event_at","2040-01-05T00:00:00Z"):row)]},
                definitions:new(fixture.Definitions){["contracts"]=[..fixture.Definitions["contracts"].Select((row,i)=>i<expired?Change(row,"expiry_at","2040-01-05T00:00:00Z"):row)]});
            return(priced,unpinned?fixture.Frame with{ObservationIds=fixture.Frame.ObservationIds.RemoveAt(expired)}:fixture.Frame);
        }
        string Day(DateOnly date)=>date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        Check("a curve shape states each contract its inputs cannot price, and prices the others as the curve prices them alone",()=>
        {
            var (priced,gap)=Unpriceable(3,true);
            Reject("source_expired",()=>priced.Build(gap,"future"));
            var shape=MarketReads.Shape(priced,priced.Snapshot(gap),gap,"future");
            var whole=data.Build(fixture.Frame,"future").Values;
            if(shape is not {UnavailableReason:null,Points:{} points}||points.Length!=405||points.Take(3).Any(p=>p is not {Price:null,UnavailableReason:"source_expired"})||
                points[3] is not {Price:null,UnavailableReason:"missing_observation"}||
                !points.Skip(4).SequenceEqual(whole.Skip(4).Select(v=>new CurveShapePoint(v.Request.Contract.Id,
                    Day(priced.ContractRows(v.Request.Contract.Id).Single().DeliveryFrom),Day(priced.ContractRows(v.Request.Contract.Id).Single().DeliveryTo),v.Price.Value.ToString(),null))))
                throw new Exception("A curve shape hid a contract it could not price, or priced another differently.");
        });
        Check("a curve's summary counts its contracts and fronts the first one the close prices, in its shape and in its evaluation alike",()=>
        {
            var priced=Data(references:Quoting());
            var own=Row(priced.Result(fixture.Frame,"future")).GetProperty("points").EnumerateArray().ToArray();
            var first=own[0];
            var summary=Wire(priced.Result(fixture.Frame,"future")).GetProperty("summary");
            var front=summary.GetProperty("front");
            if(summary.Text("kind")!="contracts"||summary.GetProperty("contractCount").GetInt32()!=405||summary.GetProperty("availability").ValueKind!=JsonValueKind.Null||
                front.Id("contractId")!=first.Id("contractId")||front.Text("deliveryFrom")!=first.Text("deliveryFrom")||
                front.Text("deliveryTo")!=first.Text("deliveryTo")||front.Text("price")!=first.Text("value"))
                throw new Exception("An evaluation did not summarize its curve by its count and first contract.");
            if(MarketReads.Shape(priced,priced.Snapshot(fixture.Frame),fixture.Frame,"future") is not {ContractCount:405,Front:{} shaped}||
                shaped!=new CurveFront(first.Id("contractId"),first.Text("deliveryFrom"),first.Text("deliveryTo"),first.Text("value")))
                throw new Exception("A curve shape did not summarize its curve as its evaluation does.");
            // The first three contracts have expired and the fourth has no pinned price: the front is the fifth.
            var (gapped,gap)=Unpriceable(3,true);
            var fifth=data.Build(fixture.Frame,"future").Values[4];
            var fifthRow=gapped.ContractRows(fifth.Request.Contract.Id).Single();
            if(MarketReads.Shape(gapped,gapped.Snapshot(gap),gap,"future") is not {ContractCount:405,Front:{} next}||
                next!=new CurveFront(fifth.Request.Contract.Id,Day(fifthRow.DeliveryFrom),Day(fifthRow.DeliveryTo),fifth.Price.Value.ToString()))
                throw new Exception("A curve whose first contract has no price did not front the next priced one.");
            var definitions=new Dictionary<string,ImmutableArray<JsonElement>>(fixture.Definitions)
            {
                ["curves"]=[..fixture.Definitions["curves"],Change(fixture.Definitions["curves"].Single(),"code","completed_future")],
                ["curve_members"]=[..fixture.Definitions["curve_members"],..fixture.Definitions["curve_members"].Select(row=>Change(row,"curve_code","completed_future"))]
            };
            var both=Data(references:Quoting(),definitions:definitions);
            var emptied=fixture.Frame with{CurveContracts=fixture.Frame.CurveContracts.Add("completed_future",[])};
            var none=Wire(both.Result(emptied,"completed_future")).GetProperty("summary");
            if(none.Text("kind")!="contracts"||none.GetProperty("contractCount").GetInt32()!=0||none.GetProperty("front").ValueKind!=JsonValueKind.Null||none.Text("availability")!="no_remaining_contracts")
                throw new Exception("A curve with no remaining contracts did not say so in its summary.");
            if(MarketReads.Shape(both,both.Snapshot(emptied),emptied,"completed_future") is not {ContractCount:0,Front:null})
                throw new Exception("A curve shape with no remaining contracts did not state a zero count and no front.");
        });
        Check("the contracts a close cannot price are isolated in builds that grow with them and not with the curve",()=>
        {
            // The fastest of three runs, so the first run's start-up does not count.
            (int Builds,double Milliseconds,CurveBuild? Build,ImmutableDictionary<CurveRequest,string> Unvalued) Isolate(PreviewData priced,InputFrame frame)
            {
                var snapshot=priced.Snapshot(frame);
                var requests=priced.Requests(frame,"future");
                (int,double,CurveBuild?,ImmutableDictionary<CurveRequest,string>) fastest=(0,double.MaxValue,null,ImmutableDictionary<CurveRequest,string>.Empty);
                for(var run=0;run<3;run++)
                {
                    var builds=0;
                    var watch=System.Diagnostics.Stopwatch.StartNew();
                    var (build,unvalued)=ValuationStatus.BuildValued(set=>{builds++;return priced.Model.Build(snapshot,set);},requests);
                    watch.Stop();
                    if(watch.Elapsed.TotalMilliseconds<fastest.Item2)fastest=(builds,watch.Elapsed.TotalMilliseconds,build,unvalued);
                }
                return fastest;
            }
            var (few,gap)=Unpriceable(3,true);
            var (builds,milliseconds,rest,unvalued)=Isolate(few,gap);
            var requests=few.Requests(gap,"future");
            // About 3 x log2(405) builds for each of the four, besides the one for the rest; the old way built one for each of 405.
            if(rest is null||rest.Values.Length!=401||unvalued.Count!=4||
                requests.Take(4).Any(r=>unvalued.GetValueOrDefault(r)!=(r==requests[3]?"missing_observation":"source_expired"))||builds>4*(3*9+3)+9+2)
                throw new Exception($"Four unpriceable contracts of 405 were not isolated in builds that grow with them; {builds} were built.");
            Console.WriteLine($"INFO preview data: 4 of 405 contracts isolated in {builds} builds, {milliseconds:F0} ms");
            // When every contract fails, each needs its own code and is built alone once: no more builds than contracts, and the whole curve first.
            var (everyone,expired)=Unpriceable(405,false);
            var (denseBuilds,denseMilliseconds,none,every)=Isolate(everyone,expired);
            if(none is not null||every.Count!=405||every.Values.Any(code=>code!="source_expired")||denseBuilds>405+1)
                throw new Exception($"Every contract unpriceable was not isolated in one build each; {denseBuilds} were built.");
            Console.WriteLine($"INFO preview data: 405 of 405 contracts isolated in {denseBuilds} builds, {denseMilliseconds:F0} ms");
        });
        Check("whichever contracts fail, exactly those are isolated with their own codes, in at most three builds for each contract",()=>
        {
            var small=Fixture(48);
            var model=new PreviewData(small.Manifest,small.Definitions,small.References,small.Observations);
            var snapshot=model.Snapshot(small.Frame);
            var requests=model.Requests(small.Frame,"future");
            var random=new Random(7);
            for(var round=0;round<200;round++)
            {
                var failing=requests.OrderBy(_=>random.Next()).Take(random.Next(1,requests.Length+1)).ToDictionary(r=>r,_=>random.Next(2)==0?"source_expired":"missing_observation");
                var builds=0;
                // A set fails with the code of the first contract in it that fails, as a build of the set does.
                CurveBuild Build(ImmutableArray<CurveRequest> set)
                {
                    builds++;
                    foreach(var request in set)if(failing.TryGetValue(request,out var code))throw new MarketDataException(code,"Synthetic failure of a contract.");
                    return model.Model.Build(snapshot,set);
                }
                var (valued,unvalued)=ValuationStatus.BuildValued(Build,requests);
                if(unvalued.Count!=failing.Count||failing.Any(f=>unvalued.GetValueOrDefault(f.Key)!=f.Value)||(valued?.Values.Length??0)!=requests.Length-failing.Count||builds>3*requests.Length+2)
                    throw new Exception($"Round {round}: {failing.Count} failing contracts were isolated as {unvalued.Count}, with {builds} builds.");
            }
        });
        Check("when no single contract fails alone the failure is not a contract's and fails the build, as is any other failure of one",()=>
        {
            var priced=Data(references:Quoting());
            var snapshot=priced.Snapshot(fixture.Frame);
            var requests=priced.Requests(fixture.Frame,"future");
            // Each contract is priced alone and no two together: a failure of the combination, not of any contract.
            CurveBuild Together(ImmutableArray<CurveRequest> set)=>set.Length>1?throw new MarketDataException("source_expired","Synthetic failure of a combination."):priced.Model.Build(snapshot,set);
            Reject("source_expired",()=>ValuationStatus.BuildValued(Together,requests));
            // A contract that fails alone for a reason that is not position-specific fails the build.
            CurveBuild Other(ImmutableArray<CurveRequest> set)=>set.Contains(requests[5])?throw new MarketDataException(set.Length==1?"price_domain":"missing_observation","Synthetic."):priced.Model.Build(snapshot,set);
            Reject("price_domain",()=>ValuationStatus.BuildValued(Other,requests));
        });
        Check("a curve that cannot be read is stated unavailable with the code its own read raises, and the curve beside it is still shaped",()=>
        {
            var definitions=new Dictionary<string,ImmutableArray<JsonElement>>(fixture.Definitions)
            {
                ["curves"]=[..fixture.Definitions["curves"],Change(fixture.Definitions["curves"].Single(),"code","completed_future")],
                ["curve_members"]=[..fixture.Definitions["curve_members"],..fixture.Definitions["curve_members"].Select(row=>Change(row,"curve_code","completed_future"))]
            };
            var both=Data(references:Quoting(),definitions:definitions);
            CurveShape Of(PreviewData read,InputFrame frame,string curve)=>MarketReads.Shape(read,read.Snapshot(frame),frame,curve);
            if(Of(both,fixture.Frame,"unlisted") is not {Curve:"unlisted",Unit:null,Points:null,UnavailableReason:"missing_curve"})throw new Exception("An undefined curve is stated unavailable.");
            Reject("missing_curve",()=>both.Result(fixture.Frame,"unlisted"));
            if(Of(both,fixture.Frame,"completed_future") is not {Curve:"completed_future",Unit:null,Points:null,UnavailableReason:"missing_curve_view"})throw new Exception("A curve its frame has no view of is stated unavailable.");
            Reject("missing_curve_view",()=>both.Result(fixture.Frame,"completed_future"));
            var emptied=fixture.Frame with{CurveContracts=fixture.Frame.CurveContracts.Add("completed_future",[])};
            if(Of(both,emptied,"completed_future") is not {Unit:"USD/MWh",UnavailableReason:null,Points:{IsEmpty:true}})throw new Exception("A curve whose view selects nothing has no points.");
            var mismatched=Data(fixture.Observations.SetItem(0,Change(fixture.Observations[0],"role","final_contract_settlement")),Quoting());
            if(Of(mismatched,fixture.Frame,"future") is not {Unit:null,Points:null,UnavailableReason:"selection_mismatch"})throw new Exception("A curve failing for another reason states that reason.");
            Reject("selection_mismatch",()=>mismatched.Result(fixture.Frame,"future"));
            if(Of(both,fixture.Frame,"future") is not {UnavailableReason:null,Points:{Length:405}})throw new Exception("A curve beside an unreadable one is shaped.");
        });
        Check("a catalog inconsistency in the references of a readable curve's price unit fails the read, and does not reach a curve that cannot be read",()=>
        {
            // Two currency rows for the quote's currency: the curve prices without them, but its unit cannot be named.
            var inconsistent=Data(references:new(Quoting()){["currencies"]=[Row(new{code="usd",display_code="USD"}),Row(new{code="usd",display_code="Dollars"})]});
            inconsistent.Result(fixture.Frame,"future");
            var snapshot=inconsistent.Snapshot(fixture.Frame);
            Reject("ambiguous_reference",()=>MarketReads.Shape(inconsistent,snapshot,fixture.Frame,"future"));
            if(MarketReads.Shape(inconsistent,snapshot,fixture.Frame,"unlisted") is not {Unit:null,Points:null,UnavailableReason:"missing_curve"})
                throw new Exception("A curve that cannot be read states its own failure, whatever its unit's references are.");
        });
        Check("a curve whose whole build fails for a position-specific reason states that failure, though isolating it meets another that the Blotter's isolation still raises",()=>
        {
            // The first contract expires, and the sixth has an observation the curve cannot take.
            var (both,frame)=Unpriceable(1,false,fixture.Observations.SetItem(5,Change(fixture.Observations[5],"role","final_contract_settlement")));
            Reject("source_expired",()=>both.Result(frame,"future"));
            var snapshot=both.Snapshot(frame);
            if(MarketReads.Shape(both,snapshot,frame,"future") is not {Curve:"future",Unit:null,Points:null,UnavailableReason:"source_expired"})
                throw new Exception("A curve states the failure of its own read, not one met while its contracts were isolated.");
            Reject("selection_mismatch",()=>ValuationStatus.BuildValued(set=>both.Model.Build(snapshot,set),both.Requests(frame,"future")));
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
    // A result as the HTTP API states it.
    private static JsonElement Wire(object value)=>JsonSerializer.SerializeToElement(value,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
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
