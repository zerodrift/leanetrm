using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Market selection is API orchestration. Reference identities and pricing stay
// in the domain. New choices use published roles; held terms use their exact native source.
internal sealed class PhysicalMarketSelection
{
    internal static PhysicalMarketSelection Gas { get; } = new(
        "gas",
        """
        SELECT json_build_object('definition_revision_id',i.definition_revision_id)::text
        FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id)
        WHERE(r.catalog_id,r.trade_id)=($1,$2) ORDER BY r.revision DESC LIMIT 1
        """);

    internal static PhysicalMarketSelection Crude { get; } = new(
        "crude",
        """
        SELECT json_build_object('definition_revision_id',i.definition_revision_id)::text
        FROM trading.crude_trade_revisions r JOIN trading.crude_instruments i USING(catalog_id,instrument_id)
        WHERE(r.catalog_id,r.trade_id)=($1,$2) ORDER BY r.revision DESC LIMIT 1
        """);

    internal static PhysicalMarketSelection Lng { get; } = new(
        "lng",
        """
        SELECT json_build_object('definition_revision_id',i.definition_revision_id)::text
        FROM trading.lng_trade_revisions r JOIN trading.lng_instruments i USING(catalog_id,instrument_id)
        WHERE(r.catalog_id,r.trade_id)=($1,$2) ORDER BY r.revision DESC LIMIT 1
        """);

    private readonly string product;
    private readonly string tradeRevisionQuery;
    private PhysicalMarketSelection(string product,string tradeRevisionQuery)
        =>(this.product,this.tradeRevisionQuery)=(product,tradeRevisionQuery);
    private MarketDataException Invalid(string message)=>new($"{product}_market_context",message);

    internal async Task<(PreviewData Data, InputFrame Frame)> PortfolioSource(PreviewDatabase database,
        NpgsqlConnection connection, InputFrame frame, Guid definition, ImmutableArray<ReferenceRevision> references,
        ImmutableArray<(PreviewData Data, InputFrame Frame)> supplied, bool requirePinnedSources, CancellationToken ct)
    {
        if(references.IsDefaultOrEmpty||references.Distinct().Count()!=1)
            throw Invalid($"Held {product} terms require one exact reference revision.");
        var exact=new CatalogueSource(definition,references[0].CatalogId,references[0].RevisionId);
        var source=await MarketInputSelection.Resolve(database,connection,frame,exact,supplied,requirePinnedSources,ct);
        RequirePortfolioSource(source,frame,definition,references);
        return source;
    }

    internal void RequirePortfolioSource((PreviewData Data, InputFrame Frame) source, InputFrame frame,
        Guid definition, ImmutableArray<ReferenceRevision> references)
    {
        if(references.IsDefaultOrEmpty||references.Any(r=>r!=source.Data.ReferenceRevision))
            throw Invalid($"{product} holdings require their exact reference revisions.");
        MarketInputSelection.Require(source,frame,new(definition,source.Data.ReferenceRevision.CatalogId,source.Data.ReferenceRevision.RevisionId));
    }

    internal async Task<(PreviewData Data, InputFrame Frame)> Read(PreviewDatabase database,
        NpgsqlConnection connection, InputFrame frame, CancellationToken ct, Guid? trade = null, Guid? setup = null)
    {
        if(setup is not null && product != "crude") throw Invalid("A crude setup cannot select another physical product's market.");
        Guid? revision = null;
        if (trade is {} id)
        {
            var rows = await PreviewDatabase.Rows(connection, tradeRevisionQuery, [database.Manifest.CatalogId, id], ct);
            if (rows.Length != 1) throw Invalid($"The selected trade has no visible retained {product} market.");
            revision = rows[0].Id("definition_revision_id");
        }
        if (product == "crude" && revision is null)
        {
            var heads=setup is {} selectedSetup?new[]{selectedSetup}:await CrudeSetupEditor.CurrentSetups(connection,database.Manifest.CatalogId,ct);
            var rows = await PreviewDatabase.Rows(connection, """
                SELECT DISTINCT jsonb_build_object('definition_revision_id',v.definition_revision_id)::text
                FROM market_data.crude_setup_versions v WHERE v.catalog_id=$1 AND v.state='published' AND v.id=ANY($2::uuid[])
                """, [database.Manifest.CatalogId,heads], ct);
            if (rows.Length > 1) throw Invalid("Choose one explicit crude pricing revision for the current delivery setups.");
            if (rows.Length == 1) revision = rows[0].Id("definition_revision_id");
            else if (setup is not null) throw Invalid("The requested published delivery setup is unavailable in this context.");
        }
        var exact=revision is {} held?await MarketInputSelection.ExactSource(database,connection,held,ct):
            (await new CatalogueStore(database.Manifest.CatalogId).ReadBound(connection,ct,frame.Time.Date)).RequireRole(product);
        var root=await MarketInputSelection.Root(database,connection,frame,ct);
        return await MarketInputSelection.Resolve(database,connection,frame,exact,[(root,frame)],false,ct);
    }

}
