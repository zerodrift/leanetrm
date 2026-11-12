using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Publications;

namespace OpenEtrm.Api;

internal sealed partial class PricePublicationStore
{
    // Inspect a proposed match against saved metadata. This neither executes a
    // placeholder methodology nor saves a source binding or qualifies finality.
    internal async Task<object> InspectPower(NpgsqlConnection connection, PinnedContext pin,
        PowerPublicationPreviewRequest request, CancellationToken ct) =>
        (await ReadPowerCapture(connection, pin, request, ct)).Receipt;

    async Task<(PricePublicationIndex Index, PowerHistory.SourceMetadata Metadata,
        PreviewData Data, PowerInterval[] Window, PowerHistory.History History, object Receipt)> ReadPowerCapture(
        NpgsqlConnection connection, PinnedContext pin, PowerPublicationPreviewRequest request, CancellationToken ct, bool calendarDays = false)
    {
        ScheduleReads.RequireCurrent(pin);
        if (request.Publication.CatalogId != database.Manifest.CatalogId || request.Publication.Id == Guid.Empty ||
            request.Publication.Version < 1 || request.SourceId == Guid.Empty || string.IsNullOrWhiteSpace(request.IndexCode) ||
            request.Market is not ("DA" or "RT"))
            throw InvalidPowerSource();
        var publication = (await Read(connection, ct)).SingleOrDefault(p => p.Key == request.Publication)
            ?? throw new MarketDataException("publication_missing", "Select an exact saved publication version visible in this context.");
        var data = await Data(connection, ct, calendarDays, publication.References);
        _ = Validate(publication, data);
        if (publication.Implementation is not ("power_placeholder_v1" or HourlyPowerPublication.Implementation)) throw InvalidPowerSource();
        var index = publication.Indices.SingleOrDefault(i => i.Key.Code == request.IndexCode)
            ?? throw InvalidPowerSource();
        var metadata = await PowerHistory.Source(connection, request.SourceId, ct);
        ValidatePowerSource(publication, index, metadata, data, request.Market);

        var clock = PowerClock.FromRule(metadata.Clock, TimeZoneDatabase.Installed());
        var window = PowerImportProtocol.Window(request.FromDate, request.ThroughDate, clock, metadata.Minutes);
        var history = await PowerHistory.ReadPrices(connection, request.SourceId, window[0].Start, window[^1].End, request.KnownAt, ct);
        // Reuse the same start/end and completeness validation as delivery-price
        // history. Missing rows remain missing; nothing is filled or averaged.
        var chart = PowerHistory.Chart(window, history.Rows);
        var missing = chart.Count(point => point.Value is null);
        var receipt = new
        {
            publication = publication.Key, index = index.Key, source = metadata.Display, request.Market,
            request.FromDate, request.ThroughDate, history.KnownAt, contextId = history.ContextId,
            publicationStatus = "collected", contractuallyFinal = false, bindingSaved = false,
            expectedIntervals = window.Length, missingIntervals = missing, complete = missing == 0,
            rows = history.Rows.Select(row => new
            {
                row.Start, row.End, row.Price, row.KnownAt, row.BatchId, sourceId = request.SourceId, revision = row.Revision
            }).ToArray()
        };
        return (index, metadata, data, window, history, receipt);
    }

    internal static void ValidatePowerSource(PricePublicationDefinition publication, PricePublicationIndex index,
        PowerHistory.SourceMetadata metadata, PreviewData data, string market)
    {
        var source = metadata.Display;
        var revision = publication.References;
        var unit = new PriceUnit(new(revision, source.Text("currency_code")),
            new(revision, source.Text("quantity_code")), ExactDecimal.Parse("1"));
        if (source.Id("catalog_id") != publication.Key.CatalogId ||
            source.Id("reference_catalog_id") != revision.CatalogId || source.Id("reference_revision_id") != revision.RevisionId ||
            index.Location != new ReferenceKey(revision, source.Text("location_code")) ||
            publication.Clock != new ReferenceKey(revision, source.Text("clock_code")) ||
            source.Text("market") != market || index.Unit != unit || metadata.Minutes != 60 ||
            source.Text("provider") != "PJM")
            throw InvalidPowerSource();
        var quantity = PpaQuantitiesStore.Quantity(data, source.Text("quantity_code"));
        if (quantity.Unit.Code != "megawatt_hour" || quantity.Dimension.Code != "energy" || quantity.Commodity != index.Commodity)
            throw InvalidPowerSource();
        PowerImportProtocol.ValidateFeed(source.Text("provider"), source.Text("dataset"), market, metadata.Minutes);
    }

    static MarketDataException InvalidPowerSource() => new("publication_power_source",
        "Select an exact saved power publication index and a matching PJM hourly source, market, location, clock and price unit.");
}
