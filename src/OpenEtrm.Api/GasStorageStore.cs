using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStorageRatchetInput([property:JsonRequired]string Lower,[property:JsonRequired]string Upper,
    [property:JsonRequired]string MaximumDailyInjection,[property:JsonRequired]string MaximumDailyWithdrawal);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStorageTermsInput([property:JsonRequired]Guid DefinitionRevisionId,[property:JsonRequired]Guid ReferenceCatalogId,
    [property:JsonRequired]Guid ReferenceRevisionId,[property:JsonRequired]string Quantity,[property:JsonRequired]string InjectionCurve,[property:JsonRequired]string WithdrawalCurve,
    [property:JsonRequired]string MinimumInventory,[property:JsonRequired]string MaximumInventory,[property:JsonRequired]ImmutableArray<GasStorageRatchetInput> Ratchets,
    [property:JsonRequired]string InjectionFuel,[property:JsonRequired]string WithdrawalFuel,[property:JsonRequired]string RatchetBasis,[property:JsonRequired]string FlowPolicy,
    [property:JsonRequired]string FuelBasis,[property:JsonRequired]string OpeningInventory,[property:JsonRequired]string TerminalMinimum,[property:JsonRequired]string TerminalMaximum,
    [property:JsonRequired]string InjectionCharge,[property:JsonRequired]string WithdrawalCharge,[property:JsonRequired]string MonthlyCapacityCharge,
    [property:JsonRequired]string TermFrom,[property:JsonRequired]string TermTo,[property:JsonRequired]string Clock,[property:JsonRequired]int DayStartMinute,
    [property:JsonRequired]PpaPaymentInput PaymentRule);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStorageBookRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid OperationId,[property:JsonRequired]Guid BookId,
    [property:JsonRequired]Guid LegalEntityId,[property:JsonRequired]int LegalEntityRevision,[property:JsonRequired]Guid CounterpartyId,[property:JsonRequired]int CounterpartyRevision,
    [property:JsonRequired]string Reason,[property:JsonRequired]GasStorageTermsInput Terms);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStorageStatusRequest([property:JsonRequired]Guid OperationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStorageReadRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid TradeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStoragePlanDayInput([property:JsonRequired]string GasDay,[property:JsonRequired]string Injection,[property:JsonRequired]string Withdrawal);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStoragePlanSourceInput([property:JsonRequired]string Kind,[property:JsonRequired]string? InventoryStep);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStoragePlanRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid OperationId,[property:JsonRequired]Guid TradeId,
    [property:JsonRequired]int ExpectedRevision,[property:JsonRequired]string Reason,[property:JsonRequired]GasStoragePlanSourceInput Source,
    [property:JsonRequired]ImmutableArray<GasStoragePlanDayInput> Days);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasStorageProposeRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid TradeId,[property:JsonRequired]string InventoryStep);
public sealed record GasStorageReceipt(Guid OperationId,Guid TradeId,int Revision,Guid InstrumentId,bool Changed);
public sealed record GasStoragePlanReceipt(Guid OperationId,Guid TradeId,Guid PlanId,int Revision,bool Changed);
public sealed record GasStoragePreparedDay(string GasDay,string StartsAt,string EndsAt,string PaymentAt,Guid InjectionContractId,Guid WithdrawalContractId);
public sealed record GasStoragePrepared(Guid InstrumentId,ImmutableArray<GasStoragePreparedDay> Days);

// Booking resolves the contract's reference terms once, expands its gas days with their curve contracts and payment
// instants, and hands both to the native operation. A plan is admitted by the managed entitlement before it is retained.
public sealed class GasStorageStore(PreviewDatabase database)
{
    public const string Convention="gas_storage_trade_v1",PlanConvention="gas_storage_plan_v1";
    private Guid Catalog=>database.Manifest.CatalogId;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static MarketDataException Invalid(string message)=>new("gas_storage_terms",message);
    // One sentence a trader can act on for each native refusal; an unlisted code keeps the general wording.
    internal static string Refusal(string code)=>code switch{
        "gas_storage_denied"=>"This account may not book or plan gas storage contracts here.",
        "gas_storage_request_conflict"=>"This request identity was already used for a different command. Recover its outcome or send a new request.",
        "gas_storage_policy"=>"Select the supported ratchet, flow and fuel policy versions.",
        "gas_storage_terms"=>"Supply a coherent inventory range, opening and terminal inventory inside it, fuel fractions below one, nonnegative charges and two outright curves priced in the contract's quantity.",
        "gas_storage_ratchets"=>"Ratchet bands must cover the contracted inventory range in order, with no gap or overlap and nonnegative daily limits.",
        "gas_storage_day"=>"The term must start after the booking instant and its gas days must be whole and contiguous.",
        "gas_storage_contract"=>"Each gas day needs exactly one contract delivering it on the injection curve and on the withdrawal curve.",
        "gas_storage_source"=>"The selected market definition or reference revision is not available to this account.",
        "gas_storage_book"=>"Select a book that existed at the booking instant.",
        "gas_storage_unavailable"=>"The storage contract is not available under the selected context.",
        "gas_storage_plan"=>"A plan states every gas day of the term once, in order, flowing one way each day.",
        "gas_storage_plan_stale"=>"The plan changed since it was read. Reload it and record again.",
        "gas_storage_plan_time"=>"A plan cannot take effect before the one it replaces.",
        "gas_storage_started"=>"The term has started: its flows are measured, not planned, and this profile does not own them.",
        _=>"Supply the complete, exact gas storage command."};
    private static void Context(FuturesReadRequest context)
    {
        if(context is null||context.SnapshotId==Guid.Empty||context.Convention!=Convention||context.KnowledgeMode!="current"||context.ContextId is not null)
            throw new MarketDataException("historical_read_only","Select the working financial snapshot under the gas storage convention.");
    }
    private static string Command(GasStorageBookRequest r)=>JsonSerializer.Serialize(new{r.OperationId,convention=Convention,r.BookId,r.LegalEntityId,r.LegalEntityRevision,
        r.CounterpartyId,r.CounterpartyRevision,r.Reason,r.Terms},Json);
    private static string Command(GasStoragePlanRequest r)=>JsonSerializer.Serialize(new{r.OperationId,convention=PlanConvention,r.TradeId,r.ExpectedRevision,r.Reason,r.Source,r.Days},Json);
    private static ExactDecimal Number(string text)
    {try{return ExactDecimal.Parse(text);}catch(Exception e)when(e is FormatException or OverflowException or ArgumentException or MarketDataException){throw Invalid("Supply exact decimal terms.");}}
    private static string Day(DateOnly day)=>day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static string Instant(DateTimeOffset at)=>at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture);

    public GasStoragePrepared Prepare(PreviewData data,InputFrame frame,GasStorageBookRequest request)
    {
        Context(request.Context);var t=request.Terms;
        if(request.OperationId==Guid.Empty||request.BookId==Guid.Empty||t?.PaymentRule is null||t.Ratchets.IsDefaultOrEmpty||t.Ratchets.Any(b=>b is null))throw Invalid("Supply the complete storage contract command.");
        if(t.DefinitionRevisionId!=data.DefinitionRevision.RevisionId||new ReferenceRevision(t.ReferenceCatalogId,t.ReferenceRevisionId)!=data.ReferenceRevision)
            throw Invalid("Book against the selected snapshot's exact market definition and reference revision.");
        var from=DateWorkspace.Date(t.TermFrom);var to=DateWorkspace.Date(t.TermTo);
        if(from>=to||to.DayNumber-from.DayNumber>GasStoragePlans.MaximumDays)throw Invalid("The term must end after it starts and stay within the supported length.");
        var unit=new ReferenceKey(data.ReferenceRevision,t.Quantity);
        Quantity Amount(string text)=>new(Number(text),unit);
        // The same terms the native operation retains must already form a valid managed entitlement, quote and payment rule.
        GasStoragePlans.ValidateEntitlement(new(data.DefinitionRevision,"booking",unit,Amount(t.MinimumInventory),Amount(t.MaximumInventory),
            [..t.Ratchets.Select(b=>new GasStorageRatchet(Amount(b.Lower),Amount(b.Upper),Amount(b.MaximumDailyInjection),Amount(b.MaximumDailyWithdrawal)))],
            Number(t.InjectionFuel),Number(t.WithdrawalFuel),t.RatchetBasis,t.FlowPolicy,t.FuelBasis));
        QuoteSpecification Quote(string curve)
        {
            var definitions=data.DefinitionRows("curves","code",curve).ToArray();
            return definitions.Length==1&&data.QuoteRows(definitions[0].Text("quote_code")).ToArray() is [var quote]?quote:throw Invalid("Select one exact injection curve and one exact withdrawal curve.");
        }
        var quote=Quote(t.InjectionCurve);
        if(Quote(t.WithdrawalCurve)!=quote||quote.Quantity!=unit)throw Invalid("Both curves must be priced under one quote in the contract's quantity.");
        foreach(var charge in new[]{t.InjectionCharge,t.WithdrawalCharge,t.MonthlyCapacityCharge})_=new UnitPrice(Number(charge),quote);
        var payment=StructuredPowerPreview.OperatingPayment(t.PaymentRule);
        var clock=new GasDayClock(new(data.ReferenceRevision,t.Clock),PpaQuantitiesStore.FixedClockRules(data,t.Clock),t.DayStartMinute);
        ImmutableArray<MarketContract> Contracts(string curve)=>
            [..data.Requests(frame,curve).Select(r=>data.ContractRows(r.Contract.Id).ToArray() is [var contract]?contract:throw Invalid("Every curve contract needs one exact definition."))];
        var injection=Contracts(t.InjectionCurve);var withdrawal=Contracts(t.WithdrawalCurve);
        Guid Delivering(ImmutableArray<MarketContract> contracts,DateOnly day)=>contracts.Where(c=>c.DeliveryFrom<=day&&day<c.DeliveryTo).ToArray() is [var one]?one.Key.Id:
            throw new MarketDataException("gas_storage_contract","Each gas day needs exactly one contract delivering it on the injection curve and on the withdrawal curve.");
        var days=ImmutableArray.CreateBuilder<GasStoragePreparedDay>();
        for(var day=from;day<to;day=day.AddDays(1))
        {
            var month=new DateOnly(day.Year,day.Month,1);
            days.Add(new(Day(day),Instant(clock.Boundary(day)),Instant(clock.Boundary(day.AddDays(1))),Instant(GasStorageInstrument.PaymentAt(payment,month,month.AddMonths(1))),
                Delivering(injection,day),Delivering(withdrawal,day)));
        }
        return new(OperationIdentity.For("gas_storage_instrument_v1",request.OperationId),days.ToImmutable());
    }
    public Task<GasStorageReceipt?> Replay(NpgsqlConnection c,GasStorageBookRequest request,CancellationToken ct)=>
        Receipt<GasStorageReceipt>(c,"SELECT security.gas_storage_request_replay($1,$2::jsonb)::text",ct,Catalog,Command(request));
    public Task<JsonElement?> Status(NpgsqlConnection c,GasStorageStatusRequest request,CancellationToken ct)=>
        Receipt<JsonElement?>(c,"SELECT security.gas_storage_request_status($1,$2)::text",ct,Catalog,request.OperationId);
    public async Task<GasStorageReceipt> Save(NpgsqlConnection c,InputFrame frame,GasStorageBookRequest request,GasStoragePrepared prepared,CancellationToken ct)=>
        await Receipt<GasStorageReceipt>(c,"SELECT security.book_gas_storage($1,$2::jsonb,$3::jsonb,$4)::text",ct,Catalog,Command(request),JsonSerializer.Serialize(prepared,Json),frame.At)
            ??throw Invalid("The booking returned no outcome.");
    private static async Task<T?> Receipt<T>(NpgsqlConnection c,string sql,CancellationToken ct,params object[] values)
    {await using var query=ScheduleStore.Command(c,sql,values);return await query.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<T>(text,Json):default;}

    private async Task<GasStoragePosition> Position(NpgsqlConnection c,Guid trade,CancellationToken ct)=>
        await GasStoragePositions.Read(c,database.Manifest,[],[],ct,trades:[trade]) is [var position]?position:
            throw new MarketDataException("gas_storage_unavailable","The storage contract is not available under the selected context.");
    private static readonly AmountFormat SixPlaces=new(6,CashTieRule.ToEven,false,true);
    private static string Shown(ExactQuantity value)=>value.ToQuantity(SixPlaces).Amount.ToString();
    // What a plan does to the contract day by day: the band each day opens in, its limits, and inventory before and after.
    private static object Path(GasStorageContract contract,GasStoragePlanResult plan)=>plan.Days.Select(d=>
    {
        var band=contract.Entitlement.Ratchets[d.Band];
        // Inventory with fuel in kind can be a fraction no decimal ends: the exact value is returned beside a decimal to read and draw.
        return new{gasDay=Day(d.Day.GasDay.OperatingDate),injection=d.Day.Injection.Amount.ToString(),withdrawal=d.Day.Withdrawal.Amount.ToString(),band=d.Band,
            maximumDailyInjection=band.MaximumDailyInjection.Amount.ToString(),maximumDailyWithdrawal=band.MaximumDailyWithdrawal.Amount.ToString(),
            opening=Shown(d.Opening),closing=Shown(d.Closing),injectionFuel=Shown(d.InjectionFuel),withdrawalFuel=Shown(d.WithdrawalFuel),
            openingExact=d.Opening.ToExactString(),closingExact=d.Closing.ToExactString()};
    }).ToArray();

    // The booked contract as the blotter lists it, with its ratchets and gas days and, when a plan is in force,
    // that plan's inventory path: valued, or with the reason it cannot be.
    public async Task<object> Read(NpgsqlConnection c,PreviewData data,InputFrame frame,Guid trade,CancellationToken ct)
    {
        var position=await Position(c,trade,ct);
        var run=new GasStorageBlotter.ContractRun(c,database,frame,[position]);
        await foreach(var unit in run.Units(ct))
        {
            object? path=null;string? planRefusal=null;
            if(position.Planned)
                try
                {
                    var contract=GasStoragePortfolio.Contract(data,position);
                    path=Path(contract,GasStoragePortfolio.Instrument(data,position).Plan);
                }
                catch(MarketDataException error)when(GasStoragePortfolio.Unpriced(error)||error.Code=="gas_storage_market_context"){planRefusal=error.Code;}
            return new{position=unit[0],terms=position.Terms,ratchets=position.Ratchets,days=position.Days,
                plan=position.Planned?position.Plan:(JsonElement?)null,path,planRefusal};
        }
        throw new MarketDataException("gas_storage_unavailable","The storage contract is not available under the selected context.");
    }
    // A plan is admitted by the contract's entitlement before it is recorded; an inadmissible plan records nothing.
    public async Task<GasStoragePlanReceipt?> PreparePlan(NpgsqlConnection c,PreviewData data,GasStoragePlanRequest request,CancellationToken ct)
    {
        Context(request.Context);
        if(request.OperationId==Guid.Empty||request.TradeId==Guid.Empty||request.Source is null||request.Days.IsDefaultOrEmpty)throw Invalid("Supply the complete plan command.");
        if(await Receipt<GasStoragePlanReceipt>(c,"SELECT security.gas_storage_request_replay($1,$2::jsonb)::text",ct,Catalog,Command(request)) is {} replayed)return replayed;
        var contract=GasStoragePortfolio.Contract(data,await Position(c,request.TradeId,ct));
        if(request.Days.Length!=contract.Days.Length||request.Days.Where((d,i)=>d?.Injection is null||d.Withdrawal is null||d.GasDay!=Day(contract.Days[i].GasDay.OperatingDate)).Any())
            throw new MarketDataException("gas_storage_plan","A plan states every gas day of the term once, in order.");
        var plan=GasStoragePortfolio.Plan(contract,request.Days.Select(d=>(d.Injection,d.Withdrawal)));
        GasStoragePlans.Evaluate(contract.Entitlement,contract.Opening,contract.Terminal,plan,GasStoragePlans.Implementation);
        return null;
    }
    public async Task<GasStoragePlanReceipt> SavePlan(NpgsqlConnection c,InputFrame frame,GasStoragePlanRequest request,CancellationToken ct)=>
        await Receipt<GasStoragePlanReceipt>(c,"SELECT security.record_gas_storage_plan($1,$2::jsonb,$3)::text",ct,Catalog,Command(request),frame.At)
            ??throw Invalid("The plan returned no outcome.");

    // The plan worth most on the stated inventory lattice at this snapshot's forwards, net of the contract's commodity
    // charges. It is returned for the desk to review and record; it changes no valuation by itself.
    public async Task<object> Propose(NpgsqlConnection c,PreviewData data,InputFrame frame,GasStorageProposeRequest request,CancellationToken ct)
    {
        var position=await Position(c,request.TradeId,ct);var contract=GasStoragePortfolio.Contract(data,position);
        if(contract.Opening.At<=frame.At)throw new MarketDataException("gas_storage_started","A started storage term needs recorded flows and inventory, which this profile does not own.");
        var build=data.Build(frame,[..new[]{position.InjectionCurve,position.WithdrawalCurve}.Distinct()]);
        var forwards=build.Values.ToDictionary(v=>v.Request,v=>v.Price);
        UnitPrice Forward(CurveRequest request)=>forwards.TryGetValue(request,out var price)&&price.Unit==PriceUnit.FromQuote(contract.Quote)?price:
            throw new MarketDataException("gas_storage_price","Every gas day needs its forward price in the contract's quote at this snapshot.");
        var prices=contract.Days.Select(day=>new GasStorageDayPrice(day.GasDay,Forward(day.Injection).Value+contract.Charges.Injection.Value,
            Forward(day.Withdrawal).Value-contract.Charges.Withdrawal.Value)).ToImmutableArray();
        var step=new Quantity(Number(request.InventoryStep),contract.Entitlement.Quantity);
        var proposal=GasStoragePlanProposals.Propose(contract.Entitlement,contract.Opening,contract.Terminal,prices,step,GasStoragePlanProposals.Implementation);
        return new{tradeId=request.TradeId,implementation=proposal.Implementation,inventoryStep=step.Amount.ToString(),
            objective=proposal.ObjectiveText,objectiveBasis="forward_net_of_commodity_charges_undiscounted_v1",
            currency=position.Terms.Text("currency"),path=Path(contract,proposal.Plan),
            prices=prices.Select(p=>new{gasDay=Day(p.GasDay.OperatingDate),injectionCost=p.InjectionCost.ToString(),withdrawalValue=p.WithdrawalValue.ToString()}).ToArray()};
    }

    // What the entry ticket chooses from: every listed value is one the booking accepts as stated, and none is chosen for the person.
    public async Task<object> Choices(NpgsqlConnection c,PreviewData data,InputFrame frame,CancellationToken ct)
    {
        var directory=await FuturesStore.ReadBookingDirectory(c,Catalog,ct);var one=ExactDecimal.Parse("1");
        var curves=data.Definitions["curves"].Where(curve=>frame.CurveContracts.ContainsKey(curve.Text("code"))).SelectMany(curve=>
            data.QuoteRows(curve.Text("quote_code")).ToArray() is [{Kind:QuoteKind.Outright} quote]&&quote.Multiplier==one
                ?new[]{new{code=curve.Text("code"),name=curve.Text("name"),quote=quote.Key.Code,quantity=quote.Quantity.Code,
                    commodity=data.References["quote_specifications"].Single(q=>q.Text("code")==quote.Key.Code).Text("commodity_code"),
                    currency=quote.Currency.Code,priceUnit=data.PriceUnit(curve.Text("code")),quantityUnit=data.QuantityUnit(quote.Quantity.Code),
                    contracts=data.Requests(frame,curve.Text("code")).SelectMany(r=>data.ContractRows(r.Contract.Id)).Select(k=>new{from=Day(k.DeliveryFrom),to=Day(k.DeliveryTo)}).ToArray()}}:[])
            .OrderBy(curve=>curve.name,StringComparer.Ordinal).ToArray();
        return new{kind="gas_storage",convention=Convention,planConvention=PlanConvention,directory.Books,directory.BookingActor,directory.LegalEntities,directory.Counterparties,
            definitionRevisionId=data.DefinitionRevision.RevisionId,referenceCatalogId=data.ReferenceRevision.CatalogId,referenceRevisionId=data.ReferenceRevision.RevisionId,curves,
            clocks=data.References["clock_rules"].Where(r=>r.Text("kind")=="fixed_offset").Select(r=>r.Text("code")).Order(StringComparer.Ordinal).ToArray(),
            maximumRatchets=GasStoragePlans.MaximumRatchets,maximumDays=GasStoragePlans.MaximumDays,
            payment=new{frequency="monthly",rule="days_after_period_end",dayBasis="calendar_days",adjustment="unadjusted",implementation=PaymentRule.Implementation,maximumDays=366},
            policies=new{ratchetBases=new[]{GasStorageEntitlement.OpeningInventoryBand},flowPolicies=new[]{GasStorageEntitlement.SingleDirection},fuelBases=new[]{GasStorageEntitlement.InKindAtPoint},
                planSearches=new[]{GasStoragePlanProposals.Implementation}}};
    }
}
