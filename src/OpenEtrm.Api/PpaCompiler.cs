using System.Collections.Immutable;
using System.Text.Json;
using System.Globalization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// Compilation reads sealed definitions only. Market inputs and quantity histories belong to preview.
internal static class PpaCompiler
{
    internal const int MaximumMembers=8,MaximumAssets=100,MaximumBindings=256;
    internal const long MaximumRequestRows=2_000_000,MaximumDetailRows=35_136;
    internal const int MaximumResponseBytes=8*1024*1024;
    internal static MarketDataException Error(string code,string message)=>new(code,message);
    internal static DateOnly Day(string? value)=>DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)?day:throw Error("ppa_term","Use an explicit ISO operating date.");
    internal static ExactDecimal Number(string? value)=>ExactDecimal.Parse(value??"");
    internal static ExactDecimal Mw(string? value)
    {var n=Number(value);if(n.CompareTo(default)<=0)throw Error("ppa_quantity","Contract MW must be positive.");return n;}
    internal static PpaRole Role(string? value)=>value switch {
        "energy_delivery"=>PpaRole.EnergyDelivery,"certificate_delivery"=>PpaRole.CertificateDelivery,"capacity_delivery"=>PpaRole.CapacityDelivery,
        "strike_payment"=>PpaRole.StrikePayment,"indexed_payment"=>PpaRole.IndexedPayment,"fixed_charge"=>PpaRole.FixedCharge,
        "certificate_payment"=>PpaRole.CertificatePayment,"capacity_payment"=>PpaRole.CapacityPayment,_=>throw Error("ppa_side","Select an explicit supported role.")};
    internal static string Role(PpaRole value)=>value switch {PpaRole.EnergyDelivery=>"energy_delivery",PpaRole.CertificateDelivery=>"certificate_delivery",PpaRole.CapacityDelivery=>"capacity_delivery",PpaRole.StrikePayment=>"strike_payment",PpaRole.IndexedPayment=>"indexed_payment",PpaRole.FixedCharge=>"fixed_charge",PpaRole.CertificatePayment=>"certificate_payment",PpaRole.CapacityPayment=>"capacity_payment",_=>throw Error("ppa_side","Unknown role.")};
    internal static void Size(PpaTermsInput? input)
    {
        if(input is null || input.Delivery is null || input.Memberships is null || input.NormalizationAssets is null)throw Error("ppa_term","Supply complete instrument terms.");
        if(input.Memberships.Length is <2 or >MaximumMembers || input.NormalizationAssets.Length>MaximumAssets)throw Error("ppa_limit","Instrument composition exceeds the bounded consumer.");
        var from=Day(input.Delivery.From);var to=Day(input.Delivery.To);var minutes=input.Delivery.IntervalMinutes;
        if(minutes is not(15 or 60))throw Error("ppa_cadence","Select native 15- or 60-minute delivery.");
        if((to.Year-from.Year)*12+to.Month-from.Month>(minutes==60?360:120))throw Error("ppa_size","Delivery exceeds the selected cadence limit.");
        foreach(var m in input.Memberships)
        {
            if(m?.Side?.Quantity is not {} q || m.Side.PaymentRule is null || q.Sources is null || q.Assets is null || q.Slots is null || q.Slots.Any(x=>x is null) || q.Sources.Any(string.IsNullOrWhiteSpace) || q.Assets.Any(string.IsNullOrWhiteSpace))throw Error("ppa_term","Supply complete side terms.");
            if(q.Sources.Length>MaximumAssets || q.Assets.Length>MaximumAssets || q.Slots.Length>MaximumMembers || m.Side.FixedPrice?.Steps is {Length:>360})throw Error("ppa_limit","Side arrays exceed supported limits.");
        }
    }
    internal static PpaInstrument Compile(PreviewData data,PpaTermsInput input,IReadOnlyList<TouProductVersion> products,Guid? instrumentId=null,IReadOnlyDictionary<SideSlot,Guid>? sideIds=null)
    {
        Size(input);
        if(input.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)throw Error("ppa_definition_revision","Instrument terms require the selected exact definition revision.");
        if(new ReferenceRevision(input.ReferenceCatalogId,input.ReferenceRevisionId)!=data.ReferenceRevision)throw Error("ppa_reference_revision","Instrument terms require the selected exact reference revision.");
        ReferenceKey R(string code)=>new(data.ReferenceRevision,code);
        ReferenceKey Reference(string table,string code){One(data.References[table],code);return R(code);}
        var currency=Reference("currencies",input.Currency);var unit=PpaQuantitiesStore.Quantity(data,input.UnitQuantity);
        if(unit.Unit.Code!="megawatt" || unit.Dimension.Code!="power")throw Error("ppa_quantity_unit","The contract unit is exactly one MW.");
        var clock=PpaQuantitiesStore.Clock(data,input.Delivery.Clock);
        var delivery=new PpaDelivery(R(input.Delivery.Clock),input.Delivery.IntervalMinutes,Day(input.Delivery.From),Day(input.Delivery.To));
        var allAssets=input.NormalizationAssets.Length==0?ImmutableArray<GenerationAsset>.Empty:PpaQuantitiesStore.Assets(data,input.NormalizationAssets);
        var members=input.Memberships.Select(m=>
        {
            var slot=new SideSlot(Role(m.Role),m.Ordinal);var s=m.Side;var q=s.Quantity;
            if(q.Sources.Distinct().Count()!=q.Sources.Length || q.Assets.Distinct().Count()!=q.Assets.Length)throw Error("ppa_quantity_reference","Quantity references must be distinct.");
            PpaQuantity quantity=q.Kind switch {
                "volume" when q.Sources.Length>0 && q.Assets.Length==0 && q.Slots.Length==0 && q.Capacity is null && q.Energy is null && q.Hours is null => new VolumeQuantity([..q.Sources.Select(c=>PpaQuantitiesStore.Source(data,c).Key)],PpaQuantitiesStore.Stage(q.Stage)),
                "profile" when q.Sources.Length==0 && q.Assets.Length==0 && q.Slots.Length==0 && q.Stage is null && q.Capacity is not null && q.Energy is not null && q.Hours is not null=>new ProfileQuantity(PpaQuantitiesStore.Quantity(data,q.Capacity).Key,PpaQuantitiesStore.Quantity(data,q.Energy).Key,Hours(data,q.Hours,delivery,products)),
                "slots" when q.Sources.Length==0 && q.Assets.Length==0 && q.Slots.Length>0 && q.Stage is null && q.Capacity is null && q.Energy is null && q.Hours is null=>new SlotQuantity([..q.Slots.Select(t=>new SideSlot(Role(t.Role),t.Ordinal))]),
                "accredited" when q.Sources.Length==0 && q.Assets.Length>0 && q.Slots.Length==0 && q.Stage is null && q.Capacity is null && q.Energy is null && q.Hours is null=>new AccreditedQuantity([..PpaQuantitiesStore.Assets(data,q.Assets).Select(a=>a.Key)]),
                _=>throw Error("ppa_quantity_reference","Select exactly one explicit quantity variant, with unused fields null or empty.")};
            var terms=new PpaSideTerms(delivery,quantity,currency,new(Number("1"),unit.Key),Payment(data,s.PaymentRule));
            var id=sideIds is not null && sideIds.TryGetValue(slot,out var retained)?retained:Guid.NewGuid();
            PpaSide side=s.Kind switch {
                "energy" when s.Location is not null && s.FixedPrice is null && s.IndexPrice is null && s.Attribute is null=>new PowerPhysicalSide(id,terms,Reference("locations",s.Location)),
                "fixed" when s.Location is null && s.FixedPrice is not null && s.IndexPrice is null && s.Attribute is null=>new PpaFixedSide(id,terms,Fixed(data,currency,s.FixedPrice)),
                "indexed" when s.Location is null && s.FixedPrice is null && s.IndexPrice is not null && s.Attribute is null=>new PpaFloatingSide(id,terms,Index(data,currency,s.IndexPrice)),
                "certificate" when s.Location is null && s.FixedPrice is null && s.IndexPrice is null && s.Attribute is {Rule:null,Conversion:not null,Eligibility:not null,Holding:not null,Disposition:not null} a=>new CertificateDeliverySide(id,terms,PpaQuantitiesStore.Quantity(data,a.Quantity).Key,Number(a.Conversion),a.Eligibility,a.Holding,a.Disposition),
                "capacity" when s.Location is null && s.FixedPrice is null && s.IndexPrice is null && s.Attribute is {Rule:not null,Conversion:null,Eligibility:null,Holding:null,Disposition:null} a=>new CapacityDeliverySide(id,terms,PpaQuantitiesStore.Quantity(data,a.Quantity).Key,a.Rule),
                _=>throw Error("ppa_side","Select exactly one explicit side variant, with unused fields null.")};
            return new PpaMembership(slot,side,Number(m.Coefficient));
        }).ToImmutableArray();
        var instrument=new PpaInstrument(instrumentId??Guid.NewGuid(),data.DefinitionRevision,data.ReferenceRevision,members,[..allAssets.Select(a=>a.Key)],input.Discounting,input.Implementation);
        // Validate calendar generation and price coverage without reading any market observations.
        foreach(var member in instrument.Memberships)
            for(var month=delivery.From;month<delivery.To;month=month.AddMonths(1))
            {member.Side.Terms.DateRule.Generate(month,month.AddMonths(1));if(member.Side is PpaFixedSide fixedSide){fixedSide.Price.At(month);fixedSide.Price.At(month.AddMonths(1).AddDays(-1));}}
        return instrument;
    }
    internal static async Task ValidateDefinitions(Npgsql.NpgsqlConnection connection,PreviewData data,PpaInstrument instrument,CancellationToken ct)
    {
        var assets=instrument.NormalizationAssets.IsEmpty?ImmutableArray<GenerationAsset>.Empty:PpaQuantitiesStore.Assets(data,[..instrument.NormalizationAssets.Select(a=>a.Code)]);
        var outputs=new Dictionary<SideSlot,ReferenceKey>();
        foreach(var m in instrument.Memberships.Where(m=>m.Side.Terms.Quantity is not SlotQuantity))
        {
            var side=m.Side;ReferenceKey output;
            switch(side.Terms.Quantity)
            {
                case ProfileQuantity p:
                    _=new ContractProfileQuantity(PpaQuantitiesStore.Clock(data,instrument.Delivery.Clock.Code),instrument.Delivery.IntervalMinutes,p.Hours,PpaQuantitiesStore.Quantity(data,p.Capacity.Code),PpaQuantitiesStore.Quantity(data,p.Energy.Code));output=p.Energy;break;
                case VolumeQuantity v:
                    var sources=v.Sources.Select(s=>PpaQuantitiesStore.Source(data,s.Code)).ToArray();
                    if(sources.Length!=assets.Length || sources.Select(s=>s.Asset).Distinct().Count()!=sources.Length || !sources.Select(s=>s.Asset).ToHashSet().SetEquals(instrument.NormalizationAssets))throw Error("ppa_normalization","Every volume side requires one contribution from every declared normalization asset.");
                    if(sources.Any(s=>s.Clock!=instrument.Delivery.Clock || s.IntervalMinutes!=instrument.Delivery.IntervalMinutes) || assets.Any(a=>a.Clock!=instrument.Delivery.Clock))throw Error("ppa_cadence","Sources and assets must retain the exact native delivery clock and cadence.");
                    var metadata=await PreviewDatabase.Rows(connection,"SELECT market_data.read_interval_series_header(id)::text FROM unnest($1::uuid[])id",[sources.Select(s=>s.MeteredSeries).ToArray()],ct);
                    if(metadata.Any(r=>r.ValueKind==JsonValueKind.Null)||metadata.Length!=sources.Select(s=>s.MeteredSeries).Distinct().Count())throw new PreviewDeniedException();
                    var energies=metadata.Select(r=>r.OptionalText("quantity_code")).Distinct().ToArray();
                    if(energies.Length!=1 || energies[0] is not {} code)throw Error("ppa_quantity_unit","Every meter declares one identical exact energy specification.");
                    var energy=PpaQuantitiesStore.Quantity(data,code);output=energy.Key;
                    foreach(var asset in assets)_=new ContractProfileQuantity(asset.OperatingClock,instrument.Delivery.IntervalMinutes,new(BlockMembership.All,null,[]),asset.CapacityQuantity,energy);
                    if(side is PowerPhysicalSide physical && assets.Any(a=>a.Location!=physical.Location))throw Error("ppa_delivery_location","Every energy asset must deliver at the declared physical location.");
                    break;
                case AccreditedQuantity a when side is CapacityDeliverySide capacity:
                    if(!a.Assets.ToHashSet().SetEquals(instrument.NormalizationAssets) || assets.Any(asset=>asset.Clock!=instrument.Delivery.Clock))throw Error("ppa_normalization","Capacity requires the same exact asset set and delivery clock.");
                    var target=PpaQuantitiesStore.Quantity(data,capacity.CapacityQuantity.Code);
                    if(target.Unit.Code!="megawatt_day" || target.Dimension.Code!="energy" || assets.Any(asset=>asset.CapacityQuantity.Commodity!=target.Commodity))throw Error("ppa_quantity_unit","Capacity entitlement requires MW-day of the same exact commodity.");output=target.Key;break;
                default:throw Error("ppa_quantity_reference","Unsupported side quantity binding.");
            }
            if(side is CertificateDeliverySide certificate)
            {
                var target=PpaQuantitiesStore.Quantity(data,certificate.CertificateQuantity.Code);
                if(target.Dimension.Code!="certificate" || target.Commodity!=PpaQuantitiesStore.Quantity(data,output.Code).Commodity)throw Error("ppa_quantity_unit","Certificate output requires the same commodity and certificate dimension.");output=target.Key;
            }
            outputs.Add(m.Slot,output);
        }
        var declared=instrument.Memberships.SelectMany(m=>m.Side.Terms.Quantity switch {VolumeQuantity v=>v.Sources.Select(s=>PpaQuantitiesStore.Source(data,s.Code).Asset),AccreditedQuantity a=>a.Assets,_=>[]}).ToHashSet();
        if(!declared.SetEquals(instrument.NormalizationAssets))throw Error("ppa_normalization","Normalization assets must equal the instrument's declared asset-derived sources.");
        foreach(var m in instrument.Memberships)
        {
            var output=m.Side.Terms.Quantity is SlotQuantity slots?slots.Slots.Select(s=>outputs[s]).Distinct().ToArray():[outputs[m.Slot]];
            if(output.Length!=1)throw Error("ppa_quantity_unit","Bound delivery slots require one exact quantity specification.");
            var price=m.Side switch {PpaFixedSide f=>f.Price.Unit,PpaFloatingSide f=>f.Price.Adder.Unit,_=>null};
            if(price is not null && price.Quantity!=output[0])throw Error("ppa_quantity_unit","The price denominator must match the bound quantity.");
            if(m.Side is PpaFloatingSide floating)
            {
                var code=floating.Price.Curve.Code;
                if(IntervalCurveDefinitions.IsShaped(data,code))code=data.Definitions["shaped_curve_blocks"].First(b=>b.Text("shaped_curve_code")==code && b.Text("anchor_role")=="period").Text("block_curve_code");
                var row=One(data.Definitions["curves"],code);var quote=PriceUnit.FromQuote(data.QuoteRows(row.Text("quote_code")).Single());
                if(quote!=price)throw Error("ppa_quantity_unit","Indexed price units must match the exact curve quote.");
            }
        }
    }
    internal static DeliveryCoverage Coverage(PpaInstrument instrument,PowerClock clock,string? from,string? to)
    {
        var span=new DeliveryCoverage(IntervalSeriesStore.ParseInstant(from??""),IntervalSeriesStore.ParseInstant(to??""));
        var term=new DeliveryCoverage(clock.StartOfDay(instrument.Delivery.From).ToUniversalTime(),clock.StartOfDay(instrument.Delivery.To).ToUniversalTime());
        if(!term.Contains(span.From,span.To))throw Error("ppa_segment_coverage","Coverage must remain inside the full instrument term.");
        DeliveryCoverage.ValidateForwardBoundary(span.From,term.From,clock,instrument.Delivery.IntervalMinutes);
        DeliveryCoverage.ValidateForwardBoundary(span.To,term.From,clock,instrument.Delivery.IntervalMinutes);
        if(instrument.Memberships.Any(m=>m.Side is CapacityDeliverySide) && (!MonthBoundary(clock,span.From) || !MonthBoundary(clock,span.To)))throw Error("ppa_segment_boundary","Capacity coverage requires whole operating months.");
        return span;
    }
    private static bool MonthBoundary(PowerClock clock,DateTimeOffset value)
    {var day=DateOnly.FromDateTime(value.UtcDateTime);return Enumerable.Range(-1,3).Select(day.AddDays).Any(d=>d.Day==1 && clock.StartOfDay(d).ToUniversalTime()==value);}
    internal const long MaximumPreparationCells=4_000_000;
    internal static void PreflightWork(IEnumerable<(PpaTermsInput Terms,string From,string To,string Identity)> bindings)
    {
        long cells=0;foreach(var b in bindings.DistinctBy(b=>(b.Identity,b.From,b.To)))
        {
            Size(b.Terms);var from=IntervalSeriesStore.ParseInstant(b.From);var to=IntervalSeriesStore.ParseInstant(b.To);
            if(from>=to)throw Error("ppa_segment_coverage","Coverage must be ordered.");
            // Preparation and evaluated schedule rows have separate budgets. Monthly
            // capacity never expands into hourly valuation rows; asset contributions
            // do not multiply the instrument's normalized output schedule.
            var capacity=b.Terms.Memberships.Any(m=>m.Side.Quantity.Kind=="accredited");
            var cellsPerAsset=capacity?(long)Math.Ceiling((to-from).TotalDays)+1:(long)Math.Ceiling((to-from).TotalMinutes/b.Terms.Delivery.IntervalMinutes);
            cells=checked(cells+cellsPerAsset*Math.Max(1,b.Terms.NormalizationAssets.Length));
            if(cells>MaximumPreparationCells)throw Error("ppa_limit","The complete request exceeds the explicit quantity-preparation cell budget.");
        }
    }
    internal static long Work(IEnumerable<(PpaInstrument Instrument,DeliveryCoverage Coverage)> bindings,ImmutableHashSet<DateOnly> detailMonths,Func<PpaInstrument,PowerClock> clock)
    {
        var selected=bindings.ToArray();long rows=0,detail=0;
        var counts=new Dictionary<(Guid,DeliveryCoverage),(long Rows,long Detail)>();
        foreach(var (i,c) in selected)
        {
            if(!counts.TryGetValue((i.Id,c),out var count))
            {
                var operatingClock=clock(i);long total=0,detailed=0;
                for(var month=i.Delivery.From;month<i.Delivery.To;month=month.AddMonths(1))
                {
                    var first=operatingClock.StartOfDay(month).ToUniversalTime();var last=operatingClock.StartOfDay(month.AddMonths(1)).ToUniversalTime();
                    var from=first>c.From?first:c.From;var to=last<c.To?last:c.To;if(from>=to)continue;
                    var native=(to-from).Ticks/TimeSpan.FromMinutes(i.Delivery.IntervalMinutes).Ticks;
                    var bySlot=new Dictionary<SideSlot,long>();
                    long Count(PpaMembership member)
                    {
                        if(bySlot.TryGetValue(member.Slot,out var result))return result;
                        result=member.Side.Terms.Quantity switch
                        {
                            AccreditedQuantity=>1,
                            VolumeQuantity=>native,
                            ProfileQuantity p when p.Hours.Membership==BlockMembership.All=>native,
                            ProfileQuantity p=>PowerHours.Compile(month,month.AddMonths(1),operatingClock,IntervalBlocks.Mask(p.Hours.Membership,i.Delivery.IntervalMinutes,p.Hours.Peak),
                                [..p.Hours.Holidays.Where(d=>d>=month && d<month.AddMonths(1))],i.Delivery.IntervalMinutes).LongCount(p=>p.Start>=from && p.End<=to),
                            SlotQuantity q=>Count(i.Memberships.Single(m=>m.Slot==q.Slots[0])),
                            _=>throw Error("ppa_quantity","Select a supported explicit quantity schedule.")
                        };
                        bySlot.Add(member.Slot,result);return result;
                    }
                    foreach(var member in i.Memberships){var n=Count(member);total=checked(total+n);if(detailMonths.Contains(month))detailed=checked(detailed+n);}
                }
                count=(total,detailed);counts.Add((i.Id,c),count);rows=checked(rows+total);
            }
            // Returned rows expand per binding even when its calculation reuses.
            detail=checked(detail+count.Detail);
            if(rows>MaximumRequestRows || detail>MaximumDetailRows)throw Error("ppa_limit","The complete request exceeds its evaluated-row or returned detail-row budget.");
        }
        return rows;
    }
    private static DeliveryHours Hours(PreviewData data,PpaHoursInput h,PpaDelivery d,IReadOnlyList<TouProductVersion> products)
    {
        var membership=PowerPreview.Membership(h.Membership);TouPeak? peak=null;
        if(membership==BlockMembership.All){if(h.TouProduct is not null || h.TouVersion is not null)throw Error("ppa_term","All hours has no TOU product.");}
        else
        {
            var p=products.SingleOrDefault(p=>p.Id==h.TouProduct && p.Version==h.TouVersion && p.State=="published")??throw Error("ppa_term","Select an exact published TOU version.");
            if(p.Clock!=new ClockReference(d.Clock.Revision.CatalogId,d.Clock.Revision.RevisionId,d.Clock.Code))throw Error("ppa_reference_revision","TOU and delivery clocks must retain the same revision.");peak=p.Peak;
        }
        return new(membership,peak,[..PowerPreview.Holidays(data,One(data.References["calendars"],h.Calendar),d.From,d.To)]);
    }
    private static PaymentRule Payment(PreviewData data,PpaPaymentInput p)
    {
        PaymentCalendar? calendar=null;
        if(p.Calendar is {} code)
        {
            var row=One(data.References["calendars"],code);var from=Day(row.Text("coverage_from"));var to=Day(row.Text("coverage_to"));
            if(to.DayNumber-from.DayNumber>PaymentRule.MaximumCalendarDays)throw Error("payment_calendar","Calendar coverage exceeds the supported bound.");
            var exceptions=data.References["calendar_days"].Where(c=>c.Text("calendar_code")==code).ToDictionary(c=>Day(c.Text("business_date")),c=>c.GetProperty("is_business_day").GetBoolean());
            var mask=int.Parse(row.Text("weekend_mask"),CultureInfo.InvariantCulture);
            calendar=new(new(data.ReferenceRevision,code),DateSet.Create(from,to,Enumerable.Range(from.DayNumber,to.DayNumber-from.DayNumber).Select(DateOnly.FromDayNumber).Where(d=>exceptions.TryGetValue(d,out var open)?open:(mask&(1<<(((int)d.DayOfWeek+6)%7)))==0)));
        }
        return new(SwapPaymentOptions.Parse(SwapPaymentOptions.Frequencies,p.Frequency),SwapPaymentOptions.Parse(SwapPaymentOptions.Rules,p.Rule),p.Days,p.DayOfMonth,
            p.DayBasis is null?null:SwapPaymentOptions.Parse(SwapPaymentOptions.DayBases,p.DayBasis),p.ShortMonth is null?null:SwapPaymentOptions.Parse(SwapPaymentOptions.ShortMonths,p.ShortMonth),SwapPaymentOptions.Parse(SwapPaymentOptions.Adjustments,p.Adjustment),calendar,p.Implementation);
    }
    private static FixedPriceTerm Fixed(PreviewData data,ReferenceKey currency,PpaFixedInput p)
    {
        if(p.Steps is null || p.Steps.Any(x=>x is null))throw Error("ppa_price_rule","Explicit strike steps are required.");
        var unit=new PriceUnit(currency,PpaQuantitiesStore.Quantity(data,p.Quantity).Key,Number(p.Multiplier));
        return p.Kind switch {
            "flat" when p.Price is not null && p.Rate is null && p.BaseDate is null && p.Basis is null && p.Steps.Length==0=>new FlatPrice(new(Number(p.Price),unit)),
            "escalated" when p.Price is not null && p.Rate is not null && p.BaseDate is not null && p.Basis=="anniversary_step_v1" && p.Steps.Length==0=>new EscalatedPrice(new(Number(p.Price),unit),Number(p.Rate),Day(p.BaseDate),ContractYearBasis.AnniversaryStep),
            "stepped" when p.Price is null && p.Rate is null && p.BaseDate is null && p.Basis is null && p.Steps.Length>0=>new SteppedPrice([..p.Steps.Select(s=>new PriceStep(Day(s.From),new(Number(s.Price),unit)))]),
            _=>throw Error("ppa_price_rule","Select one explicit supported fixed price variant.")};
    }
    private static IndexPrice Index(PreviewData data,ReferenceKey currency,PpaIndexInput p)
    {
        if(!data.Definitions["curves"].Any(r=>r.Text("code")==p.Curve) && !data.Definitions["shaped_curves"].Any(r=>r.Text("code")==p.Curve))throw Error("ppa_price_rule","Select a visible exact index curve.");
        return new(new(data.DefinitionRevision,p.Curve),Number(p.IndexMultiplier),new(Number(p.Adder),new PriceUnit(currency,PpaQuantitiesStore.Quantity(data,p.Quantity).Key,Number(p.Multiplier))));
    }
    private static System.Text.Json.JsonElement One(IEnumerable<System.Text.Json.JsonElement> rows,string code)=>rows.SingleOrDefault(r=>r.Text("code")==code) is var row && row.ValueKind!=System.Text.Json.JsonValueKind.Undefined?row:throw Error("ppa_term","Select a visible exact reference code.");
}
