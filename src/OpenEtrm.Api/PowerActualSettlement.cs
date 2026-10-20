using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerIntervalAmountInput(string Start,string End,string Value,string? Shape);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerActualSettlementRequest(Guid SnapshotId,string Curve,Guid ContractId,string DeliveryDate,string KnownAt,
    string Product,int ProductVersion,int Minutes,string NonHolidays,string Holidays,string QuantityBasis,
    ReferenceKey QuantitySpecification,PowerIntervalAmountInput[] Quantities,string NumericalPolicy);

// An unsaved delivery-value calculation over collected facts, not a declaration
// of vendor finality, a booked obligation, or evidence that payment took place.
public static class PowerActualSettlement
{
    public const string Implementation="observed_interval_power_settlement_v1";
    internal static async Task<PreviewData> ReadData(PreviewDatabase database,NpgsqlConnection connection,CancellationToken ct)
    {
        var manifest=database.Manifest;
        await using var access=ScheduleStore.Command(connection,
            "SELECT security.market_fact_visible($1,$2,NULL) AND security.reference_version_visible($3,$4)",
            manifest.CatalogId,manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId);
        if(!Equals(await access.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        return await database.Read(connection,ct,observationIds:[]);
    }
    internal static void ValidateSource(PreviewData data,PowerPreview.ContractTerms terms,JsonElement source)
    {
        if(source.Id("catalog_id")!=data.DefinitionRevision.CatalogId || source.Id("reference_catalog_id")!=data.ReferenceRevision.CatalogId ||
            source.Id("reference_revision_id")!=data.ReferenceRevision.RevisionId || source.Text("quantity_code")!=terms.Energy.Text("code") ||
            source.Text("currency_code")!=terms.Quote.Text("currency_code") || source.Text("location_code")!=terms.Curve.Text("location_code") ||
            source.Text("location_code")!=terms.Index.Text("location_code"))
            throw new MarketDataException("power_source_units","The physical source must match the curve's exact catalog, references, currency and MWh quantity, and the curve/index location.");
    }
    public static async Task<object> Calculate(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        PowerActualSettlementRequest body,PowerPreview.ProductSet products,CancellationToken ct)
    {
        if(body.NumericalPolicy!=PowerSettlementPolicy.Preview.Implementation)
            throw new MarketDataException("settlement_policy","Select the explicit supported settlement numerical policy.");
        var terms=PowerPreview.SelectContract(data,frame,body.Curve,body.ContractId);
        if(terms.Contract.Kind!=ContractKind.Forward)
            throw new MarketDataException("power_contract","Delivery settlement requires a selected power forward contract.");
        if(!DateOnly.TryParseExact(body.DeliveryDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day) ||
            day<terms.Contract.DeliveryFrom || day>=terms.Contract.DeliveryTo)
            throw new MarketDataException("power_period","Choose one operating day inside the selected contract's delivery period.");
        var cutoff=SchedulePreview.ParseAt(body.KnownAt);
        var mappings=await PreviewDatabase.Rows(connection,
            "SELECT row_to_json(m)::text FROM market_data.curve_physical_price_sources m WHERE catalog_id=$1 AND revision_id=$2 AND curve_code=$3",
            [data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId,body.Curve],ct);
        if(mappings.Length!=1)
            throw new MarketDataException("power_source_mapping","The selected curve must have exactly one explicit physical settlement-price source in its definition version.");
        var mapping=mappings[0];var sourceId=mapping.Id("source_id");
        var metadata=await PowerHistory.Source(connection,sourceId,ct);var source=metadata.Display;
        ValidateSource(data,terms,source);
        PowerImportProtocol.ValidateFeed(source.Text("provider"),source.Text("dataset"),source.Text("market"),metadata.Minutes);
        var product=PowerPreview.SelectProduct(products,body.Product,body.ProductVersion);
        if(product.Clock!=metadata.Clock || body.Minutes!=metadata.Minutes)
            throw new MarketDataException("power_source_clock","Use the mapped source's exact market clock and native interval resolution.");
        var clock=PowerClock.FromRule(metadata.Clock,TimeZoneDatabase.Installed());
        var next=day.AddDays(1);var holidays=PowerPreview.Holidays(data,terms.Calendar,day,next);
        var mask=new TimeOfUse(body.Minutes,body.NonHolidays,body.Holidays);
        var fullDay=PowerHours.Compile(day,next,clock,TimeOfUse.All(body.Minutes),holidays,body.Minutes);
        var selected=PowerHours.Compile(day,next,clock,mask,holidays,body.Minutes);
        if(selected.Length==0)throw new MarketDataException("settlement_coverage","The selected delivery day has no contracted intervals.");
        var history=await PowerHistory.ReadPrices(connection,sourceId,fullDay[0].Start,fullDay[^1].End,cutoff,ct);
        // Require a fully collected native day before selecting the contract's
        // mask. Neither a partial day nor a forward quote fills a missing actual.
        if(history.Rows.Length!=fullDay.Length || history.Rows.Select(r=>r.Start).Distinct().Count()!=history.Rows.Length)
            throw new MarketDataException("settlement_coverage","The physical source does not completely cover this operating day at the selected knowledge cutoff.");
        var prices=history.Rows.ToDictionary(r=>r.Start);
        if(fullDay.Any(p=>!prices.TryGetValue(p.Start,out var row)||row.End!=p.End||row.KnownAt>cutoff))
            throw new MarketDataException("settlement_coverage","Delivery observations must match every exact native UTC interval and the selected cutoff.");
        var input=data.References["quantity_specifications"].SingleOrDefault(q=>new ReferenceKey(data.ReferenceRevision,q.Text("code"))==body.QuantitySpecification);
        if(input.ValueKind==JsonValueKind.Undefined || body.QuantityBasis switch
            {"interval_power"=>input.Text("unit_code")!="megawatt" || input.Text("dimension_code")!="power",
             "interval_energy"=>input.Text("unit_code")!="megawatt_hour" || input.Text("dimension_code")!="energy",_=>true})
            throw new MarketDataException("power_quantity","Choose interval MW with explicit shape or already-shaped interval MWh and an exact compatible specification.");
        if(body.Quantities is null || body.Quantities.Length!=selected.Length || body.Quantities.Any(q=>q is null))
            throw new MarketDataException("settlement_coverage","Supply one quantity for each selected native UTC interval.");
        if(body.Quantities.Any(q=>string.IsNullOrWhiteSpace(q.Value)))
            throw new MarketDataException("power_quantity","Each interval requires an explicit numeric quantity; an unknown value is not zero.");
        var energy=PowerPreview.QuantityDefinition(data,terms.Energy);
        var quantities=PowerIntervalQuantities.Compile(selected,body.Quantities.Select(q=>new PowerIntervalQuantity(
            SchedulePreview.ParseAt(q.Start),SchedulePreview.ParseAt(q.End),new(ExactDecimal.Parse(q.Value),body.QuantitySpecification),
            q.Shape is null?null:ExactDecimal.Parse(q.Shape))).ToArray(),PowerPreview.QuantityDefinition(data,input),energy);
        var currency=data.References["currencies"].Single(c=>c.Text("code")==terms.Quote.Text("currency_code"));
        if(currency.Text("display_minor_units")!="2")
            throw new MarketDataException("settlement_policy","The selected numerical policy requires two currency minor units.");
        var market=source.Text("market");
        var index=new PowerIndex(new(data.ReferenceRevision,terms.Index.Text("code")),new(data.ReferenceRevision,terms.Quote.Text("currency_code")),
            energy.Unit,energy.Key,"Collected delivery price",market=="DA"?"Day-ahead":"Real-time");
        var fixings=selected.Select(p=>new PowerFixing(p.Start,p.End,index,prices[p.Start].Price.Value,null)).ToArray();
        var settled=PowerSettlement.Calculate(quantities,energy,fixings,index,PowerSettlementPolicy.Preview);
        return new {implementation=Implementation,summary=settled.Summary,energy=settled.Energy,cash=settled.Cash,
            saved=false,publicationStatus="collected",quantityImplementation=PowerIntervalQuantities.Implementation,knownAt=cutoff,historyContextId=history.ContextId,sourceId,source,
            deliveryDate=body.DeliveryDate,quantityBasis=body.QuantityBasis,product=product.Code,productVersion=product.Version,mask,
            clock=new {clock.Id,clock.Fingerprint,rules=clock.Rules},
            pins=new {definitions=data.Definitions["revisions"][0],mapping,curve=terms.Curve,contract=terms.Contract,quote=terms.Quote,
                energyDefinition=terms.Energy,inputDefinition=input,indexDefinition=terms.Index,clockRule=metadata.Clock,calendar=terms.Calendar,holidays},
            rows=quantities.Select(q=>new {start=q.Interval.Start,end=q.Interval.End,label=PowerHours.Label(q.Interval,true),
                q.Interval.OffsetMinutes,q.Interval.Occurrence,energy=q.Energy,price=prices[q.Interval.Start].Price,
                contribution=q.Energy.Times(prices[q.Interval.Start].Price),
                lineage=new {sourceId,prices[q.Interval.Start].BatchId,prices[q.Interval.Start].KnownAt,revision=prices[q.Interval.Start].Revision}}).ToArray()};
    }
}
