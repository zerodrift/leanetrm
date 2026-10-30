using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed record CloseQuoteFactor(QuoteFactor Factor,ExactDecimal BumpSize);
public sealed record CloseFactorRisk(string Measure,Guid BookId,ReferenceKey Currency,InputKey Factor,
    ExactMoney Up,ExactMoney Down,ExactMoney UpPnl,ExactMoney DownPnl,ExactRiskDerivative Delta,ExactRiskDerivative Gamma);
public sealed record ClosePortfolioValuationResult(Guid SnapshotId,ScheduleContext Context,PortfolioValuationPolicy Policy,
    ImmutableArray<UnitPricingContract> ModelContracts,ImmutableArray<PortfolioPositionEvidence> Positions,
    ImmutableArray<CloseQuoteFactor> Factors,ImmutableArray<PortfolioTotal> Totals,
    ImmutableArray<CloseFactorRisk> Risk,RiskWork Work)
{
    public ImmutableArray<PortfolioDiscountEvidence> DiscountInputs{get;init;}=[];
    public ValuationReceipt? PpaReceipt{get;init;}
    public ImmutableArray<BookValueMeasures> PpaMeasures{get;init;}=[];
}

// Publication accepts only a fenced service occurrence. Core relationships are
// normalized in storage; the serialized command is parsed and not retained.
public static class PilotCloseReports
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    static MarketDataException Invalid()=>new("pilot_report_input","The report requires complete exact results for this close and model.");
    public static async Task<bool> Exists(NpgsqlConnection connection,Guid runId,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT automation.pilot_report_exists($1)",runId);
        return await command.ExecuteScalarAsync(ct) is true;
    }
    public static Task<Guid> Publish(NpgsqlConnection connection,Guid runId,PortfolioValuationResult result,CancellationToken ct)
        =>Publish(connection,runId,Adapt(result),ct);
    internal static ClosePortfolioValuationResult Adapt(PortfolioValuationResult result)
    {
        var factors=result.Factors.Select(f=>new CloseQuoteFactor(f.Axis.Quote??throw Invalid(),f.BumpSize)).ToImmutableArray();
        var byId=result.Factors.Select((f,i)=>(f.Axis.Id,i)).ToDictionary(p=>p.Id,p=>p.i);
        var risk=result.Risk.Select(r=>new CloseFactorRisk(r.Measure,r.BookId,r.Currency,factors[byId[r.Factor]].Factor.Input,
            r.Up,r.Down,r.UpPnl,r.DownPnl,r.Delta,r.Gamma)).ToImmutableArray();
        return new ClosePortfolioValuationResult(result.SnapshotId,result.Context,result.Policy,result.ModelContracts,
            result.Positions,factors,result.Totals,risk,result.Work){DiscountInputs=result.DiscountInputs,PpaReceipt=result.PpaReceipt,PpaMeasures=result.PpaMeasures};
    }
    public static async Task<Guid> Publish(NpgsqlConnection connection,Guid runId,ClosePortfolioValuationResult result,CancellationToken ct)
    {
        PortfolioValuation.ValidatePolicy(result.Policy);
        if(runId==Guid.Empty||result.SnapshotId==Guid.Empty||result.ModelContracts.IsDefaultOrEmpty||result.Positions.IsDefaultOrEmpty||
            result.Factors.IsDefault||result.Totals.IsDefaultOrEmpty||result.Risk.IsDefault||result.DiscountInputs.IsDefault)throw Invalid();
        var reference=result.ModelContracts[0].Currency.Revision;
        var definition=result.Positions[0].DefinitionRevisionId;
        void Reference(ReferenceKey key){if(key.Revision!=reference)throw Invalid();}
        foreach(var model in result.ModelContracts)
        {
            Reference(model.Currency);Reference(model.Quantity);
            if(model.Dependencies.IsDefault||model.Dependencies.Any(d=>d.Curve.Revision.RevisionId!=definition||d.Contract.Revision!=d.Curve.Revision))throw Invalid();
        }
        foreach(var position in result.Positions)
        {
            Reference(position.Quantity.Specification);
            if(position.DefinitionRevisionId!=definition||
                position.Measure is not(PortfolioValuation.FuturesMeasure or PortfolioValuation.SwapMeasure or PortfolioValuation.PpaMeasure))throw Invalid();
        }
        var indices=result.Factors.Select((factor,n)=>(factor.Factor.Input,n)).ToDictionary(p=>p.Input,p=>p.n);
        var holdingsByValuation=result.Positions.ToLookup(p=>p.ValuationId);
        var swapSides=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT catalog_id,instrument_id,fixed_side_id,floating_side_id,first_floating_side_id,scheduled_first_id,scheduled_second_id FROM trading.swap_instruments WHERE instrument_id=ANY($1))t",
            [result.DiscountInputs.Where(d=>!d.Ppa).Select(d=>d.InstrumentId).Distinct().ToArray()],ct);
        var sidesByInstrument=swapSides.ToDictionary(s=>s.Id("instrument_id"));
        var snapshot=result.DiscountInputs.IsEmpty?null:result.DiscountInputs[0].Input.Snapshot;
        var inputUses=snapshot?.Selections.ToDictionary(s=>s.Observation.Key,s=>s.Input.Use);
        void Exact(ExactMoney money){Reference(money.Currency);}
        var totals=result.Totals.Select(t=>{Reference(t.Value.Currency);return new{t.Measure,book=t.BookId,currency=t.Value.Currency.Code,n=t.Value.Numerator,d=t.Value.Denominator};}).ToArray();
        var risk=result.Risk.Select(r=>
        {
            Reference(r.Currency);Exact(r.Up);Exact(r.Down);Exact(r.UpPnl);Exact(r.DownPnl);Exact(r.Delta.Value);Exact(r.Gamma.Value);
            var factor=result.Factors[indices[r.Factor]].Factor;
            if(r.Up.Currency!=r.Currency||r.Down.Currency!=r.Currency||r.UpPnl.Currency!=r.Currency||r.DownPnl.Currency!=r.Currency||r.Delta.Value.Currency!=r.Currency||r.Gamma.Value.Currency!=r.Currency||
                r.Delta.Unit.Currency!=r.Currency||r.Gamma.Unit.Currency!=r.Currency||r.Delta.Unit.Order!=1||r.Gamma.Unit.Order!=2||
                r.Delta.Unit.Factor!=factor.Unit||r.Gamma.Unit.Factor!=factor.Unit)throw Invalid();
            return new{r.Measure,book=r.BookId,currency=r.Currency.Code,factor=indices[r.Factor],up_n=r.Up.Numerator,up_d=r.Up.Denominator,down_n=r.Down.Numerator,down_d=r.Down.Denominator,
                up_pnl_n=r.UpPnl.Numerator,up_pnl_d=r.UpPnl.Denominator,down_pnl_n=r.DownPnl.Numerator,down_pnl_d=r.DownPnl.Denominator,delta_n=r.Delta.Value.Numerator,delta_d=r.Delta.Value.Denominator,gamma_n=r.Gamma.Value.Numerator,gamma_d=r.Gamma.Value.Denominator};
        }).ToArray();
        var factors=result.Factors.Select((f,ordinal)=>
        {
            var value=f.Factor;Reference(value.Quote);Reference(value.Source);Reference(value.Unit.Currency);Reference(value.Unit.Quantity);
            if(value.Input.Use!=InputUse.Variable||value.Input.Series.Revision.RevisionId!=definition||value.Contract.Revision!=value.Input.Series.Revision||value.Role is not(ObservationRole.TraderMark or ObservationRole.DailySettlement))throw Invalid();
            return new{ordinal,series=value.Input.Series.Id,observation=value.Observation.Id,contract=value.Contract.Id,quote=value.Quote.Code,currency=value.Unit.Currency.Code,quantity=value.Unit.Quantity.Code,
                source=value.Source.Code,role=value.Role==ObservationRole.TraderMark?"trader_mark":"daily_settlement",@base=value.BaseValue.ToString(),bump=f.BumpSize.ToString(),tick=value.TradingTick.ToString()};
        }).ToArray();
        var discounts=result.DiscountInputs.Select((d,ordinal)=>
        {
            Reference(d.Input.Currency);Reference(d.Payment.Currency);
            var settlement=d.SettlementDeterminationId;
            if(settlement is not null&&(d.Ppa||settlement!=d.Payment.SideId))throw Invalid();
            var sides=d.Ppa||settlement is not null?(JsonElement?)null:sidesByInstrument[d.InstrumentId];
            var fixedSide=sides is {} swap&&d.Payment.SideId==swap.OptionalId("fixed_side_id");
            var firstFloating=sides is {} pair&&d.Payment.SideId==pair.OptionalId("first_floating_side_id");
            var scheduledFirst=sides is {} firstSchedule&&d.Payment.SideId==firstSchedule.OptionalId("scheduled_first_id");
            var scheduledSecond=!scheduledFirst&&sides is {} secondSchedule&&d.Payment.SideId==secondSchedule.OptionalId("scheduled_second_id");
            if(settlement is null&&!d.Ppa&&!fixedSide&&!firstFloating&&!scheduledFirst&&!scheduledSecond&&d.Payment.SideId!=sides!.Value.Id("floating_side_id")||d.Input.Snapshot.Id!=result.SnapshotId||!d.Input.Snapshot.HasSameInputs(snapshot!))throw Invalid();
            return new{ordinal,instrument=d.InstrumentId,@fixed=fixedSide?(Guid?)d.Payment.SideId:null,floating=settlement is null&&!d.Ppa&&!fixedSide&&!firstFloating&&!scheduledFirst&&!scheduledSecond?(Guid?)d.Payment.SideId:null,first_floating=firstFloating?(Guid?)d.Payment.SideId:null,ppa=d.Ppa?(Guid?)d.Payment.SideId:null,scheduled_first=scheduledFirst?(Guid?)d.Payment.SideId:null,scheduled_second=scheduledSecond?(Guid?)d.Payment.SideId:null,
                settlement, payment_ordinal=d.Payment.Ordinal,payment_at=d.Payment.PaymentAt,payment_date=d.Input.PaymentDate,enabled=d.Input.Enabled,factor=d.Input.Factor.ToString(),mapping=d.Input.MappingId,
                curve=d.Input.Curve?.Code,currency=d.Input.Currency.Code,rate_policy=d.Input.RateNumericalPolicy,interpolation_policy=d.Input.InterpolationPolicy};
        }).ToArray();
        var observations=result.DiscountInputs.SelectMany((d,n)=>d.Input.Pillars.Select((p,ordinal)=>new{discount=n,ordinal,observation=p.Observation.Id,
            input_use=inputUses![p.Observation]==InputUse.Variable?"variable":"frozen",maturity=p.Maturity,annual_rate=p.AnnualRate,factor=p.DiscountFactor,days=p.Days}));
        var ppa=result.PpaReceipt;
        object? ppaReceipt=ppa is null?null:new{ppa.KnownAt,ppa.KnowledgeContext,ppa.Coverage,
            pendingActuals=ppa.Pending==PendingActualsPolicy.RequireActuals?"require_actuals_v1":"estimate_until_actual_v1",
            quantities=ppa.QuantityRevisions.Select(q=>new{series=q.Series,revision=q.Revision,known_at=q.KnownAt,context=q.Context}),
            shapes=ppa.Shapes.Select(q=>new{curve=q.Curve.Code,series=q.Series,revision=q.Revision}),
            actuals=ppa.Actuals.Select(q=>new{batch=q.Batch,revision=q.Revision,known_at=q.KnownAt}),
            assets=ppa.AssetVersions.Select(q=>new{asset=q.Asset.Code,effective_from=q.From})};
        var payload=new{ppaReceipt,
            ppaBalances=result.PpaMeasures.Select(m=>new{book=m.BookId,currency=m.Currency.Code,
                known_n=m.Measures.OverdueKnownBalance.Numerator,known_d=m.Measures.OverdueKnownBalance.Denominator,
                estimated_n=m.Measures.OverdueEstimated.Numerator,estimated_d=m.Measures.OverdueEstimated.Denominator}),snapshotId=result.SnapshotId,contextId=PinnedContext.Exact(result.Context).ContextId,result.Policy,result.Work,
            models=result.ModelContracts.Select(m=>
            {
                var holdings=holdingsByValuation[m.Id].ToArray();
                if(holdings.Length==0||holdings.Select(p=>p.Measure).Distinct().Count()!=1)throw Invalid();
                var future=holdings[0].Measure==PortfolioValuation.FuturesMeasure;
                if(future&&m.Dependencies.Length!=1)throw Invalid();
                return new{id=m.Id,model=m.ModelVersion,quantity=m.Quantity.Code,currency=m.Currency.Code,
                    future=future?(Guid?)m.Dependencies.Single().Contract.Id:null,swap=holdings[0].Measure==PortfolioValuation.SwapMeasure?(Guid?)holdings.Select(p=>p.InstrumentId).Distinct().Single():null,
                    ppa=holdings[0].Measure==PortfolioValuation.PpaMeasure?(Guid?)holdings.Select(p=>p.InstrumentId).Distinct().Single():null};
            }),
            dependencies=result.ModelContracts.SelectMany(m=>m.Dependencies.Select(d=>new{model=m.Id,curve=d.Curve.Code,contract=d.Contract.Id})),
            positions=result.Positions.Select((p,ordinal)=>new{ordinal,trade=p.TradeId,future=p.Measure==PortfolioValuation.FuturesMeasure?(int?)p.Revision:null,
                swap=p.Measure==PortfolioValuation.SwapMeasure?(int?)p.Revision:null,ppa=p.Measure==PortfolioValuation.PpaMeasure?(int?)p.Revision:null,coverage_from=p.Coverage?.From,coverage_to=p.Coverage?.To,book=p.BookId,binding=p.BindingId,instrument=p.InstrumentId,publication=p.PublicationOrder,valuation=p.ValuationId,quantity=p.Quantity.Amount.ToString(),unit=p.Quantity.Specification.Code}),
            factors,totals,risk,discounts,discountObservations=observations};
        await using var command=ScheduleStore.Command(connection,"SELECT automation.publish_pilot_report($1,$2::jsonb)",runId,JsonSerializer.Serialize(payload,Json));
        return (Guid)(await command.ExecuteScalarAsync(ct)??throw Invalid());
    }
    public static async Task<object> Read(NpgsqlConnection connection,Guid runId,PinnedContext pin,CancellationToken ct)
    {
        // The caller binds its selected view; the calculation context below is
        // evidence and never substitutes for that selected knowledge cutoff.
        async Task Guard()
        {
            await using var guard=ScheduleStore.Command(connection,"SELECT automation.require_pilot_report_view($1,$2::uuid)",runId,(object?)pin.Context?.ContextId??DBNull.Value);
            await guard.ExecuteNonQueryAsync(ct);
        }
        await using var load=ScheduleStore.Command(connection,"SELECT automation.read_pilot_report($1,$2::uuid)::text",runId,(object?)pin.Context?.ContextId??DBNull.Value);
        using var document=JsonDocument.Parse((string)(await load.ExecuteScalarAsync(ct)??throw new PreviewDeniedException()));
        var rowsets=document.RootElement;
        var h=rowsets.GetProperty("header").Clone();var catalog=h.Id("catalog_id");var reference=new ReferenceRevision(h.Id("reference_catalog_id"),h.Id("reference_revision_id"));
        if(pin.Context is {} selected)await new KnowledgeStore(catalog).Retained(connection,selected,ct);
        ReferenceKey Key(string code)=>new(reference,code);
        object Cash(string n,string d,string currency)=>new{numerator=n,denominator=d,currency=Key(currency)};
        ImmutableArray<JsonElement> Rows(string name)=>[..rowsets.GetProperty(name).EnumerateArray().Select(row=>row.Clone())];
        var books=Rows("books");var bookNames=books.ToDictionary(b=>b.Id("id"),b=>b.Text("name"));
        string Book(Guid id)=>bookNames.TryGetValue(id,out var name)?name:throw new PreviewDeniedException();
        var positions=Rows("positions");var totals=Rows("totals");var factors=Rows("factors");
        var factorByOrdinal=factors.ToDictionary(f=>f.Text("ordinal"),StringComparer.Ordinal);
        object Factor(JsonElement f)=>new{input=new InputKey(new(new(catalog,h.Id("definition_revision_id")),f.Id("series_id")),InputUse.Variable),
            contract=new ContractKey(new(catalog,h.Id("definition_revision_id")),f.Id("contract_id")),observation=new ObservationKey(catalog,f.Id("observation_id")),quote=Key(f.Text("quote_code")),source=Key(f.Text("source_code")),role=f.Text("role"),
            label=f.Text("series_code")+" ("+f.Text("delivery_from")+" — "+f.Text("delivery_to")+")",unit=Presentation.PriceUnitLabel.Format(new(Key(f.Text("currency_code")),Key(f.Text("quantity_code")),ExactDecimal.Parse(f.Text("multiplier"))),f.Text("currency_label"),f.Text("unit_label")),
            priceUnit=new{currency=Key(f.Text("currency_code")),quantity=Key(f.Text("quantity_code")),multiplier=f.Text("multiplier")},bumpSize=f.Text("bump_size")};
        var risks=Rows("risk");var discounts=Rows("discounts");var discountObservations=Rows("discountObservations");
        var models=Rows("models");var dependencies=Rows("dependencies");
        if(positions.IsEmpty||totals.IsEmpty||models.IsEmpty)throw Invalid();
        var dependenciesByModel=dependencies.ToLookup(d=>d.Id("model_id"));
        var observationsByDiscount=discountObservations.ToLookup(o=>o.Text("discount_ordinal"),StringComparer.Ordinal);
        var result=new{reportId=runId,runId,state="report_published",mode=h.Text("mode"),close=new{id=h.Id("close_id"),name=h.Text("close_name"),at=h.Instant("close_at"),kind=h.Text("close_kind")},
            context=new ScheduleContext(h.Text("effective_on"),int.Parse(h.Text("known_through")),h.Instant("known_at").ToString("O"),h.Id("context_id")),policy=h.GetProperty("policy"),work=h.GetProperty("work"),
            model=new DefinitionRevision(catalog,h.Id("definition_revision_id")),reference,
            modelContracts=models.Select(m=>new{id=m.Id("id"),modelVersion=m.Text("model_version"),quantity=Key(m.Text("quantity_code")),currency=Key(m.Text("currency_code")),
                dependencies=dependenciesByModel[m.Id("id")].Select(d=>new CurveRequest(new(new(catalog,h.Id("definition_revision_id")),d.Text("curve_code")),new(new(catalog,h.Id("definition_revision_id")),d.Id("contract_id")))).ToArray()}).ToArray(),
            positions=positions.Select(p=>new{tradeId=p.Id("trade_id"),revision=int.Parse(p.OptionalText("future_revision")??p.OptionalText("swap_revision")??p.Text("ppa_revision")),bindingId=p.Id("binding_id"),instrumentId=p.Id("instrument_id"),valuationId=p.Id("valuation_id"),bookId=p.Id("book_id"),book=Book(p.Id("book_id")),measure=p.Text("measure"),coverage=p.OptionalText("coverage_from") is {} from?new{from,to=p.Text("coverage_to")}:null,quantity=new{amount=p.Text("quantity"),specification=Key(p.Text("quantity_code"))}}).ToArray(),
            totals=totals.Select(t=>new{measure=t.Text("measure"),bookId=t.Id("book_id"),book=Book(t.Id("book_id")),currency=Key(t.Text("currency_code")),currencyLabel=t.Text("currency_label"),value=Cash(t.Text("numerator"),t.Text("denominator"),t.Text("currency_code"))}).ToArray(),
            risk=risks.Select(r=>
            {
                var f=factorByOrdinal[r.Text("factor_ordinal")];var currency=r.Text("currency_code");
                object Derivative(string name,int order)=>new{value=Cash(r.Text(name+"_n"),r.Text(name+"_d"),currency),unit=new{currency=Key(currency),factor=new{currency=Key(f.Text("currency_code")),quantity=Key(f.Text("quantity_code")),multiplier=f.Text("multiplier")},order}};
                return new{measure=r.Text("measure"),bookId=r.Id("book_id"),book=Book(r.Id("book_id")),currencyLabel=r.Text("currency_label"),factor=Factor(f),up=Cash(r.Text("up_n"),r.Text("up_d"),currency),down=Cash(r.Text("down_n"),r.Text("down_d"),currency),upPnl=Cash(r.Text("up_pnl_n"),r.Text("up_pnl_d"),currency),downPnl=Cash(r.Text("down_pnl_n"),r.Text("down_pnl_d"),currency),delta=Derivative("delta",1),gamma=Derivative("gamma",2)};
            }).ToArray(),ppaReceipt=rowsets.GetProperty("ppaInputs").Clone(),
            ppaQuantityPins=Rows("ppaQuantities"),ppaShapePins=Rows("ppaShapes"),ppaActualPins=Rows("ppaActuals"),ppaAssetPins=Rows("ppaAssets"),
            ppaBalances=Rows("ppaBalances").Select(b=>new{bookId=b.Id("book_id"),currency=Key(b.Text("currency_code")),
                overdueKnownBalance=Cash(b.Text("known_n"),b.Text("known_d"),b.Text("currency_code")),overdueEstimated=Cash(b.Text("estimated_n"),b.Text("estimated_d"),b.Text("currency_code"))}).ToArray(),discountInputs=discounts.Select(d=>new{instrumentId=d.Id("instrument_id"),settlementDeterminationId=d.OptionalId("settlement_determination_id"),payment=new{sideId=d.OptionalId("fixed_side_id")??d.OptionalId("first_floating_side_id")??d.OptionalId("floating_side_id")??d.OptionalId("scheduled_first_side_id")??d.OptionalId("scheduled_second_side_id")??d.OptionalId("settlement_determination_id")??d.Id("ppa_side_id"),ordinal=int.Parse(d.Text("payment_ordinal")),paymentAt=d.Instant("payment_at"),currency=Key(d.Text("currency_code"))},
                input=new{enabled=d.GetProperty("enabled").GetBoolean(),factor=d.Text("factor"),mappingId=d.OptionalId("mapping_id"),curve=d.OptionalText("curve_code") is {} curve?(CurveKey?)new(new(catalog,h.Id("definition_revision_id")),curve):null,
                    currency=Key(d.Text("currency_code")),paymentDate=d.Text("payment_date"),rateNumericalPolicy=d.OptionalText("rate_policy"),interpolationPolicy=d.OptionalText("interpolation_policy"),
                    observations=observationsByDiscount[d.Text("ordinal")].Select(o=>new{observation=new ObservationKey(catalog,o.Id("observation_id")),inputUse=o.Text("input_use"),maturity=o.Text("maturity"),annualRate=o.Text("annual_rate"),discountFactor=o.Text("discount_factor"),days=int.Parse(o.Text("days"))}).ToArray()}}).ToArray()};
        await Guard();
        return result;
    }
}
