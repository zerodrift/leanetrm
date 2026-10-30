using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class SideObservationCompatibilityChecks
{
    public static void Run()
    {
        var revision = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        var definitions = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
        var index = new ReferenceKey(revision, "synthetic_index");
        var quote = new ReferenceKey(revision, "synthetic_quote");
        var source = new ReferenceKey(revision, "synthetic_source");
        IndexFixing Fixing(DateOnly from, DateOnly to) => new(index, quote, from, to,
            new DateTimeOffset(2048, 3, 31, 16, 45, 0, TimeSpan.Zero),
            new(definitions, Guid.NewGuid()), source, IndexFixing.SupportedImplementation);
        var month = Fixing(new(2048, 4, 1), new(2048, 5, 1));
        var days = Enumerable.Range(1, 30).Select(day =>
            Fixing(new(2048, 4, day), new DateOnly(2048, 4, day).AddDays(1))).ToImmutableArray();

        var monthly = SwapObservationCompatibility.SupportedSamplings([month]);
        if (monthly.Length != 1 || monthly[0].Averaging != FloatingAveraging.MonthlyIndexPrice)
            throw new Exception("A monthly published fixing acquired distinct daily observations.");
        var daily = SwapObservationCompatibility.SupportedSamplings(days);
        if (daily.Length != 5 || daily.Any(mode => mode.Averaging == FloatingAveraging.MonthlyIndexPrice))
            throw new Exception("Daily fixing capability was inferred from payment frequency or reconstructed a monthly publication.");
        var all = SwapObservationCompatibility.SupportedSamplings([month, .. days]);
        if (all.Length != 6 || all.Select(mode => mode.Averaging).Distinct().Count() != 6)
            throw new Exception("The qualified mode catalog is incomplete or ambiguous.");
        if (SwapObservationCompatibility.SupportedSamplings([]).Length != 0)
            throw new Exception("Missing publication metadata became a compatible source.");
        foreach (var mode in all)
            if (SwapObservationCompatibility.Parse(mode.ResetFrequency, mode.AveragingMethod) != mode.Averaging)
                throw new Exception("The authoring pair changed the selected convention.");
        foreach (var pair in new[] { ("monthly", "arithmetic"), ("hourly", "arithmetic"), ("daily", "none"), ("daily", "unknown") })
        {
            try { SwapObservationCompatibility.Parse(pair.Item1, pair.Item2); }
            catch (MarketDataException error) when (error.Code == "swap_averaging") { continue; }
            throw new Exception("An unsupported sampling combination was admitted.");
        }
        var unsupported = days.SetItem(0, days[0] with { Implementation = "unqualified_publication" });
        if (SwapObservationCompatibility.SupportedSamplings([unsupported[0]]).Length != 0)
            throw new Exception("An unqualified publication implementation became compatible.");
        CheckReferenceInputs(revision);
        CheckAdmissionMatrix();
        CheckContractObservationInput();
        CheckContractObservationResolution();
        CheckContractObservationCorrections();
        Console.WriteLine("PASS Side observation compatibility: all six explicit authoring pairs; monthly/daily publication roles remain distinct; missing or unsupported metadata and finer cadence reject");
    }

    private static void CheckContractObservationCorrections()
    {
        var fixture=new AdmissionFixture();var empty=fixture.Data();
        var contract=empty.Contracts.First(c=>c.DeliveryTo==c.DeliveryFrom.AddDays(1));
        var quote=empty.Quotes.Single();var at=new DateTimeOffset(2048,4,1,16,0,0,TimeSpan.Zero);
        var series=empty.Definitions["series"].Single(row=>row.Id("contract_id")==contract.Key.Id).Id("id");
        var lookup=new ContractObservationLookup(new(new(empty.DefinitionRevision,"projection"),contract.Key),contract,quote,
            new(0,new(2048,4,1),new(2048,4,2),at),at,new(empty.ReferenceRevision,"projection_source"),new(empty.DefinitionRevision,series),
            ObservationRole.TraderMark,ContractObservationExpiry.RequireLive,ContractObservationLookup.SupportedImplementation);
        var prior=Guid.NewGuid();var correction=Guid.NewGuid();
        JsonElement Observation(Guid id,string state,Guid? corrects)=>Row(new{id,series_id=series,source_code="projection_source",role="trader_mark",publication_state=state,observed_at=at,value="12",corrects_id=corrects});
        var data=fixture.Data(observations:[Observation(prior,"final",null),Observation(correction,"preliminary",prior)]);
        var frame=new InputFrame(Guid.NewGuid(),"Synthetic explicit sample",at.AddDays(1),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,null);
        Reject("missing_observation",()=>ContractObservationSamples.Select(data,frame,lookup,Guid.NewGuid(),at.AddDays(2)),
            "visible preliminary correction supersedes former final");
        data=fixture.Data(observations:[Observation(prior,"final",null),Observation(correction,"final",prior)]);
        var selected=ContractObservationSamples.Select(data,frame,lookup,Guid.NewGuid(),at.AddDays(2));
        if(selected.Selections.Single().Observation.Key.Id!=correction || selected.ValuationAt!=at)
            throw new Exception("An explicit visible correction must retain its economic sample instant.");
        var originalData=fixture.Data(observations:[Observation(prior,"final",null)]);
        var original=ContractObservationSamples.Select(originalData,frame,lookup,Guid.NewGuid(),at.AddDays(2));
        var evidence=new ConnectedSampleEvidence(original.Id,at,"projection",contract.Key.Id,true,
            [new(prior,series,"Variable")],"contract_observation",false);
        var replay=ContractObservationSamples.Select(data,frame,lookup,Guid.NewGuid(),at.AddDays(2),evidence);
        if(replay.Selections.Single().Observation.Key.Id!=prior||replay.Id!=original.Id)
            throw new Exception("Retained source replay reselected a correction instead of the exact admitted historical observation.");
        var currentFrame=frame with{At=at,ObservationIds=[prior]};
        var current=ContractObservationSamples.Select(originalData,currentFrame,lookup,Guid.NewGuid(),at.AddDays(2));
        var currentEvidence=ContractObservationSamples.Evidence(new(lookup,originalData.Model,current),at);
        if(currentEvidence.FreezeHistorical||!currentEvidence.FreezeObservation)
            throw new Exception("An actual at valuation must retain observation freezing separately from historical freezing.");
        Reject("contract_observation_source",()=>ContractObservationSamples.Select(originalData,currentFrame,lookup,Guid.NewGuid(),at.AddDays(2),currentEvidence with{FreezeObservation=false}),"lost current actual freezing");
        Reject("contract_observation_source",()=>ContractObservationSamples.Select(originalData,frame,lookup,Guid.NewGuid(),at.AddDays(2),evidence with{SnapshotId=Guid.NewGuid()}),"changed retained snapshot identity");
        Reject("missing_observation",()=>ContractObservationSamples.Select(fixture.Data(observations:[Observation(correction,"final",prior)]),frame,lookup,Guid.NewGuid(),at.AddDays(2),evidence),"missing exact retained source pin");
        Console.WriteLine("PASS contract observation sampling: terminal corrections, exact retained replay and current actual freezing");
    }

    private static void CheckContractObservationResolution()
    {
        var revision = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
        var reference = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        var at = new DateTimeOffset(2048, 4, 1, 16, 0, 0, TimeSpan.Zero);
        var first = new MarketContract(new(revision, Guid.NewGuid()), ContractKind.Future,
            new(2048, 5, 1), new(2048, 6, 1), at.AddDays(15), new(reference, "may"));
        var second = new MarketContract(new(revision, Guid.NewGuid()), ContractKind.Future,
            new(2048, 6, 1), new(2048, 7, 1), at.AddDays(45), new(reference, "jun"));
        var sequence = new ContractSequence(new(revision, "future"), reference, at.AddDays(-1), at.AddDays(45),
            [new(1, first, at.AddMonths(-1), null), new(2, second, at.AddMonths(-1), null)]);
        var from = new DateOnly(2048, 4, 1); var to = new DateOnly(2048, 5, 1);
        var input = new SwapContractObservationsInput("contract_observation_source_v1", "visible_corrections_at_cutoff_v1",
            "daily_settlement", "require_live", new("prompt_offset", null, 0, ContractRollRule.ExpiryImplementation, null),
            [..Enumerable.Range(0, 30).Select(n => new SwapContractObservationInput(n, from.AddDays(n), from.AddDays(n + 1), at.AddDays(n)))]);
        var resolved = SwapContractObservations.Resolve(sequence, input, from, to);
        if (resolved.Length != 30 || resolved.Take(15).Any(row => row.Contract != first.Key) ||
            resolved.Skip(15).Any(row => row.Contract != second.Key) || resolved[0].Observation.From == resolved[0].DeliveryFrom)
            throw new Exception("Daily observations must retain independently resolved monthly delivery contracts at the explicit roll boundary.");
        foreach (var invalid in new[] { input with { HistoryPolicy = "latest" }, input with { Observations = input.Observations.RemoveAt(0) },
            input with { Role = "index_fixing" }, input with { ExpiryPolicy = "" } })
            Reject("contract_observation_input", () => SwapContractObservations.Resolve(sequence, invalid, from, to), "closed source policy");
        var explicitRoll=input with { Selection=new("prompt_offset",null,0,ContractRollRule.ExplicitImplementation,
            [new(first.Key,at.AddDays(10)),new(second.Key,at.AddDays(40))]) };
        var rolled=SwapContractObservations.Resolve(sequence,explicitRoll,from,to);
        if(rolled.Take(10).Any(row=>row.Contract!=first.Key)||rolled.Skip(10).Any(row=>row.Contract!=second.Key))
            throw new Exception("Explicit roll boundaries must retain their independent selection dates.");
        var exact = input with { Selection = new("exact", first.Key, null, null, null) };
        Reject("source_expired", () => SwapContractObservations.Resolve(sequence, exact, from, to), "exact expired require-live contract");
        if (SwapContractObservations.Resolve(sequence, exact with { ExpiryPolicy = "carry_final_settlement" }, from, to)
            .Any(row => row.Contract != first.Key)) throw new Exception("Final carry silently rolled its exact contract.");
        Console.WriteLine("PASS contract observation admission: daily windows retain monthly contracts and explicit roll/final policies");
    }

    private static void CheckContractObservationInput()
    {
        const string input = """
        {"curve":"synthetic_futures","index":null,"settlement":null,"currency":"usd",
         "deliveryFrom":"2048-04-01","deliveryTo":"2048-05-01",
         "contractObservations":{"implementation":"contract_observation_source_v1",
          "historyPolicy":"visible_corrections_at_cutoff_v1","role":"daily_settlement","expiryPolicy":"require_live",
          "selection":{"mode":"prompt_offset","contract":null,"promptOffset":0,"roll":"contract_expiry_roll_v1","boundaries":null},
          "observations":[{"ordinal":0,"from":"2048-04-01","to":"2048-04-02","fixingAt":"2048-04-01T16:00:00Z"}]}}
        """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _ = JsonSerializer.Deserialize<SwapFloatingInput>(input, options)
            ?? throw new Exception("The explicit contract-observation input was discarded.");
        foreach (var field in new[] { "historyPolicy", "role", "expiryPolicy", "selection", "observations" })
        {
            var value = JsonNode.Parse(input)!;
            value["contractObservations"]!.AsObject().Remove(field);
            try { value.Deserialize<SwapFloatingInput>(options); }
            catch (JsonException) { continue; }
            throw new Exception("Contract observation source silently defaulted " + field);
        }
        foreach(var nested in new[]{"contract","revision","boundary"})
        {
            var value=JsonNode.Parse(input)!;
            var selection=value["contractObservations"]!["selection"]!;
            var key=JsonSerializer.SerializeToNode(new ContractKey(new(Guid.NewGuid(),Guid.NewGuid()),Guid.NewGuid()),options)!;
            if(nested=="boundary")
            {
                selection["boundaries"]=new JsonArray(new JsonObject{["contract"]=key,["rollAt"]="2048-04-20T16:00:00Z",["fallback"]="nearest"});
            }
            else
            {
                selection["contract"]=key;
                (nested=="revision"?key["revision"]!:key)["fallback"]="nearest";
            }
            try { value.Deserialize<SwapFloatingInput>(options); }
            catch(JsonException) { continue; }
            throw new Exception("An unknown nested contract observation "+nested+" policy was discarded.");
        }
        var unknown = JsonNode.Parse(input)!;
        unknown["contractObservations"]!["selection"]!["fallback"] = "nearest";
        try { unknown.Deserialize<SwapFloatingInput>(options); }
        catch (JsonException) { Console.WriteLine("PASS contract observation input: explicit source variant and required closed policies"); return; }
        throw new Exception("An unknown contract-selection policy was silently discarded.");
    }

    private static void CheckAdmissionMatrix()
    {
        var fixture = new AdmissionFixture();
        var data = fixture.Data();
        var curve = data.Definitions["curves"].Single();
        var quote = data.Quotes.Single();
        var target = PriceSettlementInputs.Resolve(data, curve, quote, new("curve_target", null, null));
        var alternate = PriceSettlementInputs.Resolve(data, curve, quote,
            new("override", "alternate_index", PriceSettlementSelection.ForwardCurveNoBasis));
        foreach (var (name, selection, mode, expectedCount) in new[]
        {
            ("GAS-M published monthly", target, FloatingAveraging.MonthlyIndexPrice, 1),
            ("GAS-D daily observations", target, FloatingAveraging.CalendarDayArithmetic, 30),
            ("explicit separate monthly settlement", alternate, FloatingAveraging.MonthlyIndexPrice, 1),
            ("explicit separate daily settlement", alternate, FloatingAveraging.CalendarDayArithmetic, 30)
        })
        {
            var preview = SwapObservationCompatibility.PreviewReferences(data, curve, selection, quote,
                AdmissionFixture.From, AdmissionFixture.To, mode, null);
            var admitted = SwapObservationCompatibility.Validate(data, curve, selection, quote,
                AdmissionFixture.From, AdmissionFixture.To, mode, null, []);
            if (preview.Observations.Length != expectedCount || admitted.Plan.Observations.Length != expectedCount ||
                !preview.Projections.SequenceEqual(admitted.Projections) || !preview.Fixings.SequenceEqual(admitted.Fixings) ||
                admitted.Fixings.Any(fixing => fixing.Index != selection.SelectedIndex) ||
                admitted.Projections.Any(projection => projection.Market.Curve.Code != "projection"))
                throw new Exception(name + ": discovery and admission must preserve independent exact projection and settlement references.");
            Console.WriteLine("PASS Side admission: " + name);
        }
        foreach (var (field, value) in new[]
        {
            ("commodity_code", "other_energy"), ("currency_code", "eur"),
            ("quote_unit_code", "kwh"), ("quote_multiplier", "100")
        })
        {
            var incompatible = fixture.Data(changeIndex: row => Change(row, field, value));
            Reject("price_settlement_selection", () => PriceSettlementInputs.Resolve(incompatible,
                incompatible.Definitions["curves"].Single(), incompatible.Quotes.Single(),
                new("override", "alternate_index", PriceSettlementSelection.ForwardCurveNoBasis)), field);
        }
        var monthlyOnly = fixture.Data(monthlyOnly: true);
        var monthlyCurve = monthlyOnly.Definitions["curves"].Single();
        if (SwapObservationCompatibility.SupportedSamplings(monthlyOnly, monthlyCurve, alternate)
            .Any(mode => mode.Averaging != FloatingAveraging.MonthlyIndexPrice))
            throw new Exception("A monthly-only settlement index offered daily sampling.");
        Reject("index_fixing_cadence", () => SwapObservationCompatibility.Validate(monthlyOnly, monthlyCurve,
            alternate, quote, AdmissionFixture.From, AdmissionFixture.To, FloatingAveraging.CalendarDayArithmetic,
            null, []), "monthly-only index cannot become daily");
        Console.WriteLine("PASS Side admission rejects: incompatible commodity, currency, unit, multiplier and unavailable daily cadence");
        foreach (var (name, change, error) in new (string, Func<JsonElement, JsonElement>, string)[]
        {
            ("missing actual series", row => Change(row, "series_id", Guid.NewGuid()), "index_fixing_definition"),
            ("wrong actual observation period", row => Change(row, "series_id", fixture.MonthlySeries), "index_fixing_definition"),
            ("undeclared actual source", row => Change(row, "source_code", "unpublished_source"), "index_fixing_definition")
        })
        {
            var invalid = fixture.Data(change);
            var selectedCurve = invalid.Definitions["curves"].Single();
            Reject(error, () => SwapObservationCompatibility.PreviewReferences(invalid, selectedCurve, alternate, quote,
                AdmissionFixture.From, AdmissionFixture.To, FloatingAveraging.CalendarDayArithmetic, null), name + " discovery");
            Reject(error, () => SwapObservationCompatibility.Validate(invalid, selectedCurve, alternate, quote,
                AdmissionFixture.From, AdmissionFixture.To, FloatingAveraging.CalendarDayArithmetic, null, []), name + " admission");
            Console.WriteLine("PASS Side admission rejects: " + name);
        }
    }

    private static void Reject(string code, Action action, string name)
    {
        try { action(); }
        catch (MarketDataException error) when (error.Code == code) { return; }
        throw new Exception(name + ": expected " + code);
    }

    private static JsonElement Row(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Change(JsonElement row, string field, object value)
    {
        var copy = JsonNode.Parse(row.GetRawText())!.AsObject();
        copy[field] = JsonSerializer.SerializeToNode(value);
        return JsonSerializer.SerializeToElement(copy);
    }

    private sealed class AdmissionFixture
    {
        internal static readonly DateOnly From = new(2048, 4, 1), To = new(2048, 5, 1);
        private readonly Guid catalog = Guid.NewGuid(), revision = Guid.NewGuid(), referenceCatalog = Guid.NewGuid(), referenceRevision = Guid.NewGuid();
        private readonly Guid[] contracts = Enumerable.Range(0, 62).Select(_ => Guid.NewGuid()).ToArray();
        private readonly Guid[] series = Enumerable.Range(0, 62).Select(_ => Guid.NewGuid()).ToArray();
        internal Guid MonthlySeries => series[31];
        internal PreviewData Data(Func<JsonElement, JsonElement>? changeDailyFixing = null,
            Func<JsonElement, JsonElement>? changeIndex = null, bool monthlyOnly = false, ImmutableArray<JsonElement> observations=default)
        {
            static (DateOnly From, DateOnly To) Period(int n) => n % 31 == 0 ? (From, To) : (From.AddDays(n % 31 - 1), From.AddDays(n % 31));
            var fixings = Enumerable.Range(0, 62).Select(n =>
            {
                var period = Period(n);
                var row = Row(new { reference_catalog_id = referenceCatalog, reference_revision_id = referenceRevision,
                    index_code = n < 31 ? "target_index" : "alternate_index", quote_code = "price", observation_from = period.From,
                    observation_to = period.To, fixing_at = "2048-05-02T12:00:00Z", series_id = series[n], source_code = "actual_source",
                    implementation = IndexFixing.SupportedImplementation });
                return n == 32 && changeDailyFixing is not null ? changeDailyFixing(row) : row;
            }).ToImmutableArray();
            Dictionary<string, ImmutableArray<JsonElement>> definitions = new()
            {
                ["revisions"] = [Row(new { catalog_id = catalog, id = revision, reference_catalog_id = referenceCatalog, reference_revision_id = referenceRevision, state = "sealed" })],
                ["contracts"] = [..contracts.Select((id, n) => Row(new { id, kind = "forward", delivery_from = Period(n).From, delivery_to = Period(n).To, expiry_at = "2048-05-03T00:00:00Z", futures_series_code = (string?)null }))],
                ["curves"] = [Row(new { code = "projection", commodity_code = "synthetic_energy", quote_code = "price", location_code = "hub", profile_code = "uniform", settlement_target_code = "target", rule = "direct", direct_role = "trader_mark", direct_source_code = "projection_source" })],
                ["curve_members"] = [..contracts.Take(31).Select((id, ordinal) => Row(new { curve_code = "projection", contract_id = id, ordinal }))],
                ["series"] = [..series.Select((id, n) => Row(new { id, contract_id = contracts[n], quote_code = "price", value_specification_code = (string?)null }))],
                ["index_fixing_observations"] = monthlyOnly ? [..fixings.Where(row => row.Text("index_code") != "alternate_index" || row.Text("observation_from") == "2048-04-01" && row.Text("observation_to") == "2048-05-01")] : fixings,
                ["curve_inputs"] = [], ["constituent_mappings"] = [], ["contract_intervals"] = [], ["curve_expiry_rules"] = []
            };
            Dictionary<string, ImmutableArray<JsonElement>> references = new()
            {
                ["quantity_specifications"] = [Row(new { code = "quantity", commodity_code = "synthetic_energy", basis_code = "energy", unit_code = "mwh", dimension_code = "energy" })],
                ["quote_specifications"] = [Row(new { code = "price", kind = "outright", currency_code = "usd", quantity_code = "quantity", multiplier = "1", trading_tick = "0.001", clearing_increment = "0.001", minimum = "-1000", maximum = "1000" })],
                ["price_index_definitions"] = [..new[] { "target_index", "alternate_index" }.Select(code => Row(new { code, commodity_code = "synthetic_energy", currency_code = "usd", quote_unit_code = "mwh", quote_multiplier = "1" })).Select(row => row.Text("code") == "alternate_index" && changeIndex is not null ? changeIndex(row) : row)],
                ["settlement_targets"] = [Row(new { code = "target", kind = "index", index_a_code = "target_index", index_b_code = (string?)null })],
                ["sources"] = [Row(new { code = "actual_source" }), Row(new { code = "projection_source" })],
                ["futures_milestones"] = [], ["value_specifications"] = []
            };
            return new(new("local-synthetic-preview", catalog, revision, referenceCatalog, referenceRevision, Guid.NewGuid(), Guid.NewGuid(), [], null), definitions, references, observations.IsDefault?[]:observations);
        }
    }

    private static void CheckReferenceInputs(ReferenceRevision revision)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var calendar = JsonSerializer.SerializeToNode(new SwapObservationScheduleInput(SwapStore.ObservationScheduleImplementation,
            new ReferenceKey(revision, "synthetic_calendar"), null), options)!;
        var weighted = JsonSerializer.SerializeToNode(new SwapObservationScheduleInput(SwapStore.ObservationScheduleImplementation,
            null, [new("2048-04-03", "2048-04-04", new("1", new(revision, "synthetic_quantity")))]), options)!;
        foreach (var (template, path) in new[]
        {
            (calendar, new[] { "calendar" }), (calendar, new[] { "calendar", "revision" }),
            (weighted, new[] { "quantities", "0", "quantity", "specification" }),
            (weighted, new[] { "quantities", "0", "quantity", "specification", "revision" })
        })
        {
            var input = template.DeepClone();
            JsonNode target = input;
            foreach (var part in path) target = target is JsonArray array ? array[int.Parse(part)]! : target[part]!;
            target["boundaryPolicy"] = "reject";
            try { input.Deserialize<SwapObservationScheduleInput>(options); }
            catch (JsonException) { continue; }
            throw new Exception("An unknown nested reference field was silently discarded: " + string.Join('.', path));
        }
        if (calendar.Deserialize<SwapObservationScheduleInput>(options)!.Calendar != new ReferenceKey(revision, "synthetic_calendar") ||
            weighted.Deserialize<SwapObservationScheduleInput>(options)!.Quantities!.Value[0].Quantity.Value != "1")
            throw new Exception("A complete exact reference changed during strict input binding.");
        Console.WriteLine("PASS Side observation inputs: unknown nested reference and revision members reject without changing valid exact keys");
    }
}
