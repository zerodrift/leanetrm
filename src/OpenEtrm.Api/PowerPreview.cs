using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerCatalogRequest(Guid SnapshotId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerAmountInput(string Value,ReferenceKey Specification);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerDisplayInput(int Quantity,int Price,int Money);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerPreviewRequest(Guid SnapshotId,string Curve,Guid ContractId,string Product,int ProductVersion,string Block,
    int Minutes,string HourLabels,PowerAmountInput Quantity,string QuantityBasis,string? Allocation,string PriceType,string Timing,
    string NonHolidays,string Holidays,string NumericalPolicy,PowerDisplayInput Display);

// Unsaved, explicitly synthetic hour and settlement checks over published TOU
// products. This is not a replacement for the selected curve's contractual profile.
public static class PowerPreview
{
    // A published TOU product version with its resolved clock rule.
    public sealed record Product(string Code,string Name,int Version,ClockRule Clock,int Start,int End,bool Saturday);
    // Products whose clock is outside this workspace's reference data are listed as unavailable, never dropped silently.
    public sealed record ProductSet(ImmutableArray<Product> Available,ImmutableArray<TouProductVersion> Unavailable);
    public static ProductSet Products(IEnumerable<TouProductVersion> published,IReadOnlyCollection<ClockChoice> clocks)
    {
        var resolved=published.Select(v=>(Version:v,Clock:clocks.SingleOrDefault(c=>c.Reference==v.Clock))).ToArray();
        return new([..resolved.Where(r=>r.Clock is not null).Select(r=>new Product(r.Version.Code,r.Version.Name,r.Version.Version,r.Clock!.Rule,
            r.Version.PeakStartHour,r.Version.PeakEndHour,r.Version.PeakSaturday))],[..resolved.Where(r=>r.Clock is null).Select(r=>r.Version)]);
    }
    // Engine capabilities, not reference data: the supported mask blocks, grids and labels.
    private static readonly (string Code,string Name)[] Blocks=[("all","All hours"),("peak","On-peak"),("off","Off-peak")];
    private static readonly int[] Intervals=[60,30,15,5];
    private static readonly (string Code,string Name)[] HourLabels=[("HE","Hour ending · HE"),("HB","Hour beginning · HB")];
    // Suggested index classification labels; any explicit label up to 40 characters is accepted.
    private static readonly string[] PriceTypes=["Test","Official"],Timings=["End of day","Intraday"];
    private static TimeOfUse Mask(Product p,string block,int minutes) => block switch
    {
        "all"=>TimeOfUse.All(minutes),
        "peak"=>TimeOfUse.WeekdayBlock(minutes,p.Start,p.End,p.Saturday),
        "off"=>TimeOfUse.WeekdayBlock(minutes,p.Start,p.End,p.Saturday).Complement(),
        _=>throw new MarketDataException("tou_grid","Choose all hours, on-peak or off-peak.")
    };
    // The one explicitly bound settlement index for a contract: the curve's own index,
    // or a futures series' product terms. Anything else has no index (null).
    private static string? IndexCode(PreviewData data,JsonElement curve,MarketContract contract)
    {
        var target=data.References["settlement_targets"].Single(t=>t.Text("code")==curve.Text("settlement_target_code"));
        if(target.Text("kind")=="index" && target.OptionalText("index_b_code") is null)return target.OptionalText("index_a_code");
        if(target.Text("kind")!="future_series" || contract.FuturesSeries is not {} seriesKey)return null;
        var series=data.References["futures_series"].Single(s=>s.Text("code")==seriesKey.Code);
        var link=data.References.GetValueOrDefault("futures_product_terms",[]).SingleOrDefault(i=>i.Text("product_code")==series.Text("product_code"));
        return link.ValueKind==JsonValueKind.Undefined?null:link.OptionalText("index_code");
    }
    private static JsonElement Index(PreviewData data,string code)=>data.References["price_index_definitions"].Single(i=>i.Text("code")==code);
    private static JsonElement Calendar(PreviewData data,JsonElement index)=>data.References["calendars"].Single(c=>c.Text("code")==index.Text("calendar_code"));
    // Closed dates inside [from,to) from the index calendar; the calendar must cover the whole period.
    private static DateOnly[] Holidays(PreviewData data,JsonElement calendar,DateOnly from,DateOnly to)
    {
        var code=calendar.Text("code");
        if(DateWorkspace.Date(calendar.Text("coverage_from"))>from || DateWorkspace.Date(calendar.Text("coverage_to"))<to)
            throw new MarketDataException("power_calendar",$"Calendar {code} does not cover the whole delivery period.");
        return data.References["calendar_days"].Where(d=>d.Text("calendar_code")==code && d.Text("is_business_day")=="false")
            .Select(d=>DateWorkspace.Date(d.Text("business_date"))).Where(d=>d>=from && d<to).Order().ToArray();
    }
    private static bool PerMwhOutright(PreviewData data,JsonElement c)
    {
        var quote=data.References["quote_specifications"].Single(q=>q.Text("code")==c.Text("quote_code"));
        var quantity=data.References["quantity_specifications"].Single(q=>q.Text("code")==quote.Text("quantity_code"));
        // The hour engine settles energy in MWh; the quote must be an outright price per MWh of the curve's own commodity.
        return quantity.Text("commodity_code")==c.Text("commodity_code") && quantity.Text("unit_code")=="megawatt_hour" && quote.Text("kind")=="outright";
    }
    public static object Catalog(PreviewData data,InputFrame frame,ProductSet set)
    {
        var products=set.Available;
        var powerCommodities=data.Definitions["curves"].Where(c=>PerMwhOutright(data,c)).Select(c=>c.Text("commodity_code")).ToHashSet(StringComparer.Ordinal);
        return new
        {
            products=products.Select(p=>new {p.Code,p.Name,p.Version,clock=p.Clock.Code}),
            unavailableProducts=set.Unavailable.Select(v=>new {v.Code,v.Name,v.Version,reason="Its market clock is outside this workspace's reference data."}),
            blocks=Blocks.Select(b=>new {code=b.Code,name=b.Name}),intervals=Intervals,
            hourLabels=HourLabels.Select(h=>new {code=h.Code,name=h.Name}),
            classifications=new {priceTypes=PriceTypes,timings=Timings},
            presets=products.SelectMany(p=>Blocks.SelectMany(block=>Intervals.Select(minutes=>
            {
                var mask=Mask(p,block.Code,minutes);
                return new {product=p.Code,block=block.Code,minutes,mask.NonHolidays,mask.Holidays,
                    columns=Enumerable.Range(0,mask.Columns).Select(i=>new {
                        he=$"HE{i*minutes/60+1:D2}"+(minutes==60?"":$" +{i*minutes%60:D2}m"),
                        hb=$"HB{i*minutes/60:D2}"+(minutes==60?"":$" +{i*minutes%60:D2}m")}).ToArray()};
            }))),
            // Only contracts with one explicit settlement index are offered; each carries its holiday calendar.
            curves=data.Definitions["curves"].Where(c=>PerMwhOutright(data,c)).Select(c=>new {code=c.Text("code"),
                contracts=data.Contracts.Where(k=>frame.CurveContracts.TryGetValue(c.Text("code"),out var members)&&members.Contains(k.Key.Id))
                    .Select(k=>(Contract:k,Index:IndexCode(data,c,k))).Where(k=>k.Index is not null).Select(k=>new {
                        id=k.Contract.Key.Id,from=k.Contract.DeliveryFrom.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),to=k.Contract.DeliveryTo.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
                        holidayCalendar=Calendar(data,Index(data,k.Index!)).Text("code")}).ToArray()}).Where(c=>c.contracts.Length>0),
            weekdays=new[]{"Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"},
            quantityChoices=data.References["quantity_specifications"].Where(q=>powerCommodities.Contains(q.Text("commodity_code")) && q.Text("unit_code") is "megawatt" or "megawatt_hour")
                .Select(q=>new{basis=q.Text("unit_code")=="megawatt"?"flat_power":"total_energy",
                    label=(q.Text("unit_code")=="megawatt"?"Flat MW over selected hours":"Total MWh for the delivery period")+" · "+q.Text("basis_code").Replace('_',' '),
                    specification=new ReferenceKey(data.ReferenceRevision,q.Text("code"))}),
            display=new PowerDisplayInput(3,4,3),
            numericalPolicy=PowerSettlementPolicy.Preview.Implementation
        };
    }
    public static object Calculate(PreviewData data,InputFrame frame,PowerPreviewRequest body,ProductSet set)
    {
        if(body.Quantity is null || string.IsNullOrWhiteSpace(body.Quantity.Value) || body.Display is null || !HourLabels.Any(h=>h.Code==body.HourLabels) ||
            body.NumericalPolicy!=PowerSettlementPolicy.Preview.Implementation ||
            new[]{body.PriceType,body.Timing}.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>40||s.Any(char.IsControl)))
            throw new MarketDataException("power_input","Supply the quantity, hour labels, classifications, display places and the explicit numerical policy.");
        if(set.Unavailable.Any(v=>v.Code==body.Product))
            throw new MarketDataException("clock_reference","This TOU product's market clock is outside this workspace's reference data.");
        var product=set.Available.SingleOrDefault(p=>p.Code==body.Product)??throw new MarketDataException("power_input","Choose a published TOU product.");
        // The check is pinned to the product version its hour grid was built from; a newer publication is never substituted.
        if(product.Version!=body.ProductVersion)
            throw new MarketDataException("tou_conflict",$"TOU product {product.Code} is now version {product.Version}; reload the product before checking hours.");
        // Require a known preset choice even when its grids have been edited.
        _=Mask(product,body.Block,body.Minutes);
        var mask=new TimeOfUse(body.Minutes,body.NonHolidays,body.Holidays);
        var curve=data.Definitions["curves"].SingleOrDefault(c=>c.Text("code")==body.Curve);
        if(curve.ValueKind==JsonValueKind.Undefined)throw new MarketDataException("missing_curve","Choose an available curve.");
        var quote=data.References["quote_specifications"].Single(q=>q.Text("code")==curve.Text("quote_code"));
        var quantity=data.References["quantity_specifications"].Single(q=>q.Text("code")==quote.Text("quantity_code"));
        if(!PerMwhOutright(data,curve) || quote.Number("multiplier")!=ExactDecimal.Parse("1"))
            throw new MarketDataException("power_units","This example requires an outright price per MWh with multiplier one.");
        var built=data.Build(frame,body.Curve);
        var value=built.Values.SingleOrDefault(v=>v.Request.Contract.Id==body.ContractId)??throw new MarketDataException("power_contract","Choose a contract in the selected curve and market view.");
        var contract=data.Contracts.Single(k=>k.Key.Id==body.ContractId);
        var indexCode=IndexCode(data,curve,contract)??throw new MarketDataException("power_index","The preview requires one explicitly bound settlement index.");
        var indexDefinition=Index(data,indexCode);var calendar=Calendar(data,indexDefinition);
        var holidays=Holidays(data,calendar,contract.DeliveryFrom,contract.DeliveryTo);
        var clock=PowerClock.FromRule(product.Clock,TimeZoneDatabase.Installed());
        var basis=body.QuantityBasis switch{"flat_power"=>PowerQuantityBasis.FlatPower,"total_energy"=>PowerQuantityBasis.TotalEnergy,_=>throw new MarketDataException("power_quantity","Choose total MWh or flat MW explicitly.")};
        var allocation=body.Allocation switch{null=>(PowerAllocation?)null,"flat_rate"=>PowerAllocation.FlatRate,_=>throw new MarketDataException("power_quantity","Choose an explicit supported total-energy allocation.")};
        var source=data.References["quantity_specifications"].SingleOrDefault(q=>new ReferenceKey(data.ReferenceRevision,q.Text("code"))==body.Quantity.Specification);
        if(source.ValueKind==JsonValueKind.Undefined)throw new MarketDataException("power_quantity","Select an exact quantity specification from this market's reference revision.");
        QuantitySpecification Definition(JsonElement q)=>new(new(data.ReferenceRevision,q.Text("code")),new(data.ReferenceRevision,q.Text("commodity_code")),
            new(data.ReferenceRevision,q.Text("basis_code")),new(data.ReferenceRevision,q.Text("unit_code")),new(data.ReferenceRevision,q.Text("dimension_code")));
        var schedule=PowerQuantitySchedule.Compile(new(ExactDecimal.Parse(body.Quantity.Value),body.Quantity.Specification),basis,Definition(source),Definition(quantity),
            contract.DeliveryFrom,contract.DeliveryTo,clock,mask,holidays,body.Minutes,allocation);
        var periods=schedule.Intervals.Select(p=>p.Interval).ToArray();
        var quantityFormat=new AmountFormat(body.Display.Quantity,CashTieRule.ToEven,true,true);
        var priceFormat=new AmountFormat(body.Display.Price,CashTieRule.ToEven,true,true);
        var moneyFormat=new AmountFormat(body.Display.Money,CashTieRule.ToEven,true,false);
        var currency=data.References["currencies"].Single(c=>c.Text("code")==quote.Text("currency_code"));
        var powerUnit=data.References["units"].Single(u=>u.Text("code")=="megawatt" && u.Text("dimension_code")=="power");
        if(currency.Text("display_minor_units")!="2")
            throw new MarketDataException("settlement_policy","The preview's explicit cash rounding supports two minor units.");
        var references=new ReferenceRevision(quote.Id("catalog_id"),quote.Id("revision_id"));
        if(indexDefinition.Text("commodity_code")!=curve.Text("commodity_code") ||
            indexDefinition.Text("currency_code")!=quote.Text("currency_code") ||
            indexDefinition.Text("quote_unit_code")!=quantity.Text("unit_code") ||
            indexDefinition.Number("quote_multiplier")!=ExactDecimal.Parse("1"))
            throw new MarketDataException("power_index","Index commodity, currency and unit must match the selected power quote exactly.");
        var index=new PowerIndex(new(references,indexCode),new(references,quote.Text("currency_code")),new(references,"megawatt_hour"),new(references,quantity.Text("code")),body.PriceType,body.Timing);
        // All-in values have no invented LMP decomposition. Component-aware
        // settlement is independently checked in the domain tests.
        var fixings=periods.Select(p=>new PowerFixing(p.Start,p.End,index,value.Price.Value,null)).ToArray();
        var settled=PowerSettlement.Calculate(schedule,fixings,index,PowerSettlementPolicy.Preview);
        var currencyLabel=new AmountLabel(index.Currency,currency.Text("display_code"));
        var energyLabel=new AmountLabel(schedule.EnergyDefinition.Key,"MWh");
        var priceText=value.Price.ToString(priceFormat,currencyLabel,energyLabel);
        return new {summary=settled.Summary,currency=currency.Text("display_code"),mark=value.Price.Value.ToString(),at=frame.At,
            amounts=new{input=schedule.Input,energy=settled.Energy,cash=settled.Cash,
                energyText=settled.Energy.ToString(quantityFormat,energyLabel),cashText=settled.Cash.ToString(moneyFormat,currencyLabel),
                priceText},
            from=contract.DeliveryFrom.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),to=contract.DeliveryTo.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            synthetic=true,saved=false,clock=new {clock.Id,clock.Fingerprint,rules=clock.Rules},
            pins=new {definitions=data.Definitions["revisions"][0],quote,quantity,source,curve,powerUnit,indexDefinition},
            holidays,holidayCalendar=calendar.Text("code"),clockRule=product.Clock,mask,quantityBasis=body.QuantityBasis,body.Allocation,body.Display,
            quantityImplementation=PowerQuantitySchedule.Implementation,product=product.Name,productVersion=product.Version,
            days=periods.GroupBy(p=>p.OperatingDate).Select(g=>new {date=g.Key.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
                intervals=g.Count(),hours=new AmountFormat(18,CashTieRule.ToEven,false,true).FormatFraction(g.Sum(p=>(p.End-p.Start).Ticks),TimeSpan.TicksPerHour)}).ToArray(),
            rows=schedule.Intervals.Select(p=>new {label=PowerHours.Label(p.Interval,body.HourLabels=="HE"),p.Interval.Start,p.Interval.End,p.Interval.OffsetMinutes,p.Interval.Occurrence,p.Interval.Holiday,
                energy=p.Energy,energyText=p.Energy.ToString(quantityFormat,energyLabel),price=value.Price.Value.ToString(),priceText}).ToArray()};
    }
}
