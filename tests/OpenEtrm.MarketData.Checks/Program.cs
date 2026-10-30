using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData;
using OpenEtrm.Security;

if(args is ["--schedule-market-sources"]){ScheduleMarketSourceChecks.Run();return;}
if(args is ["--schedule-input-batch"]){await ScheduleInputBatchChecks.Run();return;}
if(args is ["--booked-side-program"]){BookedSideProgramChecks.Run();return;}
if(args is ["--schedule-program-execution"]){ScheduleProgramExecutionChecks.Run();return;}
if(args is ["--schedule-program-demand"]){ScheduleProgramDemandChecks.Run();return;}

if(args is ["--side-block-checks"]){SideBlockExperiment.Check();return;}
if(args is ["--compact-retained"]){CompactRetainedChecks.Run();return;}
if(args is ["--side-block-export",var blockOutput]){SideBlockExperiment.Export(blockOutput,12);return;}
if(args is ["--side-block-export",var blockOutputMonths,var blockMonths]){SideBlockExperiment.Export(blockOutputMonths,int.Parse(blockMonths,System.Globalization.CultureInfo.InvariantCulture));return;}
if(args is ["--side-block-evaluate",var blockInput,var blockResult]){SideBlockExperiment.Evaluate(blockInput,blockResult);return;}
if(args is ["--side-model-oracle"]){SideModelOracleChecks.Run();return;}
if(args is ["--schedule-graph"]){ScheduleGraphChecks.Run();return;}
if(args is ["--whole-term-payments"]){WholeTermPaymentChecks.Run();return;}
if(args is ["--retained-schedules"]){RetainedScheduleChecks.Run();ScheduleStorageWorkChecks.Run();return;}
if(args is ["--schedule-storage-work"]){ScheduleStorageWorkChecks.Run();return;}
if(args is ["--interval-schedules"]){IntervalScheduleChecks.Run();return;}

if(args is ["--ppa-connected"]){PpaConnectedChecks.Run();return;}
if(args is ["--curve-lookups"]){CurveLookupChecks.Run();return;}
if(args is ["--trade-contracts"]){TradeContractChecks.Run();return;}
if(args is ["--cross-commodity-swaps"]){CrossCommoditySwapChecks.Run();return;}
if(args is ["--observation-schedules"]){ObservationScheduleChecks.Run();return;}
if(args is ["--ppa-normalization-reuse"]){PpaNormalizationReuseChecks.Run();return;}
if(args is ["--series-index-acceptance"]){SeriesIndexAcceptanceChecks.Run();return;}
if(args is ["--ppa-summary"]){PpaSummaryChecks.Run();return;}
if(args is ["--ppa-break-even"]){PpaBreakEvenChecks.Run();return;}
if(args is ["--settlement-preview"]){SettlementPreviewChecks.Run();return;}
if(args is ["--ppa-sides"]){PpaBreakEvenChecks.Run();SettlementPreviewChecks.Run();PpaContractChecks.Run();PpaAcceptanceChecks.Run();PpaPreparationChecks.Run();return;}
if(args is ["--ppa-preparation"]){PpaPreparationChecks.Run();SeriesIndexWorkChecks.Run();return;}
if(args is ["--ppa-benchmark-acceptance"]){PpaBenchmark.CheckWorkloads();return;}
if(args is ["--ppa-benchmark-sample",var ppaCase,var sampleOutput]){PpaBenchmark.RunSample(ppaCase,sampleOutput);return;}
if(args is ["--ppa-benchmark",var ppaOutput]){PpaBenchmark.Run(ppaOutput);return;}
if(args is ["--book-valuation"]){BookValuationChecks.Run();return;}
if(args is ["--independent-sides"]){IndependentSideChecks.Run();return;}
if(args is ["--asset-volumes"]){Slice2AcceptanceChecks.Run();return;}
if(args is ["--swap-settlement"]){SwapSettlementChecks.Run();SettlementDeterminationChecks.Run();SwapLifecycleChecks.Run();SwapNetSettlementChecks.Run();SwapContractBridgeChecks.Run();return;}
if(args is ["--scheduled-swaps"]){ScheduledSwapChecks.Run();return;}
if(args is ["--monthly-swaps"]){MonthlySwapChecks.Run();return;}
if(args is ["--commodity-lookups"]){CommodityLookupChecks.Run();return;}
if(args is ["--commodity-lookup-benchmark"]){CommodityLookupChecks.Benchmark();return;}
if(args is ["--snapshot-inputs"]){SnapshotInputChecks.Run();return;}
if(args is ["--power-interval-quantities"]){PowerIntervalQuantityChecks.Run();return;}
if(args is ["--interval-data-benchmark",var intervalOutput]){IntervalDataBenchmark.Run(intervalOutput);return;}
if(args is ["--interval-data"]){IntervalDataChecks.Run();return;}
if(args is ["--interval-series"]){IntervalSeriesChecks.Run();return;}
if(args is ["--interval-curves"]){IntervalCurveChecks.Run();return;}
if(args is ["--block-shaping"]){BlockShapingChecks.Run();return;}
if(args is ["--interval-pricing"]){IntervalPricingChecks.Run();return;}
if(args is ["--interval-price-resolver"]){IntervalPriceResolverChecks.Run();IntervalAnchorChecks.Run();IntervalResolverReuseChecks.Run();return;}
if(args is ["--interval-resolver-reuse"]){IntervalResolverReuseChecks.Run();return;}
if(args is ["--interval-resolver-benchmark",var resolverOutput]){IntervalResolverBenchmark.Run(resolverOutput);return;}

if(args is ["--custom-curves"]){CustomCurveChecks.Run();return;}
if(args is ["--curve-kinds"]){CurveKindChecks.Run();return;}
if(args is ["--exact-curve-prices"]){ExactCurvePriceChecks.Run();return;}
if(args is ["--curve-engine-cutover"]){CurveEngineCutoverChecks.Run();return;}
if(args is ["--connected-curves"]){ConnectedCurveChecks.Run();return;}
if(args is ["--connected-scenarios"]){ConnectedScenarioChecks.Run();return;}
if(args is ["--connected-recipes"]){ConnectedRecipeChecks.Run();return;}
if(args is ["--custom-projection"]){CustomProjectionChecks.Run();return;}
if(args is ["--connected-portfolios"]){ConnectedPortfolioChecks.Run();return;}
if(args is ["--temporal-bindings"]){TemporalBindingChecks.Run();return;}
if(args is ["--connected-graph-benchmark",var graphOutput]){ConnectedGraphBenchmark.Run(graphOutput);return;}
if(args is ["--index-fixings"]){IndexFixingChecks.Run();return;}
if(args is ["--risk-propagation"]){RiskPropagationChecks.Run();return;}

if (args.Length > 0)
{
    if(args is ["--risk-benchmark",var output]){RiskBenchmark.Run(output);return;}
    if (args is ["--contract-sequences"])
    {
        ContractSequenceChecks.Run();
        ObservationContractChecks.Run();
        return;
    }
    if (args is ["--export-mixed-resolution", var destination])
    {
        new MixedResolutionFixture().ExportPreview(destination);
        return;
    }
    if (args is not ["--mixed-resolution"] && args is not ["--mixed-resolution", _])
        throw new ArgumentException("Use --mixed-resolution [csv-output-path], or no arguments for all checks.");
    MixedResolutionChecks.Run(args.Length == 2 ? args[1] : null, showExample: true);
    return;
}

await ScheduleInputBatchChecks.Run();
BookedSideProgramChecks.Run();
ScheduleMarketSourceChecks.Run();
ScheduleProgramExecutionChecks.Run();
ScheduleProgramDemandChecks.Run();
SideModelOracleChecks.Run();
WholeTermPaymentChecks.Run();
RetainedScheduleChecks.Run();
ScheduleStorageWorkChecks.Run();
ScheduleGraphChecks.Run();
IntervalScheduleChecks.Run();
IntervalDataChecks.Run();
PowerHoursChecks.Run();
CustomCurveChecks.Run();
CurveKindChecks.Run();
ExactCurvePriceChecks.Run();
var passed = PpaConnectedChecks.Run()+CrossCommoditySwapChecks.Run()+PpaBenchmark.CheckWorkloads()+ScheduledSwapChecks.Run()+SwapSettlementChecks.Run()+SettlementDeterminationChecks.Run()+SwapLifecycleChecks.Run()+SwapNetSettlementChecks.Run()+SwapContractBridgeChecks.Run();
void Check(string label, Action run) { run(); passed++; Console.WriteLine($"PASS {label}"); }
void Equal<T>(T actual, T expected) where T : notnull
{
    if (!actual.Equals(expected)) throw new Exception($"Expected {expected}; got {actual}.");
}
void Reject(string code, Action run)
{
    try { run(); } catch (MarketDataException e) when (e.Code == code) { return; }
    throw new Exception($"Expected rejection: {code}");
}
void Deny(Action run)
{
    try { run(); } catch (AccessDeniedException) { return; }
    throw new Exception("Expected access denial.");
}
void Invalid(Action run)
{
    try { run(); } catch (ArgumentException) { return; }
    throw new Exception("Expected invalid security state.");
}
var fixture = new Fixture();
var model = fixture.Model();
var request = ImmutableArray.Create(new CurveRequest(fixture.C("local_power"), fixture.K(5)));
var original = fixture.Snapshot();
Check("five layers: future -> gas forwards -> power forwards", () =>
{
    var result = model.Build(original, request);
    Equal(result.Values[0].Price.Value.ToString(), "29.1");
    Equal(result.Values[0].Lineage.Count, 6);
    Equal(result.EvaluatedConstituents, 9);
    Equal(result.NumericalPolicy, CurveModel.NumericalPolicy);
});
Check("pricing trace retains evaluated dependencies and exact multiplier evidence", () =>
{
    var build = model.Build(original, request);
    Equal(build.Evaluations.Length, build.EvaluatedConstituents);
    Equal(build.Evaluations.Select(e => (e.Result.Request,e.Use)).Distinct().Count(), build.EvaluatedConstituents);
    var hub = build.Evaluations.Single(e => e.Result.Request.Curve == fixture.C("hub_power"));
    Equal(hub.Multiplier!.Value.ToString(), "7");
    Equal(hub.MultiplierObservation!.Value, original.Selections.Single(s => s.Input.Series == fixture.S(6)).Observation.Key);
    Equal(hub.Inputs.Single(i => i.Slot == InputSlot.Base).Source, new CurveRequest(fixture.C("local_gas"),fixture.K(3)));
    var visited = new HashSet<(CurveRequest,InputUse)>();
    foreach (var evaluation in build.Evaluations)
    {
        foreach(var dependency in evaluation.Inputs)
            if(!visited.Contains((dependency.Source,dependency.Use)))throw new Exception("Evidence must precede its consumer");
        visited.Add((evaluation.Result.Request,evaluation.Use));
    }
    Equal(build.Evaluations[^1].Result, build.Values[0]);
});
Check("pricing trace contains only requested dependency closure", () =>
{
    var build = model.Build(original, [new(fixture.C("future"),fixture.K(1))]);
    Equal(build.Evaluations.Length,1);Equal(build.Evaluations[0].Inputs.Length,0);
    Equal(build.Evaluations[0].Multiplier is null,true);
});
Check("revised base observation propagates and old snapshot is unchanged", () =>
{
    var corrected = original with { Id = Guid.NewGuid(), Selections = original.Selections.Select(s =>
        s.Observation.Series == fixture.S(1) ? s with { Observation = s.Observation with {
            Key = new(fixture.Revision.CatalogId, Guid.NewGuid()), Value = ExactDecimal.Parse("3.05") } } : s).ToImmutableArray() };
    Equal(model.Build(corrected, request).Values[0].Price.Value.ToString(), "29.45");
    Equal(model.Build(original, request).Values[0].Price.Value.ToString(), "29.1");
});
Check("a new native snapshot reuses unaffected calculation slots without reusing old evidence",()=>
{
    var cache=new OpenEtrm.MarketData.Calculation.ConnectedCalculationCache(4,1000);
    var cached=fixture.Model(calculations:cache);
    var first=cached.Build(original,request);
    var next=original with{Id=Guid.NewGuid(),Selections=original.Selections.Select(s=>s.Observation.Series==fixture.S(1)?s with{
        Observation=s.Observation with{Key=new(fixture.Revision.CatalogId,Guid.NewGuid()),Value=ExactDecimal.Parse("3.05")}}:s).ToImmutableArray()};
    var corrected=cached.Build(next,request);
    Equal(corrected.Values[0].Price.Value.ToString(),"29.45");
    if(corrected.EvaluatedConstituents>=first.EvaluatedConstituents)throw new Exception("An independent native branch was recalculated.");
    Equal(corrected.Snapshot.Id,next.Id);Equal(first.Values[0].Price.Value.ToString(),"29.1");
});
Check("shared dependencies evaluate once across requested outputs", () =>
{
    var requests = request.Add(new(fixture.C("hub_power"), fixture.K(4)));
    Equal(model.Build(original, requests).EvaluatedConstituents, 9);
    var reversed = model.Build(original, requests.Reverse().ToImmutableArray());
    Equal(reversed.Values.Single(x => x.Request == request[0]).Price.Value.ToString(), "29.1");
});
Check("independent all-in location curve has no inherited base dependency", () =>
{
    var allIn = fixture.Direct("all_in", fixture.K(5), fixture.R("power_out"), SettlementKind.Index);
    var extended = fixture.Model(fixture.Curves.Add(allIn));
    var selection = fixture.Select(5, QuoteKind.Outright, "power_out", "29.1");
    var snapshots = original with { Selections = original.Selections.Add(selection) };
    var outputs = request.Add(new(fixture.C("all_in"), fixture.K(5)));
    var changed = snapshots with { Selections = snapshots.Selections.Select(s => s.Observation.Series == fixture.S(1)
        ? s with { Observation = s.Observation with { Value = ExactDecimal.Parse("3.1") } } : s).ToImmutableArray() };
    var result = extended.Build(changed, outputs);
    Equal(result.Values[0].Price.Value.ToString(), "29.8"); Equal(result.Values[1].Price.Value.ToString(), "29.1");
});
Check("missing observation raises instead of substituting zero", () => Reject("missing_observation", () =>
    model.Build(original with { Selections = original.Selections.RemoveAt(0) }, request)));
Check("duplicate variable selection rejected", () => Reject("duplicate_definition", () =>
    model.Build(original with { Selections = original.Selections.Add(original.Selections[0]) }, request)));
Check("preliminary source cannot masquerade as final selected mark", () => Reject("selection_mismatch", () =>
    model.Build(original with { Selections = original.Selections.SetItem(0, original.Selections[0] with {
        Observation = original.Selections[0].Observation with { State = PublicationState.Preliminary } }) }, request)));
Check("observation beyond explicit cutoff rejected", () => Reject("observation_cutoff", () =>
    model.Build(original with { MarketCutoff = Fixture.Utc("2040-12-17T00:00:00Z") }, request)));
Check("clearing input precision is validated without rounding", () => Reject("clearing_precision", () =>
    model.Build(original with { Selections = original.Selections.SetItem(0, original.Selections[0] with {
        Observation = original.Selections[0].Observation with { Value = ExactDecimal.Parse("3.0001") } }) }, request)));
Check("same contract cannot acquire incompatible location through another curve", () =>
{
    var curves = fixture.Curves.Add(fixture.Direct("wrong_location", fixture.K(5), fixture.R("power_out"), SettlementKind.Index) with { Location = fixture.R("other_location") });
    Reject("incompatible_membership", () => fixture.Model(curves));
});
Check("wrong snapshot definition pin rejected", () => Reject("snapshot_pin", () =>
    model.Build(original with { Definitions = new(fixture.Revision.CatalogId, Guid.NewGuid()) }, request)));
Check("reserved temporal end marker rejected; earlier year-9999 context remains representable", () =>
{
    Reject("snapshot_context", () => model.Build(original with { ValuationAt = Fixture.Utc("9999-12-31T00:00:00Z") }, request));
    Reject("source_expired", () => model.Build(original with { ValuationAt = Fixture.Utc("9999-01-01T00:00:00Z") }, request));
    Reject("contract_dates", () => fixture.Model(contracts: fixture.Contracts.SetItem(1, fixture.Contracts[1] with { DeliveryTo = new(9999, 12, 31) })));
});
Check("futures contract must preserve authoritative last-trading expiry", () => Reject("futures_specification", () =>
    fixture.Model(contracts: fixture.Contracts.SetItem(0, fixture.Contracts[0] with { Expiry = fixture.Contracts[0].Expiry.AddDays(1) }))));
Check("unspecified selection policy rejected", () => Reject("unsupported_kind", () =>
    model.Build(original with { SelectionPolicy = 0 }, request)));
Check("dependency cycle rejected before evaluation", () =>
{
    var curves = fixture.Curves.Select(c => c.Key == fixture.C("base_forward") ? c with {
        Inputs = c.Inputs.SetItem(0, fixture.Input("local_gas", fixture.K(2), fixture.K(3), InputSlot.Base)) } : c).ToImmutableArray();
    Reject("dependency_cycle", () => fixture.Model(curves));
});
Check("missing mapping cannot be inferred from matching month", () =>
{
    var curves = fixture.Curves.Select(c => c.Key == fixture.C("base_forward") ? c with {
        Inputs = c.Inputs.SetItem(0, c.Inputs[0] with { Mappings = [] }) } : c).ToImmutableArray();
    Reject("mapping_coverage", () => fixture.Model(curves));
});
Check("incompatible measurement basis rejected even with same dimensions", () =>
{
    var quote = fixture.Quotes.Single(q => q.Key == fixture.R("gas_basis"));
    var quotes = fixture.Quotes.Replace(quote, quote with { Quantity = fixture.R("power_quantity") });
    Reject("spread_type", () => fixture.Model(quotes: quotes));
});
Check("outright mark cannot be put into spread slot", () =>
{
    var curves = fixture.Curves.Select(c => c.Key == fixture.C("bridge_basis") ? c with { Quote = fixture.R("gas_out") } : c).ToImmutableArray();
    Reject("spread_type", () => fixture.Model(curves));
});
Check("source expiry requires an explicit live/frozen policy", () => Reject("source_expired", () =>
    model.Build(original with { ValuationAt = Fixture.Utc("2040-12-29T00:00:00Z") }, request)));
Check("expired source can explicitly consume its frozen final settlement", () =>
{
    var curves = fixture.Curves.Select(c => c.Key == fixture.C("base_forward") ? c with {
        Inputs = c.Inputs.SetItem(0, c.Inputs[0] with { ExpiryRule = SourceExpiryRule.FreezeFinalFuture }) } : c).ToImmutableArray();
    var frozenModel = fixture.Model(curves);
    var late = original with { ValuationAt = Fixture.Utc("2040-12-29T00:00:00Z"), MarketCutoff = Fixture.Utc("2040-12-29T00:00:00Z") };
    Reject("missing_observation", () => frozenModel.Build(late, request));
    var final = fixture.Select(1, QuoteKind.Outright, "gas_out", "3.2") with {
        Input = new(fixture.S(1), InputUse.FrozenFact) };
    final = final with { Observation = final.Observation with { Role = ObservationRole.FinalContractSettlement,
        ObservedAt = Fixture.Utc("2040-12-28T00:00:00Z") } };
    late = late with { Selections = late.Selections.Add(final) };
    var frozenBuild=frozenModel.Build(late, request);
    Equal(frozenBuild.Values[0].Price.Value.ToString(), "30.5");
    var frozenEvidence=frozenBuild.Evaluations.Single(e=>e.Result.Request.Curve==fixture.C("future"));
    Equal(frozenEvidence.Use,InputUse.FrozenFact);Equal(frozenEvidence.Result.Price.Value.ToString(),"3.2");
    var misleadingLiveValue = late with { Selections = late.Selections.SetItem(0, late.Selections[0] with {
        Observation = late.Selections[0].Observation with { Value = ExactDecimal.Parse("900") } }) };
    Equal(frozenModel.Build(misleadingLiveValue, request).Values[0].Price.Value.ToString(), "30.5");
});
Check("invalid source expiry kind rejected", () =>
{
    var curves = fixture.Curves.Select(c => c.Key == fixture.C("local_gas") ? c with {
        Inputs = c.Inputs.SetItem(0, c.Inputs[0] with { ExpiryRule = SourceExpiryRule.FreezeFinalFuture }) } : c).ToImmutableArray();
    Reject("unsupported_expiry", () => fixture.Model(curves));
});
Check("same snapshot stays deterministic across threads", () =>
{
    Parallel.For(0, 40, _ => Equal(model.Build(original, request).Values[0].Price.Value.ToString(), "29.1"));
});
Check("dated numeric series input changes only its explicit dependents", () =>
{
    var ratio = original.Selections.Single(s => s.Input.Series == fixture.S(6));
    var changed = original with { Id = Guid.NewGuid(), Selections = original.Selections.Replace(ratio,
        ratio with { Observation = ratio.Observation with { Key = new(fixture.Revision.CatalogId, Guid.NewGuid()), Value = ExactDecimal.Parse("8") } }) };
    Equal(model.Build(changed, request).Values[0].Price.Value.ToString(), "32.4");
    Equal(model.Build(original, request).Values[0].Price.Value.ToString(), "29.1");
});
Check("missing numeric series never becomes an implicit constant", () => Reject("missing_observation", () =>
    model.Build(original with { Selections = original.Selections.RemoveAt(5) }, request)));
Check("explicit typed constant is supported without a synthetic observation", () =>
{
    var curves = fixture.Curves.Select(c => c.Rule == CurveRule.ScaleAdd ? c with {
        Multiplier = new(null, fixture.R("conversion_ratio"), ExactDecimal.Parse("7"), null, null) } : c).ToImmutableArray();
    var result = fixture.Model(curves).Build(original with { Selections = original.Selections.RemoveAt(5) }, request);
    Equal(result.Values[0].Price.Value.ToString(), "29.1"); Equal(result.Values[0].Lineage.Count, 5);
    var evidence=result.Evaluations.Single(e=>e.Result.Request.Curve==fixture.C("hub_power"));
    Equal(evidence.Multiplier!.Value.ToString(),"7");Equal(evidence.MultiplierObservation is null,true);
});
Check("numeric input cannot specify both series and constant", () =>
{
    var curves = fixture.Curves.Select(c => c.Rule == CurveRule.ScaleAdd ? c with {
        Multiplier = c.Multiplier! with { Constant = ExactDecimal.Parse("7") } } : c).ToImmutableArray();
    Reject("numeric_input", () => fixture.Model(curves));
});
Check("raw price history retains dated identity, observation time and correction pins", () =>
{
    var originalObservation = original.Selections[0].Observation;
    var dayBefore = originalObservation with { Key = new(fixture.Revision.CatalogId, Guid.NewGuid()),
        ObservedAt = originalObservation.ObservedAt.AddDays(-1), Value = ExactDecimal.Parse("2.95") };
    var correction = originalObservation with { Key = new(fixture.Revision.CatalogId, Guid.NewGuid()), Value = ExactDecimal.Parse("3.05") };
    var unrelated = original.Selections[1].Observation;
    var archive = new ObservationArchive([dayBefore, originalObservation, correction, unrelated]);
    var oldPin = ImmutableHashSet.Create(dayBefore.Key, originalObservation.Key, unrelated.Key);
    var from = dayBefore.ObservedAt; var to = originalObservation.ObservedAt.AddDays(1);
    var history = archive.ReadPinnedHistory(fixture.S(1), from, to, oldPin);
    Equal(history.Length, 2); Equal(history[0].Value.ToString(), "2.95"); Equal(history[1].Value.ToString(), "3");
    Equal(archive.ReadPinnedHistory(fixture.S(1), from, to, oldPin.Add(correction.Key)).Length, 3);
    Equal(archive.ReadPinnedHistory(fixture.S(1), from, originalObservation.ObservedAt, oldPin).Length, 1);
    Equal(archive.ReadPinnedHistory(fixture.S(1), from.AddDays(-1), from, oldPin).Length, 0);
    Reject("missing_observation", () => archive.ReadPinnedHistory(fixture.S(1), from, to, oldPin.Add(new(fixture.Revision.CatalogId, Guid.NewGuid()))));
});
Check("numeric parameter roles cannot be used for direct price curves", () =>
{
    var curves = fixture.Curves.Select(c => c.Key == fixture.C("gas_basis") ? c with { DirectRole = ObservationRole.Parameter } : c).ToImmutableArray();
    Reject("unsupported_role", () => fixture.Model(curves));
    var price = original.Selections[1];
    Reject("series_type", () => model.Build(original with { Selections = original.Selections.SetItem(1,
        price with { Observation = price.Observation with { Role = ObservationRole.Parameter } }) }, request));
});
Check("raw history enforces scoped identity and finite UTC event times", () =>
{
    var observation = original.Selections[0].Observation;
    Reject("archive_identity", () => new ObservationArchive([observation with { Series = default }]));
    Reject("archive_identity", () => new ObservationArchive([observation with { Source = default }]));
    Reject("archive_identity", () => new ObservationArchive([observation with { Role = (ObservationRole)99 }]));
    Reject("archive_identity", () => new ObservationArchive([observation with { ObservedAt = Fixture.Utc("9999-12-31T00:00:00Z") }]));
    var archive = new ObservationArchive([observation]);
    Reject("history_context", () => archive.ReadPinnedHistory(default, observation.ObservedAt, observation.ObservedAt.AddDays(1), ImmutableHashSet.Create(observation.Key)));
    Equal(archive.ReadPinnedHistory(fixture.S(1), observation.ObservedAt, Fixture.Utc("9999-12-31T00:00:00Z"), ImmutableHashSet.Create(observation.Key)).Length, 1);
});
Check("common roles grant exact capabilities without implicit administration", () =>
{
    Equal(CommonRoles.Templates.Length, 7);
    Equal(string.Join(",", CommonRoles.Templates.Single(r => r.Code == "agent_reader").Permissions.Order()), "ReadMarketData,ReadTrades,ReadTradeParties,ReadJobs,DraftCustomCurves");
    Equal(string.Join(",", CommonRoles.Templates.Where(r => r.Permissions.Contains(Permission.DraftCustomCurves)).Select(r => r.Code)), "agent_reader");
    Equal(string.Join(",", CommonRoles.Templates.Where(r => r.Permissions.Contains(Permission.ReadJobs)).Select(r => r.Code)),
        string.Join(",", CommonRoles.Templates.Where(r => r.Permissions.Contains(Permission.RunJobs)).Select(r => r.Code).Append("agent_reader")));
    Equal(string.Join(",", CommonRoles.Templates.Where(r => r.Permissions.Contains(Permission.UseAgents)).Select(r => r.Code)), "trader,risk_analyst,market_data_editor");
    Equal(string.Join(",", CommonRoles.Templates.Where(r => r.Permissions.Contains(Permission.ManageAgents)).Select(r => r.Code)), "access_administrator");
    var editor = CommonRoles.Templates.Single(r => r.Code == "market_data_editor");
    Equal(editor.Permissions.Contains(Permission.ImportObservations), true);
    Equal(editor.Permissions.Contains(Permission.SealMarketDefinitions), false);
    Equal(CommonRoles.Templates.Single(r => r.Code == "access_administrator").Permissions.Contains(Permission.ReadMarketData), false);
});
Check("exact decimal arithmetic uses no binary floating point", () =>
    Equal((ExactDecimal.Parse("0.1") + ExactDecimal.Parse("0.2")).ToString(), "0.3"));
Check("excess input precision is rejected", () => Reject("numeric_format", () => ExactDecimal.Parse("0.0000000000000000001")));
Check("numeric input rejects trailing newlines and whitespace", () =>
{
    foreach (var text in new[] { "1\n", "1.0\n", " 1", "1 ", "+1", "1e0" }) Reject("numeric_format", () => ExactDecimal.Parse(text));
});
Check("product requiring rounding is rejected", () => Reject("precision_loss", () => {
    _ = ExactDecimal.Parse("0.000000000000000001") * ExactDecimal.Parse("0.1"); }));
Check("numeric overflow rejected", () => Reject("numeric_range", () => {
    _ = ExactDecimal.Parse("999999999999999999999999") + ExactDecimal.Parse("1"); }));
Check("quantity times quoted price applies explicit quote multiplier", () =>
{
    var quote = fixture.Quotes[0] with { Multiplier = ExactDecimal.Parse("0.01") };
    var amount = new UnitPrice(ExactDecimal.Parse("325"), quote).Times(new Quantity(ExactDecimal.Parse("10"), quote.Quantity));
    Equal(amount.Amount.ToString(), "32.5"); Equal(amount.Currency, quote.Currency);
});
Check("price times mismatched quantity rejected", () => Reject("quantity_mismatch", () =>
    new UnitPrice(ExactDecimal.Parse("3"), fixture.Quotes[0]).Times(new Quantity(ExactDecimal.Parse("1"), fixture.R("power_quantity")))));
Check("money addition rejects cross currency and cross revision", () =>
{
    var money = new Money(ExactDecimal.Parse("1"), fixture.R("currency"));
    Reject("currency_mismatch", () => money.Add(new(ExactDecimal.Parse("2"), fixture.R("currency_b"))));
    Reject("currency_mismatch", () => money.Add(new(ExactDecimal.Parse("2"), new(new(Guid.NewGuid(), Guid.NewGuid()), "currency"))));
});
var rounding = new CashRoundingConvention(fixture.R("round_cash"), fixture.R("currency"), ExactDecimal.Parse("0.01"), CashTieRule.ToEven, CashRoundingStage.Cashflow);
Check("explicit cash rounding handles positive/negative ties", () =>
{
    Equal(new Money(ExactDecimal.Parse("1.005"), rounding.Currency).Round(rounding, CashRoundingStage.Cashflow).Amount.ToString(), "1");
    Equal(new Money(ExactDecimal.Parse("-1.015"), rounding.Currency).Round(rounding, CashRoundingStage.Cashflow).Amount.ToString(), "-1.02");
    Equal(new Money(ExactDecimal.Parse("-1.005"), rounding.Currency).Round(rounding with { TieRule = CashTieRule.AwayFromZero }, CashRoundingStage.Cashflow).Amount.ToString(), "-1.01");
});
Check("rounding stage is never silently selected", () => Reject("rounding_policy", () =>
    new Money(ExactDecimal.Parse("1.005"), rounding.Currency).Round(rounding, CashRoundingStage.PaymentTotal)));

var human = Guid.NewGuid(); var agent = Guid.NewGuid(); var service = Guid.NewGuid();
var scope = new Scope(Guid.NewGuid(), Guid.NewGuid());
var from = Fixture.Utc("2040-01-01T00:00:00Z"); var to = from.AddDays(10); var now = from.AddDays(1);
var roleId = Guid.NewGuid(); var delegationId = Guid.NewGuid();
var principals = ImmutableArray.Create(new Principal(human, PrincipalKind.Human, true), new(agent, PrincipalKind.Agent, true), new(service, PrincipalKind.Service, true));
var bindings = ImmutableArray.Create(new IdentityBinding("https://identity.invalid", "human-subject", human, LoginKind.HumanInteractive),
    new("https://identity.invalid", "agent-subject", agent, LoginKind.AgentCredential));
var role = new Role(roleId, ImmutableHashSet.Create(Permission.ReadMarketData, Permission.CreateDelegations));
var grants = ImmutableArray.Create(new RoleGrant(Guid.NewGuid(), human, roleId, scope, from, to, false),
    new(Guid.NewGuid(), agent, roleId, scope, from, to, false));
var delegation = new AgentDelegation(delegationId, agent, human, scope, ImmutableHashSet.Create(Permission.ReadMarketData), from, to, false);
SecurityState State(ImmutableArray<RoleGrant>? gs = null, ImmutableArray<Principal>? ps = null, AgentDelegation? d = null) =>
    new(Guid.NewGuid(), ps ?? principals, bindings, [role], gs ?? grants, [d ?? delegation]);
Check("agent has separate principal and agent login binding", () => Equal(State().ResolveVerifiedIdentity("https://identity.invalid", "agent-subject", LoginKind.AgentCredential), agent));
Check("agent binding rejects a different verified login kind", () => Deny(() => State().ResolveVerifiedIdentity("https://identity.invalid", "agent-subject", LoginKind.HumanInteractive)));
Check("unbound identity denied", () => Deny(() => State().ResolveVerifiedIdentity("https://identity.invalid", "absent", LoginKind.AgentCredential)));
Check("agent login cannot bind a human principal", () => Invalid(() => new SecurityState(Guid.NewGuid(), principals,
    [new IdentityBinding("https://identity.invalid", "agent", human, LoginKind.AgentCredential)], [role], grants, [delegation])));
Check("human can use its exact current scoped grant", () => Equal(State().AuthorizeCurrent(human, scope, Permission.ReadMarketData, now, null).ActorId, human));
Check("unknown operation is denied", () => Deny(() => State().AuthorizeCurrent(human, scope, (Permission)99, now, null)));
Check("no grant means denial", () => Deny(() => State().AuthorizeCurrent(service, scope, Permission.ReadMarketData, now, null)));
Check("cross workspace and tenant grants do not leak", () =>
{
    Deny(() => State().AuthorizeCurrent(human, scope with { WorkspaceId = Guid.NewGuid() }, Permission.ReadMarketData, now, null));
    Deny(() => State().AuthorizeCurrent(human, scope with { TenantId = Guid.NewGuid() }, Permission.ReadMarketData, now, null));
});
Check("read permission cannot perform administration", () => Deny(() => State().AuthorizeCurrent(human, scope, Permission.ManageAccess, now, null)));
Check("expired permission is excluded at exact half-open boundary", () => Deny(() => State().AuthorizeCurrent(human, scope, Permission.ReadMarketData, to, null)));
Check("revoked permission is denied", () => Deny(() => State(grants.SetItem(0, grants[0] with { Revoked = true })).AuthorizeCurrent(human, scope, Permission.ReadMarketData, now, null)));
Check("disabled principal is denied", () => Deny(() => State(ps: principals.SetItem(0, principals[0] with { Enabled = false })).AuthorizeCurrent(human, scope, Permission.ReadMarketData, now, null)));
Check("disabled agent is denied", () => Deny(() => State(ps: principals.SetItem(1, principals[1] with { Enabled = false })).AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId)));
Check("disabled delegator removes agent authority", () => Deny(() => State(ps: principals.SetItem(0, principals[0] with { Enabled = false })).AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId)));
Check("agent cannot bypass delegation using own grant", () => Deny(() => State().AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, null)));
Check("agent delegation retains both actor and delegator", () =>
{
    var decision = State().AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId);
    Equal(decision.ActorId, agent); Equal(decision.EffectivePrincipalId, human); Equal(decision.DelegationId!.Value, delegationId);
});
Check("revoked delegator grant immediately removes agent authority in current state", () => Deny(() =>
    State(grants.SetItem(0, grants[0] with { Revoked = true })).AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId)));
Check("revoked delegation is denied", () => Deny(() => State(d: delegation with { Revoked = true }).AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId)));
Check("delegation cannot increase operation privileges", () => Deny(() => State().AuthorizeCurrent(agent, scope, Permission.ManageAccess, now, delegationId)));
Check("delegation permission list narrows otherwise allowed grants", () => Deny(() =>
    State(d: delegation with { Permissions = ImmutableHashSet<Permission>.Empty }).AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId)));
Check("delegation cannot replace the agent's own permission", () => Deny(() =>
    State(grants.RemoveAt(1)).AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId)));
Check("delegation scope and expiry are checked independently", () =>
{
    Deny(() => State(d: delegation with { Scope = scope with { WorkspaceId = Guid.NewGuid() } }).AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId));
    Deny(() => State(d: delegation with { To = now }).AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId));
});
Check("delegator must retain permission to delegate", () => Deny(() =>
    new SecurityState(Guid.NewGuid(), principals, bindings, [role with { Permissions = ImmutableHashSet.Create(Permission.ReadMarketData) }], grants, [delegation])
        .AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId)));
Check("delegation cannot be borrowed by another agent", () =>
{
    var other = new Principal(Guid.NewGuid(), PrincipalKind.Agent, true);
    Deny(() => State(ps: principals.Add(other), d: delegation with { AgentId = other.Id }).AuthorizeCurrent(agent, scope, Permission.ReadMarketData, now, delegationId));
});
passed += MixedResolutionChecks.Run();
ContractSequenceChecks.Run();
ObservationContractChecks.Run();
passed += ExpiryTransitionChecks.Run();
passed += PowerCurveChecks.Run();
passed += TypedPriceChecks.Run();
passed += AmountUnitChecks.Run();
passed += PilotCloseChecks.Run();
passed += PowerQuantityChecks.Run();
passed += PowerIntervalQuantityChecks.Run();
passed += IntervalSeriesChecks.Run();
passed += IntervalCurveChecks.Run();
passed += BlockShapingChecks.Run();
passed += IntervalPricingChecks.Run();
passed += IntervalPriceResolverChecks.Run();
passed += IntervalAnchorChecks.Run();
passed += Slice2AcceptanceChecks.Run();
PpaBreakEvenChecks.Run();
SettlementPreviewChecks.Run();
passed += PpaContractChecks.Run();
passed += PpaAcceptanceChecks.Run();
passed += PpaSummaryChecks.Run();
passed += PpaPreparationChecks.Run();
SeriesIndexWorkChecks.Run();passed++;
PpaNormalizationReuseChecks.Run();passed++;
SeriesIndexAcceptanceChecks.Run();passed++;
passed += IntervalResolverReuseChecks.Run();
passed += ContractIdentityChecks.Run() + TradingIdentityChecks.Run();
passed += FuturesChecks.Run();
passed += ExchangeFuturesChecks.Run();
passed += SwapChecks.Run();
passed += ObservationScheduleChecks.Run();
passed += CommodityLookupChecks.Run();
passed += SnapshotInputChecks.Run();
passed += MonthlySwapChecks.Run();
passed += IndexFixingChecks.Run();
passed += PaymentRuleChecks.Run();
passed += IndependentSideChecks.Run();
passed += RiskChecks.Run();
passed += NestedScenarioChecks.Run();
passed += RiskPropagationChecks.Run();
passed += BookValuationChecks.Run();
passed += InstrumentRiskChecks.Run();
passed += TradeContractChecks.Run();
passed += CurveLookupChecks.Run();
CurveEngineCutoverChecks.Run();
ConnectedCurveChecks.Run();
passed += ConnectedScenarioChecks.Run();
ConnectedRecipeChecks.Run();
CustomProjectionChecks.Run();
ConnectedPortfolioChecks.Run();
passed += TemporalBindingChecks.Run();
passed += BasisRiskChecks.Run();
Console.WriteLine($"{passed} managed core checks passed.");

sealed class Fixture
{
    public DefinitionRevision Revision { get; } = new(Guid.NewGuid(), Guid.NewGuid());
    public ReferenceRevision Reference { get; } = new(Guid.NewGuid(), Guid.NewGuid());
    public ReferenceKey R(string code) => new(Reference, code);
    public SeriesKey S(int n) => new(Revision, Guid.Parse($"10000000-0000-0000-0000-{n:000000000000}"));
    public CurveKey C(string code) => new(Revision, code);
    public ContractKey K(int n) => new(Revision, Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}"));
    public static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
    public ImmutableArray<QuantitySpecification> Quantities { get; }
    public ImmutableArray<QuoteSpecification> Quotes { get; }
    public ImmutableArray<MarketContract> Contracts { get; }
    public ImmutableArray<Curve> Curves { get; }
    public Fixture()
    {
        Quantities = [new(R("gas_quantity"), R("gas"), R("gas_energy"), R("energy_a"), R("energy")),
            new(R("power_quantity"), R("power"), R("power_energy"), R("energy_b"), R("energy"))];
        QuoteSpecification Quote(string code, string quantity, QuoteKind kind) => new(R(code), kind, R("currency"), R(quantity),
            ExactDecimal.Parse("1"), ExactDecimal.Parse("0.025"), ExactDecimal.Parse("0.001"), ExactDecimal.Parse("-1000"), ExactDecimal.Parse("1000"));
        Quotes = [Quote("gas_out", "gas_quantity", QuoteKind.Outright), Quote("gas_basis", "gas_quantity", QuoteKind.Basis),
            Quote("power_out", "power_quantity", QuoteKind.Outright), Quote("power_basis", "power_quantity", QuoteKind.Basis)];
        Contracts = Enumerable.Range(1, 5).Select(n => new MarketContract(K(n), n == 1 ? ContractKind.Future : ContractKind.Forward,
            new(2041, 1, 1), new(2041, 2, 1), Utc(n == 1 ? "2040-12-28T00:00:00Z" : "2041-02-01T00:00:00Z"),
            n == 1 ? R("future_jan") : null)).ToImmutableArray();
        Curve Add(string code, int target, string quote, string source, int sourceContract, string spread, CurveRule rule = CurveRule.Add) =>
            new(C(code), R(quote), R("location"), R("uniform"), new(SettlementKind.Index, R("index"), null), rule,
                [K(target)], [Input(source, K(target), K(sourceContract), InputSlot.Base), Input(spread, K(target), K(target), InputSlot.Spread)], null, null,
                rule == CurveRule.ScaleAdd ? new(S(6), null, null, ObservationRole.Parameter, R("source")) : null);
        Curves = [Direct("future", K(1), R("gas_out"), SettlementKind.FutureSeries),
            Direct("bridge_basis", K(2), R("gas_basis"), SettlementKind.IndexDifference),
            Add("base_forward", 2, "gas_out", "future", 1, "bridge_basis"),
            Direct("gas_basis", K(3), R("gas_basis"), SettlementKind.IndexDifference),
            Add("local_gas", 3, "gas_out", "base_forward", 2, "gas_basis"),
            Direct("power_spread", K(4), R("power_basis"), SettlementKind.IndexDifference),
            Add("hub_power", 4, "power_out", "local_gas", 3, "power_spread", CurveRule.ScaleAdd),
            Direct("power_basis", K(5), R("power_basis"), SettlementKind.IndexDifference),
            Add("local_power", 5, "power_out", "hub_power", 4, "power_basis")];
    }
    // An interval labelled from its own local time on the zone's clock; occurrence 2 is the second pass through a repeated hour.
    public static DeliveryInterval Interval(TimeZoneInfo zone, DateTimeOffset start, int minutes, int occurrence = 1)
    {
        var local = TimeZoneInfo.ConvertTime(start, zone);
        return new(start, start.AddMinutes(minutes), DateOnly.FromDateTime(local.DateTime), local.Hour, local.Minute, occurrence);
    }
    public MarketContract IntervalContract(int n, DeliveryInterval interval) =>
        new(K(n), ContractKind.Forward, interval.OperatingDate, interval.OperatingDate.AddDays(1), interval.End, null, interval);
    public CurveInput Input(string source, ContractKey target, ContractKey contract, InputSlot slot) =>
        new(slot, C(source), SourceExpiryRule.RequireLive, [new(target, contract)]);
    public Curve Direct(string code, ContractKey member, ReferenceKey quote, SettlementKind settlement) =>
        new(C(code), quote, R("location"), R("uniform"), new(settlement, settlement == SettlementKind.FutureSeries ? null : R("index"),
            settlement == SettlementKind.IndexDifference ? R("index_b") : null), CurveRule.Direct, [member], [],
            settlement == SettlementKind.FutureSeries ? ObservationRole.DailySettlement : ObservationRole.TraderMark, R("source"), null);
    public CurveModel Model(ImmutableArray<Curve>? curves = null, ImmutableArray<QuoteSpecification>? quotes = null,
        ImmutableArray<MarketContract>? contracts = null, ImmutableArray<ValueSpecification>? valueSpecs = null,
        OpenEtrm.MarketData.Calculation.ConnectedCalculationCache? calculations=null) =>
        new(Revision, Reference, Quantities, quotes ?? Quotes, contracts ?? Contracts, curves ?? Curves,
            [new(R("future_jan"), R("gas_quantity"), R("currency"), ExactDecimal.Parse("1"), ExactDecimal.Parse("0.025"),
                R("location"), R("uniform"), new(2041, 1, 1), new(2041, 2, 1), Utc("2040-12-28T00:00:00Z"),
                R("gas_quantity"), ExactDecimal.Parse("40"), LotBasis.FixedTotal, Utc("2040-01-01T00:00:00Z"),"cash")],
            valueSpecs ?? [new(R("conversion_ratio"), NumericKind.QuantityRatio, R("gas_quantity"), R("power_quantity"), ExactDecimal.Parse("0"), ExactDecimal.Parse("100"))],
            [new(S(1), K(1), R("gas_out"), null), new(S(2), K(2), R("gas_basis"), null),
             new(S(3), K(3), R("gas_basis"), null), new(S(4), K(4), R("power_basis"), null),
             new(S(5), K(5), R("power_basis"), null), new(S(6), null, null, R("conversion_ratio")),
             new(S(7), K(5), R("power_out"), null), new(S(8), K(2), R("gas_out"), null)],calculations);
    public SelectedObservation Select(int contract, QuoteKind measure, string quote, string value)
    {
        var id = contract == 5 && measure == QuoteKind.Outright ? 7 : contract;
        return new(new(S(id), InputUse.Variable), new(new(Revision.CatalogId, Guid.NewGuid()), S(id), R("source"),
            contract == 1 ? ObservationRole.DailySettlement : ObservationRole.TraderMark, PublicationState.Final,
            Utc("2040-12-18T00:00:00Z"), ExactDecimal.Parse(value)));
    }
    public MarketSnapshot Snapshot() => new(Guid.NewGuid(), Revision, Utc("2040-12-18T00:00:00Z"), Utc("2040-12-18T00:00:00Z"),
        InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
        [Select(1, QuoteKind.Outright, "gas_out", "3"), Select(2, QuoteKind.Basis, "gas_basis", "0.1"),
         Select(3, QuoteKind.Basis, "gas_basis", "0.2"), Select(4, QuoteKind.Basis, "power_basis", "4"), Select(5, QuoteKind.Basis, "power_basis", "2"),
         new(new(S(6), InputUse.Variable), new(new(Revision.CatalogId, Guid.NewGuid()), S(6), R("source"), ObservationRole.Parameter,
             PublicationState.Final, Utc("2040-12-18T00:00:00Z"), ExactDecimal.Parse("7")))]);
}

// An exact test-side rational, independent of the implementation's arithmetic.
internal readonly record struct Fraction
{
    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }
    public Fraction(BigInteger numerator,BigInteger denominator)
    {
        if(denominator<0){numerator=-numerator;denominator=-denominator;}
        var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator),denominator);
        Numerator=numerator/gcd;Denominator=denominator/gcd;
    }
    public static Fraction operator +(Fraction a,Fraction b)=>new(a.Numerator*b.Denominator+b.Numerator*a.Denominator,a.Denominator*b.Denominator);
    public static Fraction operator -(Fraction a,Fraction b)=>new(a.Numerator*b.Denominator-b.Numerator*a.Denominator,a.Denominator*b.Denominator);
    public static Fraction operator *(Fraction a,Fraction b)=>new(a.Numerator*b.Numerator,a.Denominator*b.Denominator);
}
