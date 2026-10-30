using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class BookedScheduleLookupChecks
{
    internal static void Run()
    {
        var fixture=new Fixture();
        var definition=ScheduleDefinitionCompiler.Generate(fixture.Side);
        BookedSideProgram Reload(ScheduleDefinition value)
        {
            var compiled=BookedSideProgram.Compile(value);var bytes=compiled.Write();
            return BookedSideProgram.Read(bytes,new(BookedSideProgram.Digest(bytes),value.Manifest,value.Family,value.EconomicIdentity,value.PricingBindingIdentity));
        }
        var program=Reload(definition);
        var lookups=BookedScheduleLookups.Dated(fixture.Data,program);
        if(lookups.Length!=2||!lookups.Select(value=>value.Contract.Key).SequenceEqual(fixture.Contracts.Select(value=>value.Key))||
            lookups.Any(value=>value.Settlement!=fixture.Selection||value.Fixing is null||value.Fixing.Index!=fixture.Selection.SelectedIndex)||
            !lookups.Select(value=>value.Fixing!.FixingAt).SequenceEqual(fixture.Fixings.Select(value=>value.FixingAt)))
            throw new Exception("Stored dated observations must retain exact contracts, separate settlement index and independently qualified publication instants.");
        // The decoder has only retained executable bytes and pinned metadata: no
        // Side terms, prompt selector or valuation clock can choose another contract.
        if(!program.Write().SequenceEqual(Reload(definition).Write()))throw new Exception("Loading changed the retained source program.");
        ScheduleDefinition Rewrite(Func<ScheduleDefinitionBinding,bool> keep,Func<ScheduleDefinitionBinding,ScheduleDefinitionBinding> change)=>
            new(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
                definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,
                definition.Bindings.Where(keep).Select(change).ToArray(),definition.Roots,definition.Ports,definition.Grids);
        void Reject(string behavior,ScheduleDefinition changed)
        {
            var loaded=Reload(changed); // Structurally valid stored program; decoder must qualify source relationships.
            try{BookedScheduleLookups.Dated(fixture.Data,loaded);}
            catch(MarketDataException error)when(error.Code=="side_market_sources"){return;}
            throw new Exception("Stored-source decoder accepted "+behavior);
        }
        void RejectTopology(string behavior,Func<ScheduleDefinition> construct)
        {
            try{_ = construct();}
            catch(MarketDataException error)when(error.Code=="side_schedule_definition"){return;}
            throw new Exception("Stored-source topology accepted "+behavior);
        }
        RejectTopology("an override without its retained fixing source",()=>Rewrite(b=>b.Port!="fixing",b=>b));
        Reject("a same-period contract outside the declared curve",Rewrite(_=>true,b=>b.Port=="market"&&b.Market!.Value.Contract==fixture.Contracts[0].Key
            ?b with{Market=b.Market.Value with{Contract=fixture.OtherContract.Key}}:b));
        var curveTarget=PriceSettlementSelection.CurveTarget(fixture.Selection.CurveTargetIndex);
        RejectTopology("obsolete projection-only target terms without retained fixing sources",()=>new ScheduleDefinition(
            definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
            definition.SchemaVersion,definition.GeneratorVersion,
            definition.Rows.Select(row=>row.Operation=="price_lookup"?row with{Parameters=row.Parameters.SetItem("selection",DefinitionIdentity.For(curveTarget).Canonical)}:row).ToArray(),
            definition.Edges,definition.Bindings.Where(binding=>binding.Port!="fixing")
                .Select(binding=>binding.Port=="index"?binding with{Reference=curveTarget.SelectedIndex}:binding).ToArray(),
            definition.Roots,definition.Ports,definition.Grids));
        Reject("a quote not owned by the pinned curve",Rewrite(_=>true,
            binding=>binding.Port=="quote"?binding with{Reference=fixture.OtherQuote.Key}:binding));
        var quote=fixture.Side.DateQuote!;
        var observationFrom=new DateOnly(2040,1,1);
        var sources=fixture.Contracts.Select((contract,n)=>
        {
            var day=observationFrom.AddDays(n);var at=new DateTimeOffset(day.ToDateTime(new TimeOnly(16,0),DateTimeKind.Utc));
            return new ContractObservationLookup(new(new(contract.Key.Revision,"projection"),contract.Key),contract,quote,
                new(n,day,day.AddDays(1),at),at,new(quote.Key.Revision,"marks"),fixture.Fixings[n].Series,
                ObservationRole.TraderMark,ContractObservationExpiry.RequireLive,ContractObservationLookup.SupportedImplementation);
        }).ToImmutableArray();
        var sourcePeriod=new FloatingSidePeriod(observationFrom,observationFrom.AddMonths(1),sources[0].FixingAt,
            new DateTimeOffset(2040,2,3,0,0,0,TimeSpan.Zero),FloatingAveraging.ObservationArithmetic,
            [..sources.Select(value=>new SwapProjection(value.Market,value.Contract))],[],null,null,null,null,new ExactRatio(1,1),
            FloatingSide.ContractObservationImplementation){ContractObservations=sources};
        var sourceSide=new FloatingSide(Guid.NewGuid(),quote,new(ExactDecimal.Parse("1"),quote.Quantity),[sourcePeriod]);
        var sourceSwap=new FinancialSwapInstrument(Guid.NewGuid(),
            [new(sourceSide,ExactDecimal.Parse("-1")),new(sourceSide,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,false);
        foreach(var (at,eligible) in new[]{(sources[0].FixingAt.AddTicks(-1),true),(sources[0].FixingAt,false),(sources[0].FixingAt.AddTicks(1),false)})
            if(SwapStore.ScheduledAmendmentEligible(sourceSwap,at)!=eligible)
                throw new Exception("Scheduled source amendments must retain their explicit first-observation boundary after schedule-marker retirement.");
        var sourceDefinition=ScheduleDefinitionCompiler.Generate(sourceSide);
        var sourceProgram=Reload(sourceDefinition);
        var decoded=BookedScheduleLookups.Contracts(fixture.Data,sourceProgram);
        if(!decoded.SequenceEqual(sources))throw new Exception("Stored source bytes must decode exact observations, anchors, contracts, series, source and role without selector input.");
        ScheduleDefinition ChangeSource(Func<ScheduleDefinitionBinding,ScheduleDefinitionBinding> binding,
            Func<ScheduleDefinitionRow,ScheduleDefinitionRow>? row=null)=>new(sourceDefinition.SideId,sourceDefinition.Family,
                sourceDefinition.EconomicIdentity,sourceDefinition.PricingBindingIdentity,sourceDefinition.SchemaVersion,sourceDefinition.GeneratorVersion,
                sourceDefinition.Rows.Select(value=>row is null?value:row(value)).ToArray(),sourceDefinition.Edges,
                sourceDefinition.Bindings.Select(binding).ToArray(),sourceDefinition.Roots,sourceDefinition.Ports,sourceDefinition.Grids);
        void RejectSource(string behavior,ScheduleDefinition changed)
        {
            var retainedProgram=Reload(changed);
            try{BookedScheduleLookups.Contracts(fixture.Data,retainedProgram);}
            catch(MarketDataException error)when(error.Code=="side_market_sources"){return;}
            throw new Exception("Stored contract-source decoder accepted "+behavior);
        }
        RejectSource("a source other than the curve's declared source",ChangeSource(b=>b.Port=="settlement"?b with{Reference=fixture.Fixings[0].Source}:b));
        RejectSource("a role other than the curve's declared role",ChangeSource(b=>b.Port=="settlement"?b with{Coordinate="DailySettlement"}:b));
        RejectSource("a series for a different contract",ChangeSource(b=>b.Port=="settlement"&&b.Series==sources[0].Series?b with{Series=sources[1].Series}:b));
        RejectSource("carry without the curve's explicit expiry rule",ChangeSource(b=>b,row=>row.Operation==ContractObservationScheduleSource.Operation?
            row with{Parameters=row.Parameters.SetItem("expiryPolicy","CarryFinalSettlement")}:row));
        Console.WriteLine("PASS retained contract sources: exact executable-byte decoding; wrong source, role, series and undeclared carry policy refuse");
        Console.WriteLine("PASS booked schedule lookups: exact contracts and settlement publications survive stored bytes; nonmember contract, inconsistent quote and absent override fixing refuse at decoding");
    }

    private sealed class Fixture
    {
        internal PreviewData Data { get; }
        internal FloatingSide Side { get; }
        internal ImmutableArray<MarketContract> Contracts { get; }
        internal ImmutableArray<IndexFixing> Fixings { get; }
        internal MarketContract OtherContract { get; }
        internal PriceSettlementSelection Selection { get; }
        internal QuoteSpecification OtherQuote { get; }
        internal Fixture()
        {
            var definitions=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
            ReferenceKey R(string code)=>new(reference,code);
            var from=new DateOnly(2040,2,1);
            Contracts=[..Enumerable.Range(0,2).Select(n=>new MarketContract(new(definitions,Guid.NewGuid()),ContractKind.Forward,
                from.AddMonths(n),from.AddMonths(n+1),new DateTimeOffset(2040,1,20+n,0,0,0,TimeSpan.Zero),null))];
            OtherContract=Contracts[0] with{Key=new(definitions,Guid.NewGuid())};
            QuoteSpecification Quote(string code)=>new(R(code),QuoteKind.Outright,R("usd"),R("quantity"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
            var quote=Quote("price");OtherQuote=Quote("other_price");
            Selection=new(PriceSettlementMode.Override,R("projection_index"),R("settlement_index"),PriceSettlementSelection.ForwardCurveNoBasis);
            Fixings=[..Contracts.Select((contract,n)=>new IndexFixing(Selection.SelectedIndex,quote.Key,contract.DeliveryFrom,contract.DeliveryTo,
                new DateTimeOffset(2040,2+n,2,16,0,0,TimeSpan.Zero),new(definitions,Guid.NewGuid()),R("publication"),IndexFixing.SupportedImplementation))];
            var periods=Contracts.Select((contract,n)=>new FloatingSide(Guid.NewGuid(),Selection,quote,new(ExactDecimal.Parse("1"),quote.Quantity),
                contract.DeliveryFrom,contract.DeliveryTo,[Fixings[n]],new DateTimeOffset(contract.DeliveryTo.AddDays(3).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)),
                FloatingAveraging.MonthlyIndexPrice,[new(new(new(definitions,"projection"),contract.Key),contract)],FloatingSide.IndexedImplementation).Periods[0]).ToImmutableArray();
            Side=new(Guid.NewGuid(),Selection,quote,new(ExactDecimal.Parse("1"),quote.Quantity),periods);
            JsonElement Row(object value)=>JsonSerializer.SerializeToElement(value);
            Dictionary<string,ImmutableArray<JsonElement>> rows=new()
            {
                ["revisions"]=[Row(new{catalog_id=definitions.CatalogId,id=definitions.RevisionId,reference_catalog_id=reference.CatalogId,reference_revision_id=reference.RevisionId,state="sealed"})],
                ["contracts"]=[..Contracts.Append(OtherContract).Select(c=>Row(new{id=c.Key.Id,kind="forward",delivery_from=c.DeliveryFrom.ToString("yyyy-MM-dd"),delivery_to=c.DeliveryTo.ToString("yyyy-MM-dd"),expiry_at=c.Expiry,futures_series_code=(string?)null}))],
                ["curves"]=[Row(new{code="projection",quote_code="price",location_code="hub",profile_code="uniform",settlement_target_code="target",rule="direct",direct_role="trader_mark",direct_source_code="marks"})],
                ["curve_members"]=[..Contracts.Select((c,n)=>Row(new{curve_code="projection",contract_id=c.Key.Id,ordinal=n}))],
                ["series"]=[..Fixings.Select((f,n)=>Row(new{id=f.Series.Id,contract_id=Contracts[n].Key.Id,quote_code="price",value_specification_code=(string?)null}))],
                ["index_fixing_observations"]=[..Fixings.Select(f=>Row(new{index_code=f.Index.Code,quote_code=f.Quote.Code,observation_from=f.From.ToString("yyyy-MM-dd"),observation_to=f.To.ToString("yyyy-MM-dd"),fixing_at=f.FixingAt,series_id=f.Series.Id,source_code=f.Source.Code,implementation=f.Implementation,reference_catalog_id=reference.CatalogId,reference_revision_id=reference.RevisionId}))],
                ["curve_inputs"]=[],["constituent_mappings"]=[],["rate_curves"]=[],["rate_curve_points"]=[],["revision_periods"]=[],["contract_intervals"]=[],["curve_expiry_rules"]=[]
            };
            Dictionary<string,ImmutableArray<JsonElement>> references=new()
            {
                ["quantity_specifications"]=[Row(new{code="quantity",commodity_code="gas",basis_code="energy",unit_code="mwh",dimension_code="energy"})],
                ["quote_specifications"]=[..new[]{quote,OtherQuote}.Select(q=>Row(new{code=q.Key.Code,kind="outright",currency_code="usd",quantity_code="quantity",multiplier="1",trading_tick="0.01",clearing_increment="0.01",minimum="-1000",maximum="1000"}))],
                ["settlement_targets"]=[Row(new{code="target",kind="index",index_a_code="projection_index",index_b_code=(string?)null})],
                ["sources"]=[Row(new{code="publication"}),Row(new{code="marks"})],["value_specifications"]=[]
            };
            Data=new(new("local-synthetic-preview",definitions.CatalogId,definitions.RevisionId,reference.CatalogId,reference.RevisionId,Guid.NewGuid(),Guid.NewGuid(),[],null),rows,references,[]);
        }
    }
}
