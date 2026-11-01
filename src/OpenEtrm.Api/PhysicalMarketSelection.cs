using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Market selection is API orchestration. Reference identities and pricing stay
// in the domain; each product explicitly declares whether primary-market use is admitted.
internal sealed class PhysicalMarketSelection
{
    private enum PrimaryMarketUse { Allowed, RequireIndependent }

    internal static PhysicalMarketSelection Gas { get; } = new(
        "gas", PrimaryMarketUse.Allowed, m => m.GasMarket, m => m.RetainedGasMarkets ?? [],
        """
        SELECT json_build_object('definition_revision_id',i.definition_revision_id)::text
        FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id)
        WHERE(r.catalog_id,r.trade_id)=($1,$2) ORDER BY r.revision DESC LIMIT 1
        """);

    internal static PhysicalMarketSelection Crude { get; } = new(
        "crude", PrimaryMarketUse.RequireIndependent, m => m.CrudeMarket, m => m.RetainedCrudeMarkets ?? [],
        """
        SELECT json_build_object('definition_revision_id',i.definition_revision_id)::text
        FROM trading.crude_trade_revisions r JOIN trading.crude_instruments i USING(catalog_id,instrument_id)
        WHERE(r.catalog_id,r.trade_id)=($1,$2) ORDER BY r.revision DESC LIMIT 1
        """);

    private readonly string product;
    private readonly PrimaryMarketUse primaryMarketUse;
    private readonly Func<PreviewManifest, PreviewPhysicalMarket?> current;
    private readonly Func<PreviewManifest, PreviewPhysicalMarket[]> retained;
    private readonly string tradeRevisionQuery;

    private PhysicalMarketSelection(string product, PrimaryMarketUse primaryMarketUse,
        Func<PreviewManifest, PreviewPhysicalMarket?> current,
        Func<PreviewManifest, PreviewPhysicalMarket[]> retained, string tradeRevisionQuery)
    {
        this.product = product;
        this.primaryMarketUse = primaryMarketUse;
        this.current = current;
        this.retained = retained;
        this.tradeRevisionQuery = tradeRevisionQuery;
    }

    private MarketDataException Invalid(string message) => new($"{product}_market_context", message);

    internal bool Configured(PreviewManifest manifest) => current(manifest) is not null || retained(manifest).Length > 0;

    internal async Task<(PreviewData Data, InputFrame Frame)> Read(PreviewDatabase database,
        NpgsqlConnection connection, InputFrame frame, CancellationToken ct, Guid? trade = null)
    {
        Guid? revision = null;
        if (trade is {} id)
        {
            var rows = await PreviewDatabase.Rows(connection, tradeRevisionQuery, [database.Manifest.CatalogId, id], ct);
            if (rows.Length != 1) throw Invalid($"The selected trade has no visible retained {product} market.");
            revision = rows[0].Id("definition_revision_id");
        }
        var selected = Select(database.Manifest, frame, revision);
        var corrections = selected.Revision != database.Manifest.RevisionId && frame.ResolvesLatestCorrection;
        var data = await database.Read(connection, ct, selectedRevision: selected.Revision,
            observationIds: selected.Frame.ObservationIds,
            observationScope: corrections ? ObservationReadScope.FactHistory : ObservationReadScope.ExactPins);
        return (data, corrections ? data.SelectPinned(selected.Frame) : selected.Frame);
    }

    internal (Guid Revision, InputFrame Frame) Select(PreviewManifest manifest, InputFrame frame, Guid? requiredRevision = null)
    {
        if (primaryMarketUse == PrimaryMarketUse.Allowed && requiredRevision == manifest.RevisionId)
            return (manifest.RevisionId, frame);

        var market = current(manifest);
        var declared = (market is null ? Enumerable.Empty<PreviewPhysicalMarket>() : [market])
            .Concat(retained(manifest)).ToArray();
        if (declared.Any(m => m.RevisionId == Guid.Empty || m.RevisionId == manifest.RevisionId) ||
            declared.Select(m => m.RevisionId).Distinct().Count() != declared.Length)
            throw Invalid($"Declare each independent {product} revision once, separately from the primary market.");

        if (requiredRevision is {} required)
        {
            var matches = declared.Where(m => m.RevisionId == required).ToArray();
            if (matches.Length != 1)
                throw Invalid($"The retained trade requires one explicitly configured {product} market selection.");
            market = matches[0];
        }
        if (market is null)
        {
            if (primaryMarketUse == PrimaryMarketUse.Allowed) return (manifest.RevisionId, frame);
            throw Invalid($"Select an explicit {product} market with exact dated price inputs.");
        }

        var frames = (market.Frames ?? []).Where(f => f.At == frame.At).ToArray();
        if (frames.Length != 1)
            throw Invalid($"The selected date requires one explicitly configured {product} market selection.");
        var source = frames[0];
        if (source.ObservationIds is null || source.CurveContracts is null ||
            source.ObservationIds.Distinct().Count() != source.ObservationIds.Length)
            throw Invalid($"The {product} market selection contains missing or duplicate price inputs.");

        return (market.RevisionId, frame with
        {
            Name = $"Selected {product} market",
            ObservationIds = [..source.ObservationIds],
            CurveContracts = source.CurveContracts.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableArray()),
            FrozenObservationIds = source.FrozenObservationIds is {} frozen ? [..frozen] : default,
            OriginCloseId = null,
            SwapMarketSelections = null,
            PpaBundle = null
        });
    }
}
