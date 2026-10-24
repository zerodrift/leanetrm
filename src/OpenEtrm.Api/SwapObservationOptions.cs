using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapObservationOptionsRequest(FuturesReadRequest Context, Guid? DefinitionRevisionId = null,
    string? Commodity = null, string? Index = null, string? Curve = null, string? ResetFrequency = null,
    string? AveragingMethod = null, string? DeliveryFrom = null, string? DeliveryTo = null,
    CustomCurveProjectionInput? CustomProjection = null, ReferenceKey? ObservationCalendar = null);

public sealed record SideObservationUnavailable(string Code, string Message);
public sealed record SideDeliveryPeriod(string ResetFrequency,string AveragingMethod,string DeliveryFrom,string DeliveryTo);
public sealed record SideObservationChoice(Guid DefinitionRevisionId, string Curve, string Index,
    string ResetFrequency, string AveragingMethod, bool Available, ImmutableArray<SideObservationUnavailable> Reasons);

public sealed partial class SwapStore
{
    private static void RequireObservationInputs(PreviewData data,InputFrame frame,PlannedSideObservationReferences references,bool custom,CancellationToken ct)
    {
        var snapshot=data.Snapshot(frame);
        var requests=references.Projections.Where((_,ordinal)=>!custom&&snapshot.ValuationAt<references.Fixings[ordinal].FixingAt)
            .Select(projection=>projection.Market).Distinct().ToImmutableArray();
        var market=data.Model.Build(snapshot,requests,ct);
        for(var ordinal=0;ordinal<references.Projections.Length;ordinal++)
        {
            ct.ThrowIfCancellationRequested();
            var projection=references.Projections[ordinal];var fixing=references.Fixings[ordinal];
            var quote=data.QuoteRows(fixing.Quote.Code).Single();
            _=IndexFixingPricing.Resolve(market,new(fixing.Index,quote,projection.Market,projection.Contract,null,fixing));
        }
    }

    public async Task<object> ObservationOptions(NpgsqlConnection connection, PreviewData data, InputFrame frame,
        SwapObservationOptionsRequest input, CancellationToken ct)
    {
        FloatingAveraging? selectedMode = null;
        if (input.ResetFrequency is not null && input.ResetFrequency is not ("monthly" or "daily"))
            throw Error("swap_averaging", "Select an admitted observation frequency.");
        if (input.AveragingMethod is not null)
        {
            if (input.ResetFrequency is null) throw Error("swap_averaging", "An averaging method requires its observation frequency.");
            selectedMode = SwapObservationCompatibility.Parse(input.ResetFrequency, input.AveragingMethod);
        }
        var from = input.DeliveryFrom is null ? (DateOnly?)null : Date(input.DeliveryFrom);
        var to = input.DeliveryTo is null ? (DateOnly?)null : Date(input.DeliveryTo);
        if (from is {} start && to is {} end && (start.Day != 1 || start.Year == 9999 || start.AddMonths(1) != end))
            throw Error("swap_projection", "This booking model selects one complete delivery month per instrument.");
        var markets = new List<(PreviewData Data, InputFrame Frame)> { (data, frame) };
        markets.AddRange(await database.ReadSwapMarkets(connection, frame, ct, data,
            input.DefinitionRevisionId is {} revision ? new HashSet<Guid> { revision } : null));
        if (input.DefinitionRevisionId is {} exact && markets.All(market => market.Data.DefinitionRevision.RevisionId != exact))
            throw Error("swap_definition", "The selected exact definition is unavailable in this context.");

        var choices = new List<SideObservationChoice>();
        var candidates = new List<(PreviewData Data, InputFrame Frame, JsonElement Curve, string Index, QuoteSpecification Quote)>();
        foreach (var market in markets)
        {
            ct.ThrowIfCancellationRequested();
            var bundle = market.Data;
            if (input.DefinitionRevisionId is {} selected && bundle.DefinitionRevision.RevisionId != selected) continue;
            foreach (var curve in bundle.Definitions["curves"])
            {
                if (input.Commodity is not null && curve.Text("commodity_code") != input.Commodity) continue;
                var targets = bundle.References["settlement_targets"].Where(row => row.Text("code") == curve.Text("settlement_target_code")).ToArray();
                var quotes = bundle.Quotes.Where(quote => quote.Key.Code == curve.Text("quote_code")).ToArray();
                if (targets.Length != 1 || targets[0].Text("kind") != "index" || quotes.Length != 1 || quotes[0].Kind != QuoteKind.Outright) continue;
                var index = targets[0].Text("index_a_code");
                candidates.Add((bundle, market.Frame, curve, index, quotes[0]));
                var supported = SwapObservationCompatibility.SupportedSamplings(bundle, curve);
                foreach (var mode in SwapObservationCompatibility.Modes)
                {
                    var reasons = ImmutableArray<SideObservationUnavailable>.Empty;
                    try
                    {
                        if (!supported.Any(sampling => sampling.Averaging == mode.Averaging))
                            throw Error("index_fixing_cadence", "The selected index has no qualified published observations at the required period or cadence.");
                        if (from is {} f && to is {} t && (!mode.RequiresObservationCalendar || input.ObservationCalendar is not null))
                        {
                            var calendar = mode.RequiresObservationCalendar ? ObservationCalendar(bundle, input.ObservationCalendar!.Value) : null;
                            _ = SwapObservationCompatibility.PreviewReferences(bundle, curve, new(bundle.ReferenceRevision, index), quotes[0], f, t, mode.Averaging, calendar);
                        }
                    }
                    catch (MarketDataException error) { reasons = [new(error.Code, error.Message)]; }
                    choices.Add(new(bundle.DefinitionRevision.RevisionId, curve.Text("code"), index,
                        mode.ResetFrequency, mode.AveragingMethod, reasons.IsEmpty, reasons));
                }
            }
        }
        var indices = choices.Where(choice => choice.Available &&
            (selectedMode is null ? input.ResetFrequency is null || choice.ResetFrequency == input.ResetFrequency :
                choice.ResetFrequency == SwapObservationCompatibility.Mode(selectedMode.Value).ResetFrequency && choice.AveragingMethod == SwapObservationCompatibility.Mode(selectedMode.Value).AveragingMethod))
            .Select(choice => new { choice.DefinitionRevisionId, choice.Curve, choice.Index }).Distinct().ToArray();
        var samplings = SwapObservationCompatibility.Modes.Where(mode => choices.Any(choice => choice.Available &&
            choice.ResetFrequency == mode.ResetFrequency && choice.AveragingMethod == mode.AveragingMethod &&
            (input.Curve is null || choice.Curve == input.Curve) && (input.Index is null || choice.Index == input.Index))).ToArray();
        var calendars = markets.SelectMany(market => market.Data.References["calendars"].Select(row => new
        {
            reference = new ReferenceKey(market.Data.ReferenceRevision, row.Text("code")), name = row.Text("code"),
            from = row.Text("coverage_from"), to = row.Text("coverage_to")
        })).Distinct().ToArray();

        object? observations = null;
        SideObservationUnavailable? unavailable = null;
        string? customUnavailable = null;
        if (selectedMode is not null && selectedMode is not (FloatingAveraging.MonthlyIndexPrice or FloatingAveraging.CalendarDayArithmetic))
            customUnavailable = "This observation convention has no qualified custom-curve projection mapping.";
        var selectedCandidates = candidates.Where(candidate => (input.Curve is null || candidate.Curve.Text("code") == input.Curve) &&
            (input.Index is null || candidate.Index == input.Index)).ToArray();
        var deliveryPeriods = new List<SideDeliveryPeriod>();
        // Derive calendar choices independently of the dates currently entered.
        // The same observation planner qualifies calendar guards and exact fixings;
        // a calendar-based mode has no selectable dates before its calendar is chosen.
        if (input.Curve is not null && selectedCandidates.Length == 1 && selectedMode is {} periodMode &&
            (!SwapObservationCompatibility.Mode(periodMode).RequiresObservationCalendar || input.ObservationCalendar is not null))
        {
            var candidate = selectedCandidates[0];
            var members = candidate.Data.Definitions["curve_members"].Where(row => row.Text("curve_code") == input.Curve)
                .Select(row => row.Id("contract_id")).ToHashSet();
            var months = candidate.Data.Contracts.Where(contract => members.Contains(contract.Key.Id) &&
                    contract.Kind == ContractKind.Forward && contract.DeliveryFrom.Year < 9999)
                .Select(contract => new DateOnly(contract.DeliveryFrom.Year,contract.DeliveryFrom.Month,1)).Distinct().Order();
            var published = input.CustomProjection is null ? ImmutableArray<CustomCurves.PublishedProjectionChoice>.Empty :
                await CustomCurves.ProjectionChoices(connection,database,candidate.Data,candidate.Frame,ct);
            foreach (var month in months)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var calendar = input.ObservationCalendar is {} key ? ObservationCalendar(candidate.Data,key) : null;
                    var custom = input.CustomProjection is {} c ? new CustomCurveProjectionBinding(c.CatalogId,c.CurveId,c.Version,c.OutputNode,c.Position,c.Selection,c.Implementation) : null;
                    var references = SwapObservationCompatibility.PreviewReferences(candidate.Data,candidate.Curve,
                        new(candidate.Data.ReferenceRevision,candidate.Index),candidate.Quote,month,month.AddMonths(1),periodMode,calendar,custom);
                    if (input.CustomProjection is {} projection && !published.Any(choice => choice.Reference == projection &&
                        choice.Unit == PriceUnit.FromQuote(candidate.Quote) && (projection.Selection == CustomCurveProjectionBinding.SupportedSelection ||
                        references.Observations.All(observation => choice.Deliveries.Contains(new(observation.From,observation.To)))))) continue;
                    RequireObservationInputs(candidate.Data,candidate.Frame,references,custom is not null,ct);
                    var mode = SwapObservationCompatibility.Mode(periodMode);
                    deliveryPeriods.Add(new(mode.ResetFrequency,mode.AveragingMethod,Date(month),Date(month.AddMonths(1))));
                }
                catch (MarketDataException error) when (error.Code is "swap_averaging" or "swap_projection_coverage" or
                    "index_fixing_convention_missing" or "index_fixing_definition" or "index_fixing_cadence" or
                    "swap_observation_calendar" or "swap_custom_projection_averaging" or "custom_projection" or
                    "missing_observation" or "index_fixing_missing" or "source_expired") { }
            }
        }
        if (input.Curve is not null && selectedCandidates.Length == 0)
            unavailable = new("swap_definition", "The selected projection is unavailable in this exact reference context.");
        if (input.Curve is not null && selectedCandidates.Length == 1 && selectedMode is {} averaging && from is {} deliveryFrom && to is {} deliveryTo)
        {
            var candidate = selectedCandidates[0];
            try
            {
                var calendar = input.ObservationCalendar is {} key ? ObservationCalendar(candidate.Data, key) : null;
                CustomCurveProjectionBinding? custom = input.CustomProjection is {} c ? new(c.CatalogId, c.CurveId, c.Version, c.OutputNode, c.Position, c.Selection, c.Implementation) : null;
                var references = SwapObservationCompatibility.PreviewReferences(candidate.Data, candidate.Curve,
                    new(candidate.Data.ReferenceRevision, candidate.Index), candidate.Quote, deliveryFrom, deliveryTo, averaging, calendar, custom);
                if (input.CustomProjection is {} selectedProjection)
                {
                    var published = await CustomCurves.ProjectionChoices(connection, database, candidate.Data, candidate.Frame, ct);
                    if (!published.Any(choice => choice.Reference == selectedProjection && choice.Unit == PriceUnit.FromQuote(candidate.Quote) &&
                        (selectedProjection.Selection == CustomCurveProjectionBinding.SupportedSelection ||
                         references.Observations.All(observation => choice.Deliveries.Contains(new(observation.From, observation.To))))))
                        throw Error("custom_projection", "The exact published projection is unavailable or does not cover this observation plan.");
                }
                observations = references.Observations.Select((observation, ordinal) => new
                {
                    from = Date(observation.From), to = Date(observation.To), quantitySpecification = candidate.Quote.Quantity,
                    contract = references.Projections[ordinal].Contract.Key, fixing = references.Fixings[ordinal]
                }).ToArray();
            }
            catch (MarketDataException error) { unavailable = new(error.Code, error.Message); }
        }
        return new { indices, samplings, deliveryPeriods, observationCalendars = calendars, observations, availability = unavailable,
            customProjectionUnavailableReason = customUnavailable, choices };
    }
}
