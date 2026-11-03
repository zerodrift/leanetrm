using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Lng;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LngPhysicalSelection(Guid ReferenceCatalogId, Guid ReferenceRevisionId, string Commodity, string Product,
    string LoadFacility, string LoadCustody, string DischargeFacility, string DischargeCustody, string EnergyQuantityCode, string Clock);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LngIncotermsSelection(string Edition, string Rule, string NamedPlace);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LngResolveRequest(Guid SnapshotId, string? Convention, string? KnowledgeMode,
    LngPhysicalSelection Physical, string DischargeFrom, string DischargeTo);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PhysicalLngWriteRequest(Guid SnapshotId, string? Convention, string? KnowledgeMode, string? Profile,
    Guid OperationId, Guid BookId, LngPhysicalSelection Physical, string? LoadFrom, string? LoadTo,
    string? DischargeFrom, string? DischargeTo, string? Quantity, string? QuantityCode, string? QuantityUnit,
    [property:JsonRequired] string? ConversionCode, string? QuantityBasis, string? Tolerance,
    LngIncotermsSelection Incoterms, string? Direction, string? ConsiderationDirection, string? FixedPrice, string? PaymentAt,
    bool? Discounting, Guid? LegalEntityId, int? LegalEntityRevision, Guid? CounterpartyId, int? CounterpartyRevision);
public sealed record PhysicalLngEditorRequest(FuturesReadRequest Context, Guid TradeId);

public sealed class PhysicalLngStore(PreviewDatabase database)
{
    public const string Convention = "physical_lng_cargo_v1";
    public const string Profile = "fixed_gross_discharge_energy_v1";
    private Guid Catalog => database.Manifest.CatalogId;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static MarketDataException Invalid(string message) => new("lng_input", message);
    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value) ? value : throw Invalid("Choose every required LNG term explicitly.");
    private static T One<T>(IEnumerable<T> values)
    { var rows = values.Take(2).ToArray(); return rows.Length == 1 ? rows[0] : throw Invalid("An exact LNG reference or definition is missing or ambiguous."); }
    private static DateTimeOffset Instant(string? value) => TradingInstant.Parse(Required(value), "lng_input");
    private static ExactDecimal Sign(string? value, bool physical = false) => value switch
    {
        "receive" => ExactDecimal.Parse("1"), "deliver" when physical => ExactDecimal.Parse("-1"),
        "pay" when !physical => ExactDecimal.Parse("-1"), _ => throw Invalid("Choose each physical and consideration direction explicitly.")
    };
    internal async Task<ReferenceRevision> DeliveryRevision(NpgsqlConnection connection, CancellationToken ct)
    {
        var selected = database.Manifest.LngReferenceCatalog ?? throw new MarketDataException("lng_reference_context", "Configure an explicit LNG delivery reference revision.");
        var row = One(await PreviewDatabase.Rows(connection, """
            SELECT row_to_json(r)::text FROM market_data.revisions r
            WHERE(catalog_id,id,reference_revision_id,state)=($1,$2,$3,'sealed')
            """, [Catalog, selected.DefinitionRevisionId, selected.ReferenceRevisionId], ct));
        var revision = new ReferenceRevision(row.Id("reference_catalog_id"), row.Id("reference_revision_id"));
        await using var authority = ScheduleStore.Command(connection, """
            SELECT security.market_access($1,'ReadMarketData') AND security.market_fact_visible($1,$2,NULL)
            AND security.reference_access($3,'ReadMarketData') AND security.reference_version_visible($3,$4)
            """, Catalog, selected.DefinitionRevisionId, revision.CatalogId, revision.RevisionId);
        if (!Equals(await authority.ExecuteScalarAsync(ct), true)) throw new PreviewDeniedException();
        return revision;
    }
    internal async Task RecheckReferences(NpgsqlConnection connection, CancellationToken ct) => _ = await DeliveryRevision(connection, ct);
    internal async Task RecheckScope(NpgsqlConnection connection, Guid? trade, CancellationToken ct)
    {
        await using var query = ScheduleStore.Command(connection, """
            WITH selected AS(SELECT i.definition_revision_id,i.reference_catalog_id,i.reference_revision_id
             FROM trading.lng_trade_revisions r JOIN trading.lng_instruments i USING(catalog_id,instrument_id)
             WHERE r.catalog_id=$1 AND($2::uuid IS NULL OR r.trade_id=$2) ORDER BY r.publication_order DESC LIMIT 256)
            SELECT (CASE WHEN $2::uuid IS NULL THEN true ELSE count(*)=1 END) AND
             coalesce(bool_and(security.market_access($1,'ReadMarketData') AND security.reference_access(reference_catalog_id,'ReadMarketData')
              AND security.reference_version_visible(reference_catalog_id,reference_revision_id)
              AND security.market_fact_visible($1,definition_revision_id,NULL)),true) FROM selected
            """, Catalog, (object?)trade ?? DBNull.Value);
        if (!Equals(await query.ExecuteScalarAsync(ct), true)) throw new PreviewDeniedException();
    }
    public async Task<object> References(NpgsqlConnection connection, CancellationToken ct)
    {
        var revision = await DeliveryRevision(connection, ct);
        async Task<ImmutableArray<JsonElement>> Rows(string table) => await PreviewDatabase.Rows(connection,
            $"SELECT row_to_json(r)::text FROM ref_data.{table} r WHERE(catalog_id,revision_id)=($1,$2) ORDER BY to_jsonb(r)::text",
            [revision.CatalogId, revision.RevisionId], ct);
        return new
        {
            referenceCatalogId = revision.CatalogId, referenceRevisionId = revision.RevisionId,
            commodities = await Rows("commodities"), products = await Rows("lng_products"), custodyPoints = await Rows("lng_custody_points"),
            locations = await Rows("locations"), quantities = await Rows("quantity_specifications"), units = await Rows("units"),
            clocks = await Rows("clock_rules"), conversions = await Rows("lng_mass_energy_conversions"),
            incotermsEditions = await Rows("incoterms_editions"), incotermsRules = await Rows("incoterms_rules"),
            books = await PreviewDatabase.Rows(connection, "SELECT row_to_json(b)::text FROM trading.books b WHERE catalog_id=$1 ORDER BY name", [Catalog], ct),
            legalEntities = await TradeParties.Catalog(connection, Catalog, true, ct), counterparties = await TradeParties.Catalog(connection, Catalog, false, ct),
            canBook = await PreviewDatabase.HasPermission(connection, "BookTrade", ct),
            profiles = new[] { new { code = Profile, quantityBasis = "gross_discharge_energy_total", tolerance = "0", discounting = false,
                label = "Fixed gross discharge energy · zero tolerance · undiscounted" } }
        };
    }
    private async Task<JsonElement> ResolveMapping(NpgsqlConnection connection, LngPhysicalSelection physical,
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset valuation, CancellationToken ct)
    {
        await using var query = ScheduleStore.Command(connection, "SELECT security.resolve_lng_delivery($1,$2::jsonb,$3,$4,$5)::text",
            Catalog, JsonSerializer.Serialize(physical, Json), from, to, valuation);
        return JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);
    }
    public async Task<object> ResolveReference(NpgsqlConnection connection, InputFrame frame, LngResolveRequest body, CancellationToken ct)
    {
        if (body.Convention != Convention || body.KnowledgeMode != "current") throw Invalid("Resolve a new LNG cargo under its explicit current contract.");
        var revision = await DeliveryRevision(connection, ct);
        if (body.Physical is null || body.Physical.ReferenceCatalogId != revision.CatalogId || body.Physical.ReferenceRevisionId != revision.RevisionId)
            throw Invalid("Select the exact configured LNG reference revision.");
        var selected = await ResolveMapping(connection, body.Physical, Instant(body.DischargeFrom), Instant(body.DischargeTo), frame.At, ct);
        var data = await database.Read(connection, ct, selectedRevision: selected.Id("definitionRevisionId"),
            observationIds: [], observationScope: ObservationReadScope.ExactPins);
        var assignment = await Metadata(connection, data, body.Physical, selected.Text("assignmentCode"), Instant(body.DischargeFrom), Instant(body.DischargeTo), ct);
        var curve = One(data.Definitions["curves"].Where(c => c.Text("code") == selected.Text("curve")));
        var currency = One(data.References["currencies"].Where(c => c.Text("code") == assignment.Quote.Currency.Code)).Text("display_code");
        return new { definitionRevisionId = data.DefinitionRevision.RevisionId, assignmentCode = selected.Text("assignmentCode"),
            curve = curve.Text("code"), curveName = curve.Text("name"), energyQuantityCode = assignment.Energy.Key.Code,
            energyUnit = Unit(data, assignment.Energy.Key), currency, quoteMultiplier = assignment.Quote.Multiplier.ToString() };
    }
    private static QuantitySpecification Quantity(PreviewData data, string code)
    {
        var q = One(data.References["quantity_specifications"].Where(q => q.Text("code") == code));
        ReferenceKey R(string value) => new(data.ReferenceRevision, value);
        return new(R(code), R(q.Text("commodity_code")), R(q.Text("basis_code")), R(q.Text("unit_code")), R(q.Text("dimension_code")));
    }
    private static string Unit(PreviewData data, ReferenceKey specification)
    {
        var quantity = Quantity(data, specification.Code);
        return One(data.References["units"].Where(u => u.Text("code") == quantity.Unit.Code)).Text("symbol");
    }
    private static async Task RequireLocations(NpgsqlConnection connection, ReferenceRevision revision, string[] codes, CancellationToken ct)
    {
        var selected = codes.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await PreviewDatabase.Rows(connection, """
            SELECT row_to_json(l)::text FROM ref_data.locations l
            WHERE(catalog_id,revision_id)=($1,$2) AND code=ANY($3::text[]) ORDER BY code
            """, [revision.CatalogId, revision.RevisionId, selected], ct);
        if (rows.Length != selected.Length) throw Invalid("Every exact LNG terminal, custody point and named place must be visible in the selected reference revision.");
    }
    internal async Task<LngValuationAssignment> Metadata(NpgsqlConnection connection, PreviewData data,
        LngPhysicalSelection physical, string assignmentCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (physical is null || physical.ReferenceCatalogId != data.ReferenceRevision.CatalogId || physical.ReferenceRevisionId != data.ReferenceRevision.RevisionId)
            throw Invalid("Use the source's exact LNG reference namespace.");
        await RequireLocations(connection, data.ReferenceRevision,
            [physical.LoadFacility, physical.LoadCustody, physical.DischargeFacility, physical.DischargeCustody], ct);
        var row = One(await PreviewDatabase.Rows(connection, """
            SELECT row_to_json(a)::text FROM market_data.lng_valuation_assignments a
            WHERE(catalog_id,revision_id,code)=($1,$2,$3)
            """, [Catalog, data.DefinitionRevision.RevisionId, assignmentCode], ct));
        var product = One(data.References["lng_products"].Where(p => p.Text("code") == physical.Product));
        if (product.Text("commodity_code") != physical.Commodity || product.Text("energy_quantity_code") != physical.EnergyQuantityCode ||
            row.Text("product_code") != physical.Product || row.Text("discharge_facility_code") != physical.DischargeFacility ||
            row.Text("discharge_custody_code") != physical.DischargeCustody || row.Text("clock_code") != physical.Clock ||
            row.Text("energy_quantity_code") != physical.EnergyQuantityCode || row.Id("reference_catalog_id") != physical.ReferenceCatalogId ||
            row.Id("reference_revision_id") != physical.ReferenceRevisionId)
            throw Invalid("The exact discharge physical parameters do not match their retained valuation assignment.");
        foreach (var point in new[] { ("load", physical.LoadFacility, physical.LoadCustody), ("discharge", physical.DischargeFacility, physical.DischargeCustody) })
            _ = One(data.References["lng_custody_points"].Where(p => p.Text("product_code") == physical.Product && p.Text("stage") == point.Item1 &&
                p.Text("facility_code") == point.Item2 && p.Text("custody_point_code") == point.Item3));
        var clock = One(data.References["clock_rules"].Where(c => c.Text("code") == physical.Clock));
        if (clock.Text("kind") != "fixed_offset" || clock.Text("fixed_offset_seconds") != "0") throw Invalid("This LNG profile requires an explicitly selected UTC clock.");
        var curve = One(data.Definitions["curves"].Where(c => c.Text("code") == row.Text("curve_code")));
        var quote = One(data.Quotes.Where(q => q.Key.Code == curve.Text("quote_code")));
        var target = One(data.References["settlement_targets"].Where(t => t.Text("code") == curve.Text("settlement_target_code")));
        if (curve.Text("commodity_code") != physical.Commodity || quote.Quantity.Code != physical.EnergyQuantityCode ||
            quote.Kind != QuoteKind.Outright || target.Text("kind") != "index" || target.OptionalText("index_b_code") is not null)
            throw Invalid("The LNG mapping requires one exact outright gross-energy index source.");
        var ids = data.Definitions["curve_members"].Where(m => m.Text("curve_code") == row.Text("curve_code")).Select(m => m.Id("contract_id")).ToHashSet();
        var contract = One(data.Contracts.Where(c => ids.Contains(c.Key.Id) && c.Kind == ContractKind.Forward && c.Interval is null &&
            c.DeliveryFrom.Day == 1 && c.DeliveryTo == c.DeliveryFrom.AddMonths(1) &&
            from >= new DateTimeOffset(c.DeliveryFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)) &&
            to <= new DateTimeOffset(c.DeliveryTo.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))));
        ReferenceKey R(string code) => new(data.ReferenceRevision, code);
        var settlement = PriceSettlementSelection.CurveTarget(R(target.Text("index_a_code")));
        return new(R(assignmentCode), R(physical.Product), Quantity(data, physical.EnergyQuantityCode), R(physical.DischargeFacility),
            R(physical.DischargeCustody), R(physical.Clock), new(settlement, quote, new(new(data.DefinitionRevision, row.Text("curve_code")), contract.Key),
                contract, null, One(PriceSettlementInputs.Fixings(data, settlement, quote, [contract]))));
    }
    private static LngCargoQuantity ResolveQuantity(PreviewData data, PhysicalLngWriteRequest body, LngValuationAssignment assignment)
    {
        var input = Quantity(data, Required(body.QuantityCode));
        if (input.Unit.Code != Required(body.QuantityUnit)) throw Invalid("Choose the exact unit of the selected input quantity specification.");
        LngMassEnergyConversion? conversion = null;
        if (body.ConversionCode is {} code)
        {
            var row = One(data.References["lng_mass_energy_conversions"].Where(c => c.Text("code") == code && c.Text("product_code") == body.Physical.Product));
            conversion = new(new(data.ReferenceRevision, code), Quantity(data, row.Text("mass_quantity_code")),
                Quantity(data, row.Text("energy_quantity_code")), ExactDecimal.Parse(row.Text("energy_per_mass")), row.Text("implementation"));
        }
        return new(new(ExactDecimal.Parse(Required(body.Quantity)), input.Key), assignment.Energy, conversion);
    }
    internal static async Task<LngIncoterms> ResolveIncoterms(NpgsqlConnection connection, PreviewData data, LngIncotermsSelection selected, CancellationToken ct)
    {
        if (selected is null) throw Invalid("Choose the Incoterms edition, rule and named place explicitly.");
        _ = One(data.References["incoterms_editions"].Where(e => e.Text("code") == selected.Edition));
        var rule = One(data.References["incoterms_rules"].Where(r => r.Text("code") == selected.Rule));
        await RequireLocations(connection, data.ReferenceRevision, [selected.NamedPlace], ct);
        ReferenceKey R(string code) => new(data.ReferenceRevision, code);
        return new(R(selected.Edition), new(R(selected.Rule), R(rule.Text("edition_code"))), R(selected.NamedPlace));
    }
    private async Task<(PhysicalLngInstrument Instrument, LngValuationAssignment Assignment, LngCargoQuantity Quantity)> Resolve(
        NpgsqlConnection connection, PreviewData data, InputFrame frame, PhysicalLngWriteRequest body, CancellationToken ct)
    {
        if (body.Convention != Convention || body.KnowledgeMode != "current" || body.Profile != Profile ||
            body.QuantityBasis != "gross_discharge_energy_total" || body.Tolerance is null || ExactDecimal.Parse(body.Tolerance) != default || body.Discounting != false)
            throw Invalid("Select the explicit fixed gross discharge-energy, zero-tolerance and undiscounted LNG profile.");
        var revision = await DeliveryRevision(connection, ct);
        if (body.Physical is null || body.Physical.ReferenceCatalogId != revision.CatalogId || body.Physical.ReferenceRevisionId != revision.RevisionId)
            throw Invalid("Select the configured exact LNG physical reference revision.");
        var mapping = await ResolveMapping(connection, body.Physical, Instant(body.DischargeFrom), Instant(body.DischargeTo), frame.At, ct);
        if (mapping.Id("definitionRevisionId") != data.DefinitionRevision.RevisionId)
            throw new MarketDataException("lng_market_context", "The physical mapping requires a different explicit LNG source revision.");
        var assignment = await Metadata(connection, data, body.Physical, mapping.Text("assignmentCode"), Instant(body.DischargeFrom), Instant(body.DischargeTo), ct);
        var cargo = new PhysicalLngCargoSide(Guid.NewGuid(), assignment, new(data.ReferenceRevision, body.Physical.LoadFacility),
            new(data.ReferenceRevision, body.Physical.LoadCustody), Instant(body.LoadFrom), Instant(body.LoadTo), Instant(body.DischargeFrom), Instant(body.DischargeTo));
        var price = ExactDecimal.Parse(Required(body.FixedPrice));
        if (price.CompareTo(ExactDecimal.Parse("1000000000000")) >= 0 || price.CompareTo(ExactDecimal.Parse("-1000000000000")) <= 0)
            throw Invalid("The fixed consideration price must have absolute value below 10^12.");
        var cash = new FixedSide(Guid.NewGuid(), new FlatPrice(new UnitPrice(price, assignment.Quote)), assignment.Quote, cargo.UnitQuantity,
            [new(cargo.DateFrom, cargo.DateTo, Instant(body.PaymentAt), new(1, 1), null)]);
        var quantity = ResolveQuantity(data, body, assignment);
        var instrument = new PhysicalLngInstrument(Guid.NewGuid(), cargo, cash, await ResolveIncoterms(connection, data, body.Incoterms, ct),
            Sign(body.Direction, true), Sign(body.ConsiderationDirection));
        instrument.ValidateBinding(quantity.Energy, null);
        return (instrument, assignment, quantity);
    }
    private async Task<object> Value(NpgsqlConnection connection, PreviewData data, InputFrame frame, PhysicalLngWriteRequest body, Guid instrumentId,
        PhysicalLngInstrument? draft, LngValuationAssignment assignment, LngCargoQuantity quantity,
        ImmutableArray<RetainedSideProgram>? retained, CancellationToken ct)
    {
        if (frame.ContextId is not {} contextId) throw Invalid("LNG valuation requires one certified calculation context.");
        if (frame.At >= Instant(body.LoadFrom)) throw new MarketDataException("lng_delivery_lifecycle", "This contractual LNG profile applies only before loading starts.");
        var programs = retained ?? [..draft!.Sides.Select((m, slot) => {
            var d = ScheduleDefinitionCompiler.Generate(m.Side, ct); var p = BookedSideProgram.Compile(d);
            return new RetainedSideProgram(slot, m.Side.Id, Guid.Empty, new(BookedSideProgram.Digest(p.Write()), d.Manifest, d.Family, d.EconomicIdentity, d.PricingBindingIdentity), p);
        })];
        if (programs.Length != 2 || !programs.Select(p => p.Slot).Order().SequenceEqual(new[] { 0, 1 })) throw Invalid("Retain exactly one LNG cargo and one consideration program.");
        var members = programs.OrderBy(p => p.Slot).Select(p => new RetainedLngMember(p.SideId,
            p.Slot == 0 ? Sign(body.Direction, true) : Sign(body.ConsiderationDirection), p.Program)).ToImmutableArray();
        _ = new RetainedLngInstrument(instrumentId, assignment,
            draft?.Incoterms ?? await ResolveIncoterms(connection, data, body.Incoterms, ct), members);
        var context = (await new KnowledgeStore(Catalog).Read(connection, contextId, ct)).Context;
        var snapshot = MarketSnapshotBinding.Bind(data.Snapshot(frame), contextId, SchedulePreview.ParseAt(context.KnownAt));
        var dependencies = PeriodPriceSelector.ProjectionAt(assignment.Lookup, snapshot) is {} request ? ImmutableArray.Create(request) : [];
        var market = data.Model.Build(snapshot, dependencies, ct);
        var sources = ScheduleMarketSources.ForPhysicalLng(market, [assignment]);
        var discount = new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted);
        var sides = new List<object>(); var total = ExactMoney.FromMoney(new(default, assignment.Quote.Currency));
        foreach (var program in programs.OrderBy(p => p.Slot))
        {
            var coefficient = program.Slot == 0 ? Sign(body.Direction, true) : Sign(body.ConsiderationDirection);
            var amount = sources.Evaluate(program.Program, program.SideId, market, discount, ct).PresentValue.Scale(quantity.Energy.Amount * coefficient);
            total = total.Add(amount);
            sides.Add(new { role = program.Slot == 0 ? "cargo" : "consideration", presentValue = amount.ToExactString() });
        }
        return new { presentValue = total.ToExactString(), sides, originalQuantity = quantity.Original.Amount.ToString(),
            energyQuantity = quantity.Energy.Amount.ToString(), energyUnit = Unit(data, quantity.Energy.Specification),
            currency = One(data.References["currencies"].Where(c => c.Text("code") == assignment.Quote.Currency.Code)).Text("display_code") };
    }
    public async Task<object> Preview(NpgsqlConnection connection, PreviewData data, InputFrame frame, PhysicalLngWriteRequest body, CancellationToken ct)
    { var resolved = await Resolve(connection, data, frame, body, ct); return await Value(connection, data, frame, body, resolved.Instrument.Id, resolved.Instrument, resolved.Assignment, resolved.Quantity, null, ct); }
    internal Task<RetainedScheduleTerms> Prepare(NpgsqlConnection connection, InputFrame frame, PhysicalLngWriteRequest body, CancellationToken ct) =>
        TradingBookingRead.Run(database, connection, frame, async (data, pinned) => {
            var resolved = await Resolve(connection, data, pinned, body, ct);
            _ = await Value(connection, data, pinned, body, resolved.Instrument.Id, resolved.Instrument, resolved.Assignment, resolved.Quantity, null, ct);
            return new RetainedScheduleTerms(data.DefinitionRevision, data.ReferenceRevision, resolved.Instrument);
        }, ct, physicalMarket: PhysicalMarketSelection.Lng);
    public async Task<JsonElement> Status(NpgsqlConnection connection, Guid operation, CancellationToken ct)
    {
        await using var query = ScheduleStore.Command(connection, "SELECT security.lng_operation_status($1,$2)::text", Catalog, operation);
        return JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);
    }
    public async Task<JsonElement> Save(NpgsqlConnection connection, InputFrame frame, PhysicalLngWriteRequest body, CancellationToken ct)
    {
        await using var query = ScheduleStore.Command(connection, "SELECT security.book_physical_lng($1,$2::jsonb,$3)::text", Catalog, JsonSerializer.Serialize(body, Json), frame.At);
        return JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);
    }
    public async Task<object> Editor(NpgsqlConnection connection, PreviewData data, InputFrame frame, Guid trade, CancellationToken ct)
    {
        var row = One(await PreviewDatabase.Rows(connection, """
            SELECT jsonb_build_object('request',t.request,'instrumentId',i.instrument_id,'definition',i.definition_revision_id,
                'assignment',i.assignment_code,'revision',r.revision,'energyQuantity',r.energy_quantity::text)::text
            FROM trading.lng_trade_revisions r JOIN trading.lng_instruments i USING(catalog_id,instrument_id)
            JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id) WHERE(r.catalog_id,r.trade_id)=($1,$2)
            """, [Catalog, trade], ct));
        if (row.Id("definition") != data.DefinitionRevision.RevisionId) throw Invalid("Use the exact retained LNG source revision.");
        var body = row.GetProperty("request").Deserialize<PhysicalLngWriteRequest>(Json)! with { SnapshotId = frame.Id };
        var assignment = await Metadata(connection, data, body.Physical, row.Text("assignment"), Instant(body.DischargeFrom), Instant(body.DischargeTo), ct);
        var quantity = ResolveQuantity(data, body, assignment);
        if (quantity.Energy.Amount != ExactDecimal.Parse(row.Text("energyQuantity"))) throw Invalid("Retained conversion evidence must reproduce the booked energy quantity exactly.");
        var programs = await new RetainedScheduleStore(database).LoadPrograms(connection, row.Id("instrumentId"), frame.ContextId, frame.ContextId is null ? null : frame.At, ct);
        return new { tradeId = trade, revision = int.Parse(row.Text("revision"), CultureInfo.InvariantCulture), terms = body,
            valuation = await Value(connection, data, frame, body, row.Id("instrumentId"), null, assignment, quantity, programs, ct) };
    }
    public async Task<object> Directory(NpgsqlConnection connection, CancellationToken ct)
    {
        var positions = await LngPositions.Read(connection, database.Manifest, [], [], ct);
        return new { trades = positions.OrderByDescending(p => int.Parse(p.Head.Text("publication_order"), CultureInfo.InvariantCulture)).Take(256).Select(p => new {
            tradeId = p.Trade, tradeReference = TradingReference.Trade(p.Head), revision = p.Head.Text("revision"), instrumentId = p.Binding, bookId = p.Book.Id("id"), bookName = p.Book.Text("name"),
            direction = p.Head.Text("direction") == "1" ? "receive" : "deliver", quantity = p.Head.Text("original_quantity"),
            energyQuantity = p.Head.Text("energy_quantity"), quantityUnit = p.Physical.Text("input_unit"),
            loadFrom = p.Head.Text("load_from"), loadTo = p.Head.Text("load_to"), dischargeFrom = p.Head.Text("discharge_from"), dischargeTo = p.Head.Text("discharge_to"),
            productName = p.Physical.Text("product_name"), loadFacilityName = p.Physical.Text("load_facility_name"), dischargeFacilityName = p.Physical.Text("discharge_facility_name")
        }) };
    }
}
