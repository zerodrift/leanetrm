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
public sealed record SideObservationChoice(Guid DefinitionRevisionId, string Curve, string Index,
    string ResetFrequency, string AveragingMethod, bool Available, ImmutableArray<SideObservationUnavailable> Reasons);

public sealed partial class SwapStore
{
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
        return new { indices, samplings, observationCalendars = calendars, observations, availability = unavailable,
            customProjectionUnavailableReason = customUnavailable, choices };
    }
}
