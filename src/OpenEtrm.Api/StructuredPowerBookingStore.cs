using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed record StructuredPowerBookingPrepared(StructuredPowerBookRequest Request,StructuredPowerPreparedPlan Plan,string OriginalCommand)
{
    public StructuredPowerPreviewResult Preview=>Plan.Preview;
    internal RetainedScheduleTerms Retained=>new(Plan.Resolved.Resource.Asset.Revision,Plan.Resolved.Location.Revision,
        Plan.Instrument??throw new MarketDataException("operating_booking","A booked operating right requires explicit financial terms.")){OperatingQuantities=Plan.Quantities};
}

public sealed partial class StructuredPowerBookingStore(PreviewDatabase database)
{
    public const string Convention="supplied_operating_right_trade_v1";
    private Guid Catalog=>database.Manifest.CatalogId;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    private static MarketDataException Invalid(string message)=>new("operating_booking",message);

    public async Task<StructuredPowerBookingPrepared> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,
        StructuredPowerFacilityVersion facility,DateTimeOffset knownAt,StructuredPowerBookRequest request,CancellationToken ct)
    {
        if(request is null||request.OperationId==Guid.Empty||request.BookId==Guid.Empty||request.Convention!=Convention||
            request.TradeId is not null||request.ExpectedRevision!=0||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>500||
            request.Plan is null||(request.Plan.Valuation is null&&request.Plan.Composition is null))
            throw Invalid("Supply a new complete operating-right booking, explicit book, parties, reason and financial terms; amendments are not yet qualified.");
        // Booking date is the ordinary server UTC booking fact, independent of
        // the selected historical market's valuation instant.
        await using var bookingDate=ScheduleStore.Command(connection,"SELECT (clock_timestamp() AT TIME ZONE 'UTC')::date::text");
        var date=(string)(await bookingDate.ExecuteScalarAsync(ct))!;
        await TradeParties.Validate(connection,Catalog,TradeParties.Payload(date,request.LegalEntityId,request.LegalEntityRevision,
            request.CounterpartyId,request.CounterpartyRevision),frame.At,ct);
        await using(var book=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadTrades') AND security.trading_access($1,'BookTrade') AND EXISTS(SELECT 1 FROM trading.books WHERE(catalog_id,id)=($1,$2))",Catalog,request.BookId))
            if(!Equals(await book.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        var canonical=CanonicalPlan(request.Plan);
        var identity=new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{
            Catalog,facility.Id,facility.Version,Plan=canonical},Json))).AsSpan(0,16));
        var prepared=StructuredPowerPreview.Prepare(data,frame,facility,knownAt,request.Plan,identity);
        var clock=data.References["clock_rules"].Single(row=>row.Text("code")==prepared.Resolved.Clock.Code);
        if(clock.Text("kind")!="fixed_offset"||clock.Number("fixed_offset_seconds")!=default)
            throw new MarketDataException("operating_booking_clock","Saved operating-right booking for this clock requires separately qualified native grid admission.");
        if(prepared.Grid[0].Start<=frame.At)throw new MarketDataException("operating_delivery_lifecycle","Only an unstarted complete supplied-plan horizon can be booked.");
        return new(request,prepared,JsonSerializer.Serialize(request,Json));
    }

    // Economic identity excludes calculation coordinates and market levels. All
    // relationship selections remain exact; equivalent decimal/UTC spellings
    // have one canonical business value.
    internal static StructuredPowerPreviewRequest CanonicalPlan(StructuredPowerPreviewRequest request)
    {
        var node=JsonSerializer.SerializeToNode(request with{SnapshotId=Guid.Empty},Json)!;
        void Normalize(JsonNode? value)
        {
            if(value is JsonObject obj)foreach(var entry in obj.ToArray())
            {
                if(entry.Value is JsonValue scalar&&scalar.TryGetValue<string>(out var text))
                {
                    if(System.Text.RegularExpressions.Regex.IsMatch(text,@"^-?[0-9]+(\.[0-9]+)?$"))
                        obj[entry.Key]=text.Contains('.')?ExactDecimal.Parse(text).ToString():System.Numerics.BigInteger.Parse(text,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    else if(text.Contains('T')&&DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var at)&&StructuredPowerPreview.Utc(at))
                        obj[entry.Key]=at.ToString("O",CultureInfo.InvariantCulture);
                }
                else Normalize(entry.Value);
            }
            else if(value is JsonArray array)foreach(var child in array)Normalize(child);
        }
        Normalize(node);
        if(node["solarRight"] is JsonObject solar)
        {
            var n=System.Numerics.BigInteger.Parse(solar["entitlementNumerator"]!.GetValue<string>(),CultureInfo.InvariantCulture);
            var d=System.Numerics.BigInteger.Parse(solar["entitlementDenominator"]!.GetValue<string>(),CultureInfo.InvariantCulture);
            var gcd=System.Numerics.BigInteger.GreatestCommonDivisor(n,d);
            if(gcd>0){solar["entitlementNumerator"]=(n/gcd).ToString(CultureInfo.InvariantCulture);solar["entitlementDenominator"]=(d/gcd).ToString(CultureInfo.InvariantCulture);}
        }
        return node.Deserialize<StructuredPowerPreviewRequest>(Json)!;
    }
}

public sealed record StructuredPowerTradeDirectory(ImmutableArray<StructuredPowerBookedTrade> Trades,int Total,int? NextOffset);

public sealed partial class StructuredPowerBookingStore
{
    public async Task<StructuredPowerBookingReceipt?> OperationStatus(NpgsqlConnection connection,Guid operation,CancellationToken ct)
    {
        if(operation==Guid.Empty)throw Invalid("Supply an exact operation identity.");
        await using var query=ScheduleStore.Command(connection,"SELECT security.operating_booking_status($1,$2)::text",Catalog,operation);
        return await query.ExecuteScalarAsync(ct) is string value?JsonSerializer.Deserialize<StructuredPowerBookingReceipt>(value,Json):null;
    }
    public async Task<StructuredPowerBookingReceipt?> RequestStatus(NpgsqlConnection connection,StructuredPowerBookRequest request,CancellationToken ct)
    {
        await using var query=ScheduleStore.Command(connection,"SELECT security.operating_booking_replay($1,$2::jsonb)::text",Catalog,JsonSerializer.Serialize(request,Json));
        return await query.ExecuteScalarAsync(ct) is string value?JsonSerializer.Deserialize<StructuredPowerBookingReceipt>(value,Json):null;
    }
    public async Task<StructuredPowerBookingReceipt> Save(NpgsqlConnection connection,InputFrame frame,StructuredPowerBookRequest request,
        StructuredPowerBookingPrepared prepared,CancellationToken ct)
    {
        if(JsonSerializer.Serialize(request,Json)!=prepared.OriginalCommand||prepared.Plan.Instrument is not {} instrument)
            throw Invalid("Save the exact reviewed operating-right command.");
        var members=instrument.Sides.Select((member,slot)=>new{slot,name=prepared.Plan.SideNames[member.Side.Id]});
        await using var query=ScheduleStore.Command(connection,"SELECT security.book_operating_right($1,$2::jsonb,$3::jsonb,$4)::text",Catalog,
            JsonSerializer.Serialize(request,Json),JsonSerializer.Serialize(new{instrumentId=instrument.Id,members},Json),frame.At);
        return JsonSerializer.Deserialize<StructuredPowerBookingReceipt>((string)(await query.ExecuteScalarAsync(ct))!,Json)!;
    }
    public async Task<StructuredPowerBookedTrade> ReadExact(NpgsqlConnection connection,InputFrame frame,Guid trade,CancellationToken ct)
    {
        if(frame.ContextId is null||trade==Guid.Empty)throw Invalid("Read an exact saved trade under a certified context.");
        var positions=await OperatingPositions.Read(connection,database.Manifest,[],[],ct,trade:trade);
        if(positions.Length!=1)throw TradePositionSelection.Unavailable();
        return await Project(connection,frame,positions[0],ct);
    }
    internal async Task<StructuredPowerBookedTrade> ReadSelected(NpgsqlConnection connection,TradingView view,TradePositionSelection selected,CancellationToken ct)
    {
        if(selected.InstrumentType!=OperatingPositions.Type)throw TradePositionSelection.Unavailable();
        var position=RequireSelectedPosition(await OperatingPositions.Read(connection,database.Manifest,[],[],ct,position:selected));
        return(await Project(connection,view.CalculationFrame,position,ct)) with{RetainedSource=RetainedSideSource.From(view.CalculationFrame,view.Context)};
    }
    internal static OperatingPosition RequireSelectedPosition(ImmutableArray<OperatingPosition> positions)=>positions.Length==1?positions[0]:throw TradePositionSelection.Unavailable();
    public async Task<StructuredPowerTradeDirectory> Directory(NpgsqlConnection connection,InputFrame frame,ImmutableArray<Guid> books,int offset,int limit,CancellationToken ct)
    {
        if(frame.ContextId is null||books.IsDefault||offset<0||limit is <1 or >100)throw Invalid("Select an explicit certified directory scope and bounded page.");
        var positions=await OperatingPositions.Read(connection,database.Manifest,books,[],ct);
        var result=ImmutableArray.CreateBuilder<StructuredPowerBookedTrade>();
        foreach(var p in positions.Skip(offset).Take(limit))result.Add(await Project(connection,frame,p,ct));
        return new(result.ToImmutable(),positions.Length,offset+result.Count<positions.Length?offset+result.Count:null);
    }
    private async Task<StructuredPowerBookedTrade> Project(NpgsqlConnection connection,InputFrame frame,OperatingPosition p,CancellationToken ct)
    {
        var programs=await new RetainedScheduleStore(database).LoadPrograms(connection,p.Binding,frame.ContextId,frame.At,ct);
        var members=p.Head.GetProperty("members").EnumerateArray().ToArray();
        if(programs.Length!=members.Length)throw Invalid("Every saved operating membership requires its retained schedule.");
        var sides=programs.OrderBy(x=>x.Slot).Select(x=>
        {
            var m=members.Single(m=>int.Parse(m.Text("slot"),CultureInfo.InvariantCulture)==x.Slot);
            if(m.Id("sideId")!=x.SideId)throw Invalid("A retained schedule must belong to its saved membership.");
            return new StructuredPowerBookedSide(x.Slot,x.SideId,x.ScheduleId,m.Text("name"),m.Text("coefficient"),x.Program.Document.ScheduleManifest);
        }).ToImmutableArray();
        return new(p.Trade,int.Parse(p.Head.Text("revision"),CultureInfo.InvariantCulture),p.Binding,p.Binding,p.Book.Id("id"),
            p.Head.Id("facility_id"),int.Parse(p.Head.Text("facility_version"),CultureInfo.InvariantCulture),p.Head.Text("effective_at"),
            TradingReference.Trade(p.Head),p.Physical.Text("kind"),p.Physical.Text("facility_code"),p.Physical.Text("facility_name"),p.Book.Text("name"),
            p.Head.GetProperty("request").Deserialize<StructuredPowerBookRequest>(Json)!,sides)
            {SideGroups=[RetainedInstrumentSidePresentation.Operating(p,programs)]};
    }
    public async Task<StructuredPowerPreviewResult> Value(NpgsqlConnection connection,InputFrame frame,Guid trade,CancellationToken ct)
    {
        var positions=await OperatingPositions.Read(connection,database.Manifest,[],[],ct,trade:trade);
        if(positions.Length!=1)throw TradePositionSelection.Unavailable();var p=positions[0];
        var source=await MarketInputSelection.Resolve(database,connection,frame,new(p.Definition,p.Reference.CatalogId,p.Reference.RevisionId),[],false,ct,referenceScope:ReferenceReadScope.AllVisible);
        var context=PinnedContext.Exact((await new KnowledgeResolver(Catalog).Read(connection,frame.ContextId!.Value,ct)).Context);
        var knownAt=SchedulePreview.ParseAt(context.KnownAt);
        var facility=await new PhysicalOperatingFacilityStore(Catalog).ReadExact(connection,p.Head.Id("facility_id"),int.Parse(p.Head.Text("facility_version"),CultureInfo.InvariantCulture),
            frame.ContextId.Value,frame.At,frame.Time.Date,ct);
        var request=p.Terms(source.Frame.Id).Plan;
        var physical=StructuredPowerPreview.Calculate(source.Data,source.Frame,facility,knownAt,PhysicalPlan(request));
        if(physical.Intervals[0].Interval.Start<=frame.At)throw new MarketDataException("operating_delivery_lifecycle","A started operating right requires a separately qualified operational lifecycle.");
        var result=await EvaluatePrograms(connection,source.Data,source.Frame,p,ct);
        return physical with{PreviewId=p.Binding,Valuation=result,
            Limitations=["Saved hypothetical supplied-plan quantities are retained assumptions; no dispatch instruction, actual production or certificate issuance.",
                "This complete exclusive-right profile values future plans only; operational actuals and cash lifecycle require separate admission."]};
    }
    internal static StructuredPowerPreviewRequest PhysicalPlan(StructuredPowerPreviewRequest request)=>request with{Valuation=null,Composition=null};

    internal static ImmutableArray<RetainedSideProgram> SelectPrograms(ImmutableArray<RetainedSideProgram> programs,int? selectedSlot)
    {
        if(programs.IsDefaultOrEmpty)throw new MarketDataException("side_schedule_inspection","Retain the operating instrument's owned Side programs.");
        if(selectedSlot is null)return programs;
        var selected=programs.Where(program=>program.Slot==selectedSlot.Value).Take(2).ToArray();
        if(selected.Length!=1)throw new MarketDataException("side_schedule_inspection","Select one exact owned operating Side slot.");
        return [selected[0]];
    }

    internal async Task<StructuredPowerValuationResult> EvaluatePrograms(NpgsqlConnection connection,PreviewData data,InputFrame frame,OperatingPosition position,
        CancellationToken ct,Action<int,SideViewInput>? inspect=null,int? selectedSlot=null)
    {
        if(TradingInstant.Parse(position.From,"operating_delivery_lifecycle")<=frame.At)
            throw new MarketDataException("operating_delivery_lifecycle","A started complete operating right requires a separately qualified operational lifecycle.");
        var programs=SelectPrograms(await new RetainedScheduleStore(database).LoadPrograms(connection,position.Binding,frame.ContextId,frame.At,ct),selectedSlot);
        var members=position.Head.GetProperty("members").EnumerateArray().ToArray();
        var context=(await new KnowledgeResolver(Catalog).Read(connection,frame.ContextId!.Value,ct)).Context;
        var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),frame.ContextId.Value,SchedulePreview.ParseAt(context.KnownAt));
        var dependencies=programs.SelectMany(p=>p.Program.Document.Instructions).SelectMany(i=>i.Bindings).Where(b=>b.Kind=="contract_price").Select(b=>b.Market!.Value).Distinct().ToImmutableArray();
        var market=data.Model.Build(snapshot,dependencies,ct);
        var discounting=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var sideContext=SideValuationContext.ForRetainedPrograms(market,market,discounting,ct);
        var sides=ImmutableArray.CreateBuilder<StructuredPowerSideResult>();var total=ExactMoney.FromMoney(new(default,position.Currency));
        var plan=position.Terms(frame.Id).Plan;
        foreach(var program in programs.OrderBy(p=>p.Slot))
        {
            var member=members.Single(m=>int.Parse(m.Text("slot"),CultureInfo.InvariantCulture)==program.Slot);
            var sign=ExactDecimal.Parse(member.Text("coefficient"));
            if(member.Id("sideId")!=program.SideId)throw Invalid("A retained program must belong to its exact Side membership.");
            var concrete=RetainedProgramSide.ForOperating(program.SideId,program.Program,new(ExactDecimal.Parse("1"),position.Quantity),position.Currency);
            var value=concrete.PV(sideContext).Execution;
            inspect?.Invoke(program.Slot,new(member.Text("name"),value,program.Program,ExactDecimal.Parse("1"),new(sign.Power(1).Numerator,sign.Power(1).Denominator),[]));
            var rows=ImmutableArray.CreateBuilder<StructuredPowerSideRow>();
            foreach(var guard in program.Program.Document.Instructions.Where(i=>i.Output.Operation=="output_guard"))
            {
                var amount=program.Program.Document.Instructions[guard.Inputs["amount"][0]];
                var q=amount.Inputs["quantity"][0];var price=amount.Inputs["price"][0];
                var qr=value.Values[q].Scenario;var pr=value.Values[price].Scenario;var ar=value.Values[amount.Output.Ordinal].Scenario;var pv=value.Values[guard.Output.Ordinal].Scenario;
                var quantity=qr.Values[0].Numerator.IsZero?ExactQuantity.FromQuantity(new(default,qr.Unit!.Value)):
                    ExactQuantity.FromQuantity(new(ExactDecimal.Parse("1"),qr.Unit!.Value)).Scale(new OpenEtrm.Shared.Numerics.ExactRatio(qr.Values[0].Numerator,qr.Values[0].Denominator));
                var unit=new PriceUnit(pr.Currency!.Value,pr.Unit!.Value,ExactDecimal.Parse(pr.QuoteMultiplier!));
                rows.Add(new(guard.Output.Interval!,guard.Output.ValueAt!.Value,quantity,ExactUnitPrice.Parse(pr.Values[0].Numerator+"/"+pr.Values[0].Denominator,unit),
                    ExactMoney.Parse(ar.Values[0].Numerator+"/"+ar.Values[0].Denominator,position.Currency),ExactMoney.Parse(pv.Values[0].Numerator+"/"+pv.Values[0].Denominator,position.Currency),
                    (plan.Composition is {} composed?composed.Sides[program.Slot].QuantitySource=="horizon_right":member.Text("name")=="access_fee")?
                        OpenEtrm.MarketData.Power.VolumeState.Contractual:OpenEtrm.MarketData.Power.VolumeState.Forecast,null,null){PriceUnitLabel=data.UnitLabel(unit)});
            }
            var financial=plan.Valuation;
            var quoteCode=plan.Composition is {} composition?composition.Sides[program.Slot].Price.QuoteCode:member.Text("name") switch{
                "fuel_input"=>financial!.FuelPrice!.QuoteCode,"charging_energy"=>financial!.ChargingPrice!.QuoteCode,
                "access_fee"=>financial!.AccessQuoteCode!,_=>financial!.EnergyPrice.QuoteCode};
            var quote=data.QuoteRows(quoteCode).Single().Key;
            var contribution=value.PresentValue.Scale(sign);total=total.Add(contribution);
            sides.Add(new(program.SideId,member.Text("name"),sign.ToString(),quote,value.PresentValue,contribution,rows.ToImmutable()));
        }
        return new(StructuredPowerPreview.RemainingValueMeasure,StructuredPowerPreview.RightHolderPerspective,position.Currency,
            new(ExactDecimal.Parse("1"),position.Quantity),total,sides.ToImmutable(),BookedSideEvaluation.NumericalPolicy);
    }
}
