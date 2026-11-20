using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed record TicketStatusRequest(Guid OperationId,string? Product=null,string? Profile=null)
{
    internal bool UsesCustody()
    {
        if(Product==GasParkLoanTicket.Product&&Profile is null||
            Profile is not(null or GasParkAndLoanTerms.Version or GasParkLoanBalanceFeeTerms.Version or GasParkCustodyTerms.Version)||
            Product==GasParkCustodyTicket.Product&&Profile is not(null or GasParkCustodyTerms.Version)||
            Product is not(GasParkLoanTicket.Product or GasParkCustodyTicket.Product)&&Profile is not null)
            throw new MarketDataException("ticket_input","Retain the exact supported product family and selected profile for booking recovery.");
        return Product==GasParkCustodyTicket.Product||Product==GasParkLoanTicket.Product&&Profile==GasParkCustodyTerms.Version;
    }
}
internal sealed class GasParkLoanTicketStore(PreviewDatabase database)
{
    internal static readonly JsonSerializerOptions Json=GasOwnedOperations.Json;
    internal async Task<JsonElement> Status(NpgsqlConnection c,Guid operation,CancellationToken ct)=>
        await LifecycleNative.Read(c,"SELECT security.gas_park_loan_booking_status($1,$2)::text",ct,database.Manifest.CatalogId,operation);
    internal static LifecycleReadContext Context(InputFrame frame)=>new(frame.ContextId??throw GasParkLoanTicket.Invalid("profile","Select a certified source context."),frame.At,frame.BusinessDate,frame.Id);
    async Task<T> Certified<T>(NpgsqlConnection c,PreviewData data,InputFrame frame,Func<PreviewData,InputFrame,Task<T>> read,CancellationToken ct)=>
        frame.ContextId is null?await TradingBookingRead.Run(database,c,frame,read,ct):await read(data,frame);

    internal Task<object> Choices(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketFieldRequest request,CancellationToken ct)=>Certified<object>(c,data,frame,async(d,f)=>
    {
        if(request.Draft.Product!=GasParkLoanTicket.Product||request.Side is not null||!GasParkLoanTicket.Describe(request.Draft).TradeFields.Any(x=>x.Id==request.Field))
            throw GasParkLoanTicket.Invalid(request.Field,"Select a declared agreement field.");
        if(GasParkLoanTicket.StaticChoices(request.Field) is {} fixedChoices)return new TicketChoices(fixedChoices,null);
        if(request.Field is "book" or "legal_entity" or "counterparty")
        {
            if(request.Field=="book")
            {
                var books=await BookScope.Read(c,database.Manifest.CatalogId,ct);
                return new TicketChoices([..books.Select(b=>new TicketChoice(b.Id("id").ToString(),b.Text("name")))],null);
            }
            var parties=await TradeParties.Catalog(c,database.Manifest.CatalogId,request.Field=="legal_entity",ct);
            return new TicketChoices([..parties.Select(p=>new TicketChoice($"{p.Id}@{p.Revision}",p.Name))],null);
        }
        if(request.Field is not("owned_account" or "claim_account"))return new TicketChoices([],null);
        var state=await GasOwnedOperations.Read(c,database,Context(f),null,ct);
        var accounts=state.GetProperty("accounts").EnumerateArray().Where(a=>a.Text("kind")==
            (request.Field=="owned_account"?"owned_inventory":"operator_balance"));
        var values=request.Draft.Trade;
        if(values.TryGetValue("book",out var book)&&Guid.TryParse(book,out var bookId))accounts=accounts.Where(a=>a.Id("book_id")==bookId);
        accounts=accounts.Where(a=>a.Id("definition_revision_id")==d.DefinitionRevision.RevisionId);
        if(values.TryGetValue("owned_account",out var selected)&&Guid.TryParse(selected,out var selectedId)&&request.Field=="claim_account")
        {
            var owner=state.GetProperty("accounts").EnumerateArray().SingleOrDefault(a=>a.Id("id")==selectedId);
            if(owner.ValueKind==JsonValueKind.Object)accounts=accounts.Where(a=>Compatible(owner,a));
        }
        if(request.Field=="claim_account"&&values.TryGetValue("counterparty",out var partySelection))
        {
            var pieces=partySelection.Split('@');
            if(pieces.Length==2&&Guid.TryParse(pieces[0],out var partyId)&&int.TryParse(pieces[1],out var partyRevision))
                accounts=accounts.Where(a=>a.OptionalId("counterparty_id")==partyId&&a.GetProperty("counterparty_revision").GetInt32()==partyRevision);
        }
        var choices=accounts.Select(a=>new TicketChoice(a.Id("id").ToString(),$"{a.Text("code")} · {a.Text("location_code")} · {a.Text("quantity_code")} · {a.Text("currency_code")}")).ToImmutableArray();
        if(request.Field=="claim_account")choices=[new("none","Not applicable for a principal receiver"),..choices];
        return new TicketChoices(choices,null);
    },ct);

    internal Task<object> Lookup(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketFieldRequest request,CancellationToken ct)=>
        throw GasParkLoanTicket.Invalid(request.Field,"This profile has no derived lookup for that field. Select the explicit agreement terms.");

    internal Task<object> Preview(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketPreviewRequest request,CancellationToken ct)=>
        Certified<object>(c,data,frame,async(d,f)=>
        {
            var body=GasParkLoanTicket.Request(request.Draft,f.Id,f.ContextId!.Value,request.Context.KnowledgeMode??"",Guid.Empty,d.DefinitionRevision.RevisionId);
            var state=await GasOwnedOperations.Read(c,database,Context(f),body.OwnedAccountId,ct);
            var account=state.GetProperty("accounts").EnumerateArray().SingleOrDefault(a=>a.Id("id")==body.OwnedAccountId);
            if(account.ValueKind!=JsonValueKind.Object||account.Text("kind")!="owned_inventory"||account.Id("book_id")!=body.BookId||
                account.Id("definition_revision_id")!=body.DefinitionRevisionId||account.Id("legal_entity_id")!=body.LegalEntityId||
                account.GetProperty("legal_entity_revision").GetInt32()!=body.LegalEntityRevision)
                throw GasParkLoanTicket.Invalid("owned_account","Choose an exact inventory account in this book, entity and market source.");
            if(body.ClaimAccountId is {} claimId)
            {
                var claimState=await GasOwnedOperations.Read(c,database,Context(f),claimId,ct);
                var claim=claimState.GetProperty("accounts").EnumerateArray().SingleOrDefault(a=>a.Id("id")==claimId);
                if(claim.ValueKind!=JsonValueKind.Object||claim.Text("kind")!="operator_balance"||!Compatible(account,claim)||
                    claim.OptionalId("counterparty_id")!=body.CounterpartyId||claim.GetProperty("counterparty_revision").GetInt32()!=body.CounterpartyRevision)
                    throw GasParkLoanTicket.Invalid("claim_account","Choose the exact compatible return claim account for this counterparty.");
            }
            var knowledge=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(c,f.ContextId.Value,ct)).Context;
            var snapshot=MarketSnapshotBinding.Bind(d.Snapshot(f),f.ContextId.Value,SchedulePreview.ParseAt(knowledge.KnownAt));
            var agreement=Draft(body,account,f,snapshot);
            var value=await Value(c,d,f,snapshot,agreement,true,ct);
            var composition=GasServiceSidePresentation.ParkLoan(database.Manifest.CatalogId,agreement,true);
            return new{snapshotId=frame.Id,definitionRevisionId=body.DefinitionRevisionId,presentValue=value,currency=account.Text("currency_code"),
                quantityUnit=account.Text("quantity_code"),schedule=Schedule(agreement,value,f.At),summaries=Array.Empty<object>(),scheduleRefusal=(object?)null,links=Array.Empty<object>(),
                source=new CatalogueSource(body.DefinitionRevisionId,account.Id("reference_catalog_id"),account.Id("reference_revision_id")),context=knowledge,
                measure=GasParkLoanTicket.Measure,measureLabel=MeasureLabel(agreement),overviewSections=Sections(agreement),
                ownershipNote=OwnershipNote(agreement),sideGroups=composition.Groups,sideGroupsReason=composition.Reason};
        },ct);

    internal async Task<JsonElement> Book(string profile,InputFrame frame,TicketBookRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty)throw GasParkLoanTicket.Invalid("profile","Supply the booking request identity.");
        GasParkLoanBooking? prepared=null;
        ScheduleContext? preparedSource=null;
        var prior=await database.WithIdentity(profile,"BookTrade",async(c,token)=>
        {
            var status=await Status(c,request.OperationId,token);
            if(status.GetProperty("found").GetBoolean())
            {
                var saved=status.GetProperty("request").Deserialize<GasParkLoanBooking>(Json)??throw new InvalidOperationException("Missing native booking request.");
                var attempted=GasParkLoanTicket.Request(request.Draft,saved.SnapshotId,saved.ContextId,request.Context.KnowledgeMode??"",request.OperationId,request.DefinitionRevisionId);
                if(saved!=attempted||saved.SnapshotId!=request.Context.SnapshotId)throw GasParkLoanTicket.Invalid("profile","The request identity belongs to different accepted terms.");
                return (JsonElement?)status.GetProperty("outcome").Clone();
            }
            prepared=await TradingBookingRead.Run(database,c,frame,async(data,pinned)=>
            {
                MarketInputSelection.RequireTerms(data,request.DefinitionRevisionId);
                _=await Preview(c,data,pinned,new(request.Context,request.Draft),token);
                preparedSource=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(c,pinned.ContextId!.Value,token)).Context;
                return GasParkLoanTicket.Request(request.Draft,frame.Id,pinned.ContextId!.Value,request.Context.KnowledgeMode??"",request.OperationId,request.DefinitionRevisionId);
            },token);
            return null;
        },ct);
        if(prior is {} recorded)return recorded;
        return await database.WithIdentity(profile,"BookTrade",async(c,token)=>
        {
            var source=await new KnowledgeResolver(database.Manifest.CatalogId).Retained(c,preparedSource??throw new InvalidOperationException("Missing prepared booking inputs."),token);
            await TradingContext.AdmitSource(database,c,source,frame.At,token);
            return await LifecycleNative.Read(c,"SELECT security.book_gas_park_loan($1,$2::jsonb,$3)::text",token,database.Manifest.CatalogId,JsonSerializer.Serialize(prepared,Json),frame.At);
        },ct);
    }

    internal async Task<object> Trade(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketTradeRequest request,CancellationToken ct)
    {
        var row=await Read(c,database,Context(frame),request.TradeId,ct);
        if(row.Id("instrumentId")!=request.BindingId||row.GetProperty("revision").GetInt32()!=request.Revision)
            throw GasParkLoanTicket.Invalid("profile","Select the exact retained agreement binding and revision.");
        var knowledge=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(c,frame.ContextId!.Value,ct)).Context;
        var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),frame.ContextId.Value,SchedulePreview.ParseAt(knowledge.KnownAt));
        string? value=null;string? unavailable=null;
        try{value=await Value(c,data,frame,snapshot,row,false,ct);}
        catch(MarketDataException error)when(ValuationStatus.PositionSpecific(error)||error.Code is "gas_park_loan_reconciliation" or "discount_curve" or "gas_balance_fee_advance_window")
        {unavailable=error.Message;}

        var t=row.GetProperty("terms");
        var composition=GasServiceSidePresentation.ParkLoan(database.Manifest.CatalogId,row);
        return new{snapshotId=frame.Id,context=knowledge,draft=GasParkLoanTicket.Reopen(row),definitionRevisionId=t.Id("definitionRevisionId"),
            presentValue=value,currency=t.Text("currency"),quantityUnit=t.Text("quantity"),schedule=Schedule(row,value,frame.At),summaries=Array.Empty<object>(),scheduleRefusal=(object?)null,
            source=new CatalogueSource(t.Id("definitionRevisionId"),t.Id("referenceCatalogId"),t.Id("referenceRevisionId")),facilityAllocations=Array.Empty<object>(),links=Array.Empty<object>(),sideSlots=ImmutableDictionary<string,int>.Empty,
            actions=new{amend=new{allowed=false,reason="Agreement amendments require an installed continuity profile."},closeOut=new{allowed=false,reason="Complete the independent principal and cash obligations."},record=new{allowed=false,reason="Use the agreement's lifecycle actions to accept advances and returns."}},
            overviewSections=Sections(row,unavailable),unavailableReason=unavailable,measureLabel=MeasureLabel(row),ownershipNote=OwnershipNote(row),
            sideGroups=composition.Groups,sideGroupsReason=composition.Reason,
            lifecycleTarget=new LifecycleTarget(GasParkLoanActions.Handler,request.TradeId,request.BindingId)};
    }

    internal static async Task<JsonElement> Read(NpgsqlConnection c,PreviewDatabase database,LifecycleReadContext context,Guid trade,CancellationToken ct)
    {
        var row=await LifecycleNative.Read(c,"SELECT security.read_gas_park_loan($1,$2,$3,$4)::text",ct,database.Manifest.CatalogId,trade,context.ContextId,context.AsOf);
        if(row.Id("contextId")!=context.ContextId||row.Instant("asOf")!=context.AsOf||DateWorkspace.Date(row.Text("effectiveOn"))!=context.BusinessDate)
            throw GasParkLoanTicket.Invalid("profile","Retain the agreement's exact business and knowledge context.");
        database.RegisterCommitAdmission(c,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,row.Instant("authorityUntil")));
        return row;
    }

    internal async Task<string> Value(NpgsqlConnection c,PreviewData data,InputFrame frame,MarketSnapshot snapshot,JsonElement agreement,bool draft,CancellationToken ct)
    {
        var inputs=await GasParkLoanPortfolio.PrepareAgreements(c,database,frame,snapshot,[agreement],[(data,frame)],false,draft,ct);
        var total=ExactMoney.Parse("0",GasParkLoanPortfolio.Terms(database.Manifest.CatalogId,agreement).Currency);
        var exact=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(c,frame.ContextId!.Value,ct)).Context;
        foreach(var input in inputs)
        {
            var dependencies=input.Portfolio.Declare(input.Snapshot).SelectMany(x=>x.Dependencies).Distinct().ToImmutableArray();
            var connected=await CustomCurves.PrepareMarket(c,database,input.Data,input.Frame,input.Snapshot,exact,input.Portfolio.Instruments,
                dependencies,CustomCurves.MarketPreparation.ValuationDependencies,ct);
            var prepared=RiskEngine.PrepareConnected(connected.Valuation,connected.Bindings,[input.Portfolio],connected.Authority,ct);
            var run=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,ct,RiskDetail.Books);
            foreach(var money in run.Groups.SelectMany(g=>g.BaseValues).Select(x=>x.Value))total=total.Add(money);
        }
        return total.ToExactString();
    }

    static ScheduleView Schedule(JsonElement agreement,string? value,DateTimeOffset at)
    {
        var terms=agreement.GetProperty("terms");
        var cells=new Dictionary<string,ScheduleViewCell>{
            ["trade.principal"]=new(agreement.Text("quantity"),["contract"]),
            ["trade.present_value"]=new(value,["modeled"])
        }.ToImmutableDictionary();
        return new(["term"],[new("agreement",null,"term",terms.Instant("advanceAt"),terms.Instant("returnAt"),null,cells,false)],
            cells,null,null,0,1,at);
    }

    static bool Compatible(JsonElement owner,JsonElement account)=>new[]{"book_id","legal_entity_id","legal_entity_revision","reference_catalog_id","reference_revision_id","quantity_code","location_code","definition_revision_id","mark_curve_code","mark_contract_id","currency_code"}.All(k=>owner.Text(k)==account.Text(k));
    internal static JsonElement Draft(GasParkLoanBooking b,JsonElement a,InputFrame f,MarketSnapshot snapshot)
    {
        var draft=JsonSerializer.SerializeToElement(new{
        implementation=b.Profile,tradeId=GasParkAndLoanValuation.PreviewIdentity(b.OperationId,f.Id,"trade"),instrumentId=GasParkAndLoanValuation.PreviewIdentity(b.OperationId,f.Id,"instrument"),revision=1,
        bookId=b.BookId,quantity=b.Quantity,ownedAccountId=b.OwnedAccountId,claimAccountId=b.ClaimAccountId,legalEntityId=b.LegalEntityId,legalEntityRevision=b.LegalEntityRevision,counterpartyId=b.CounterpartyId,counterpartyRevision=b.CounterpartyRevision,
        publicationOrder=0,tradeReference=(string?)null,instrumentReference=(string?)null,reconciliationRequired=false,effectiveAt=f.At,contextId=f.ContextId,asOf=f.At,knownAt=snapshot.Knowledge!.KnownAt,effectiveOn=f.BusinessDate,
        terms=new{b.ServiceKind,b.Perspective,principalDirection=b.ServiceKind=="loan"&&b.Perspective=="customer"||b.ServiceKind=="park"&&b.Perspective=="provider"?1:-1,
            definitionRevisionId=b.DefinitionRevisionId,referenceCatalogId=a.Id("reference_catalog_id"),referenceRevisionId=a.Id("reference_revision_id"),quantity=a.Text("quantity_code"),location=a.Text("location_code"),currency=a.Text("currency_code"),
            markCurve=a.Text("mark_curve_code"),markContractId=a.Id("mark_contract_id"),b.AdvanceAt,b.ReturnAt,b.FixedFeeAmount,b.FeeDirection,feeRecognition=GasParkAndLoanTerms.FeeAtAdvance,b.FeeDueAt,b.TitlePolicy,b.ReturnPolicy,b.ReturnTiming,b.Carrying},
        advance=(object?)null,balances=new{acceptedPrincipal="0",acceptedReturn="0",outstandingPrincipal="0"},
        fee=new{amount=b.FixedFeeAmount,unpaid=b.FixedFeeAmount,dueAt=b.FeeDueAt}},Json);
        if(b.Profile!=GasParkLoanBalanceFeeTerms.Version)return draft;
        if(b.BalanceFee is null||b.FixedFeeAmount!="0"||b.FeeDueAt is null)
            throw GasParkLoanTicket.Invalid("profile","Retain the complete explicitly selected balance-time fee contract.");
        var selected=JsonNode.Parse(draft.GetRawText())!.AsObject();
        selected["terms"]!["feeRecognition"]=GasParkLoanBalanceFeeTerms.CumulativeDifference;
        selected["terms"]!["balanceFee"]=JsonSerializer.SerializeToNode(b.BalanceFee,Json);
        selected["returns"]=new JsonArray();
        selected["fee"]=JsonSerializer.SerializeToNode(new{amount="0",paid="0",unpaid="0",cumulativeDeterminedTotal="0",dueAt=b.FeeDueAt,determinations=Array.Empty<object>()},Json);
        return JsonSerializer.SerializeToElement(selected,Json);
    }
    internal static string MeasureLabel(JsonElement agreement)=>agreement.Text("implementation")==GasParkLoanBalanceFeeTerms.Version?"Remaining service plan PV":"Remaining scheduled plan PV";
    internal static string OwnershipNote(JsonElement agreement)=>agreement.Text("implementation")==GasParkLoanBalanceFeeTerms.Version?
        "This service value includes planned gas before advance and undetermined fee cash. Accepted principal and determined fee cash, paid or unpaid, belong to independent native holdings. The whole-book ownership policy includes those holdings.":
        "Accepted principal and fee cash are valued through their independent holdings after advance.";
    static object[] Sections(JsonElement agreement,string? unavailable=null)
    {
        var t=agreement.GetProperty("terms");var b=agreement.GetProperty("balances");var fee=agreement.GetProperty("fee");
        var principal=b.Text("outstandingPrincipal");
        var sections=new List<object>{new{label="Principal",values=new[]{new{label="Committed quantity",value=agreement.Text("quantity")+" "+t.Text("quantity")},new{label="Accepted advance",value=b.Text("acceptedPrincipal")},new{label="Accepted returns",value=b.Text("acceptedReturn")},new{label="Outstanding return",value=principal}}},
            new{label="Dates",values=new[]{new{label="Advance",value=t.Text("advanceAt")},new{label="Return due",value=t.Text("returnAt")},new{label="Fee due",value=t.OptionalText("feeDueAt")??"Not applicable"}}},
            };
        if(agreement.Text("implementation")==GasParkLoanBalanceFeeTerms.Version)
        {
            var terms=t.GetProperty("balanceFee");
            sections.Add(new{label="Outstanding-balance fee",values=new[]{
                new{label="Absolute rate",value=terms.Text("rateAmount")+" "+t.Text("currency")+" / "+t.Text("quantity")+" / elapsed UTC hour"},
                new{label="Direction",value=t.Text("feeDirection")},new{label="Accrue through",value=terms.Text("accrualThroughAt")},
                new{label="Overdue treatment",value="Accrue accepted outstanding principal until the fee cutoff; principal remains owed after cutoff."},
                new{label="Remaining-return plan",value="Outstanding principal returns at the fee cutoff for valuation. Contractual return due remains unchanged."},
                new{label="Unadvanced plan",value="Before return due, advance at the later of earliest advance or evaluation time. No actual accrual before acceptance."},
                new{label="Cash rounding",value=terms.Text("roundingIncrement")+" "+t.Text("currency")+" · "+terms.Text("roundingTieRule")+" · cumulative payment total"},
                new{label="Cumulative determined fee",value=fee.Text("amount")+" "+t.Text("currency")},
                new{label="Net unpaid determined fee",value=fee.Text("unpaid")+" "+t.Text("currency")}}});
        }
        else sections.Add(new{label="Independent fee",values=new[]{new{label="Fixed fee",value=t.Text("fixedFeeAmount")+" "+t.Text("currency")},new{label="Unpaid fee",value=fee.Text("unpaid")+" "+t.Text("currency")}}});
        if(unavailable is not null)sections.Add(new{label="Valuation",values=new[]{new{label="Unavailable",value=unavailable}}});
        return [..sections];
    }
}
