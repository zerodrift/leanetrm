using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Publications;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed class PricePublicationStore(PreviewDatabase database)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new ExactDecimalJsonConverter() } };
    internal async Task<ImmutableArray<PricePublicationDefinition>> Read(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = ScheduleStore.Command(connection, "SELECT market_data.read_price_publications($1)::text", database.Manifest.CatalogId);
        return JsonSerializer.Deserialize<ImmutableArray<PricePublicationDefinition>>((string)(await command.ExecuteScalarAsync(ct))!, Json);
    }

    internal async Task<ImmutableArray<SavedPricePublicationOccurrence>> Occurrences(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = ScheduleStore.Command(connection, "SELECT market_data.read_price_publication_occurrences($1)::text", database.Manifest.CatalogId);
        return JsonSerializer.Deserialize<ImmutableArray<SavedPricePublicationOccurrence>>((string)(await command.ExecuteScalarAsync(ct))!, Json);
    }

    internal async Task<ImmutableArray<JsonElement>> Releases(NpgsqlConnection connection, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var command = ScheduleStore.Command(connection, "SELECT market_data.read_price_publication_releases($1,$2)::text", database.Manifest.CatalogId, cutoff);
        return JsonSerializer.Deserialize<ImmutableArray<JsonElement>>((string)(await command.ExecuteScalarAsync(ct))!, Json);
    }

    async Task<PreviewData> Data(NpgsqlConnection connection, CancellationToken ct) =>
        await database.Read(connection, ct, observationIds: [], referenceScope: ReferenceReadScope.AllVisible, includeCalendarDays: false);

    internal async Task<PricePublicationCatalog> Catalog(NpgsqlConnection connection, PinnedContext pin, CancellationToken ct)
    {
        var data = await Data(connection, ct);
        var publications = (await Read(connection, ct)).Where(p=>p.References==data.ReferenceRevision).ToImmutableArray();
        ImmutableArray<PricePublicationChoice> Choices(string table) => [..data.References[table].Select(row =>
            new PricePublicationChoice(new(data.ReferenceRevision, row.Text("code")),
                row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : row.Text("code")))];
        return new(database.Manifest.CatalogId, data.ReferenceRevision,
            pin.Context is null && await PreviewDatabase.HasPermission(connection, "WriteMarketDefinitions", ct),
            pin.Context is null && await PreviewDatabase.HasPermission(connection, "ImportObservations", ct),
            [..publications.Select(p => p.Publisher).DistinctBy(p => p.Key)], publications,
            Choices("commodities"), IndexChoices(data), Choices("calendars"), Choices("clock_rules"), Choices("sources"),
            [DatedGasPublication.DailyImplementation, DatedGasPublication.MonthlyImplementation, ..UnsupportedPricePublication.Implementations],
            [..(await Occurrences(connection, ct)).Where(o=>publications.Any(p=>p.Key==o.Occurrence.Publication))],
            await Releases(connection, pin.RequireFrame().At, ct));
    }

    static ImmutableArray<PricePublicationIndexChoice> IndexChoices(PreviewData data)
    {
        ReferenceKey Key(string code) => new(data.ReferenceRevision, code);
        var choices = ImmutableArray.CreateBuilder<PricePublicationIndexChoice>();
        foreach (var index in data.References["price_index_definitions"])
        foreach (var quote in data.Quotes.Where(q => q.Kind == QuoteKind.Outright && q.Currency.Code == index.Text("currency_code") &&
            q.Multiplier == ExactDecimal.Parse(index.Text("quote_multiplier"))))
        {
            var quantity = data.References["quantity_specifications"].Single(q => q.Text("code") == quote.Quantity.Code);
            if (quantity.Text("commodity_code") != index.Text("commodity_code") || quantity.Text("unit_code") != index.Text("quote_unit_code")) continue;
            choices.Add(new(Key(index.Text("code")), Key(index.Text("commodity_code")), Key(index.Text("location_code")), quote.Key,
                PriceUnit.FromQuote(quote), index.Text("code")));
        }
        return choices.ToImmutable();
    }

    static PricePublicationDefinition Normalize(PricePublicationDefinition definition) => definition with
    {
        Commodities = [..definition.Commodities.OrderBy(c => c.Code, StringComparer.Ordinal)],
        Indices = [..definition.Indices.OrderBy(i => i.Key.Code, StringComparer.Ordinal)]
    };

    static PricePublicationBehavior Validate(PricePublicationDefinition definition, PreviewData data)
    {
        var behavior = PricePublicationBehavior.Create(definition);
        if (definition.Key.CatalogId != data.DefinitionRevision.CatalogId || definition.References != data.ReferenceRevision)
            throw new MarketDataException("publication_definition", "The publication must pin the selected market's exact reference bundle.");
        bool Has(string table, ReferenceKey key) => key.Revision == data.ReferenceRevision && data.References[table].Any(r => r.Text("code") == key.Code);
        if (!Has("calendars", definition.Calendar) || !Has("clock_rules", definition.Clock) || definition.Commodities.Any(c => !Has("commodities", c)))
            throw new MarketDataException("publication_definition", "Choose exact visible commodity, calendar and clock references.");
        var candidates = IndexChoices(data);
        foreach (var index in definition.Indices)
            if (!candidates.Any(c => c.Key == index.Key && c.Commodity == index.Commodity && c.Location == index.Location && c.Quote == index.Quote && c.Unit == index.Unit))
                throw new MarketDataException("publication_index", "Each index must retain its referenced commodity, location and compatible quote unit.");
        return behavior;
    }

    internal async Task<PricePublicationDefinition> Save(NpgsqlConnection connection, PricePublicationSaveRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.ExpectedVersion < 0 || request.Definition is null ||
            request.Definition.Key.Version != request.ExpectedVersion + 1)
            throw new MarketDataException("publication_command", "Supply a stable request identity and the next version after the expected current version.");
        var data = await Data(connection, ct);
        _ = Validate(request.Definition, data);
        var definition = Normalize(request.Definition);
        await using var command = ScheduleStore.Command(connection, "SELECT market_data.save_price_publication($1,$2,$3,$4::jsonb)::text",
            database.Manifest.CatalogId, request.RequestId, request.ExpectedVersion, JsonSerializer.Serialize(definition, Json));
        return JsonSerializer.Deserialize<PricePublicationDefinition>((string)(await command.ExecuteScalarAsync(ct))!, Json)!;
    }

    internal async Task<object> Preview(NpgsqlConnection connection, PricePublicationPreviewRequest request, CancellationToken ct)
    {
        var data = await Data(connection, ct);
        var behavior = Validate(request.Definition, data);
        var periods = behavior.CoveredPeriods(request.Occurrence);
        var bindings = request.Definition.Indices.Select(index => new
        {
            index = index.Key,
            periods = periods.Select(period =>
            {
                var row = data.Definitions["index_fixing_observations"].SingleOrDefault(f => f.Text("index_code") == index.Key.Code &&
                    f.Text("quote_code") == index.Quote.Code &&
                    DateOnly.Parse(f.Text("observation_from"), System.Globalization.CultureInfo.InvariantCulture) == period.From &&
                    DateOnly.Parse(f.Text("observation_to"), System.Globalization.CultureInfo.InvariantCulture) == period.To);
                IndexFixing? fixing = row.ValueKind == JsonValueKind.Undefined ? null : new(index.Key, index.Quote, period.From, period.To,
                    DateTimeOffset.Parse(row.Text("fixing_at"), System.Globalization.CultureInfo.InvariantCulture),
                    new(data.DefinitionRevision, row.Id("series_id")), new(data.ReferenceRevision, row.Text("source_code")), row.Text("implementation"));
                return new { period, fixing, available = fixing is not null };
            }).ToArray()
        }).ToArray();
        return new
        {
            publication = request.Definition.Key, assessmentWindow = behavior.AssessmentWindow(request.Occurrence),
            request.Occurrence.AssessmentDate, coveredPeriods = periods, expectedReleaseAt = behavior.ExpectedRelease(request.Occurrence),
            bindings, note = "Expected release is operational metadata. Existing economic fixing boundaries remain explicit; no price is created by a schedule."
        };
    }

    static string Instant(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    internal async Task<SavedPricePublicationOccurrence> SaveOccurrence(NpgsqlConnection connection, PricePublicationOccurrenceSaveRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.Id == Guid.Empty || request.Occurrence is null)
            throw new MarketDataException("publication_occurrence", "Supply exact request, occurrence and publication identities.");
        var definition = (await Read(connection, ct)).SingleOrDefault(p => p.Key == request.Occurrence.Publication)
            ?? throw new MarketDataException("publication_missing", "Save and select an exact visible publication version before authoring occurrences.");
        var data=await Data(connection,ct);
        if(definition.References!=data.ReferenceRevision)throw new MarketDataException("publication_index","The occurrence must use its publication's exact admitted market.");
        var behavior = PricePublicationBehavior.Create(definition);
        _ = behavior.CoveredPeriods(request.Occurrence);
        var occurrence = request.Occurrence;
        var body = JsonSerializer.Serialize(new { occurrence.Publication, assessmentFrom = Instant(occurrence.AssessmentFrom),
            assessmentTo = Instant(occurrence.AssessmentTo), occurrence.AssessmentDate, occurrence.Periods,
            expectedReleaseAt = Instant(occurrence.ExpectedReleaseAt) }, Json);
        await using var command = ScheduleStore.Command(connection, "SELECT market_data.save_price_publication_occurrence($1,$2,$3,$4::jsonb)::text",
            database.Manifest.CatalogId, request.RequestId, request.Id, body);
        return JsonSerializer.Deserialize<SavedPricePublicationOccurrence>((string)(await command.ExecuteScalarAsync(ct))!, Json)!;
    }

    internal async Task<JsonElement> IngestFinal(NpgsqlConnection connection, PricePublicationFinalIngestRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.OccurrenceId == Guid.Empty || (request.PublishedAt.Offset != TimeSpan.Zero || request.PublishedAt.Ticks % 10 != 0) ||
            new[] { request.IndexCode, request.SourceCode, request.SourceEventId, request.SourceRevisionId }.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 200 || v != v.Trim()) ||
            request.Value is null)
            throw new MarketDataException("publication_release", "Supply a saved occurrence, exact index/source, provider event/revision, UTC issue instant and exact final value.");
        var occurrence = (await Occurrences(connection, ct)).SingleOrDefault(o => o.Id == request.OccurrenceId)
            ?? throw new MarketDataException("publication_missing", "Select a saved occurrence visible in the selected context.");
        var definition = (await Read(connection, ct)).Single(p => p.Key == occurrence.Occurrence.Publication);
        _ = PricePublicationBehavior.Create(definition).CoveredPeriods(occurrence.Occurrence);
        var data = await Data(connection, ct);
        if (!definition.Indices.Any(i => i.Key.Code == request.IndexCode) || definition.References != data.ReferenceRevision ||
            !data.References["sources"].Any(s => s.Text("code") == request.SourceCode))
            throw new MarketDataException("publication_index", "The selected publication index and source must belong to the exact visible reference bundle.");
        var value = ExactDecimal.Parse(request.Value);
        var body = JsonSerializer.Serialize(new { request.OccurrenceId, request.IndexCode, request.SourceCode, request.SourceEventId,
            request.SourceRevisionId, publishedAt = Instant(request.PublishedAt), value = value.ToString(), request.CorrectsReleaseId }, Json);
        await using var command = ScheduleStore.Command(connection, "SELECT market_data.ingest_price_publication_final($1,$2,$3,$4::jsonb)::text",
            database.Manifest.CatalogId, data.DefinitionRevision.RevisionId, request.RequestId, body);
        using var result = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        return result.RootElement.Clone();
    }
}
