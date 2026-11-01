using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;

internal static class RetainedScheduleChecks
{
    internal static int Run()
    {
        ScheduleManifestChecks.Run();
        CompactMonthlyBounds();
        CompactRetainedChecks.Run();
        var fixture=new CommodityLookupFixture(2,monthly:true);
        var dated=fixture.Swap(monthly:true,monthOrdinal:0);
        var schedule=ScheduleDefinitionCompiler.Generate(dated.Floating);
        if(schedule.Rows.Count<=dated.Floating.GenerateSchedule().Rows.Count||
            !schedule.Rows.Any(r=>r.Operation=="price_lookup")||
            !schedule.Rows.Any(r=>r.Operation=="price_application")||
            !schedule.Rows.Any(r=>r.Operation=="weighted_price")||
            !schedule.Rows.Any(r=>r.Operation=="discount")||
            schedule.Roots.Count(r=>r.Measure=="pv")!=1)
            throw new Exception("A retained floating schedule must include observations, original applications, averaging, discounting and an explicit PV root.");
        if(schedule.WithSideId(Guid.NewGuid()).Manifest!=schedule.Manifest)
            throw new Exception("Remapping a retained exact Side UUID must not alter its complete canonical content manifest.");
        var fixedSchedule=ScheduleDefinitionCompiler.Generate(dated.Fixed);
        var second=fixture.Swap(monthly:true,monthOrdinal:1).Fixed;
        var periods=ImmutableArray.Create(dated.Fixed.Periods[0],second.Periods[0]);
        var quote=fixture.Quote;var priceUnit=PriceUnit.FromQuote(quote);
        var original=new FixedSide(Guid.NewGuid(),new FlatPrice(new(ExactDecimal.Parse("10"),priceUnit)),quote,dated.Fixed.UnitQuantity,periods);
        var amended=new FixedSide(Guid.NewGuid(),new SteppedPrice([
            new(periods[0].DeliveryFrom,new(ExactDecimal.Parse("11"),priceUnit)),
            new(periods[1].DeliveryFrom,new(ExactDecimal.Parse("10"),priceUnit))]),quote,dated.Fixed.UnitQuantity,periods);
        var originalSecond=ScheduleDefinitionCompiler.Generate(original).Rows.Single(r=>r.Operation=="output_guard"&&r.PaymentOrdinal==1);
        var amendedSecond=ScheduleDefinitionCompiler.Generate(amended).Rows.Single(r=>r.Operation=="output_guard"&&r.PaymentOrdinal==1);
        if(originalSecond.OccurrenceKey!=amendedSecond.OccurrenceKey||originalSecond.TermsDigest!=amendedSecond.TermsDigest)
            throw new Exception("Amending an earlier strike must preserve an unchanged later obligation, including when its literal was formerly shared with that earlier period.");
        if(fixedSchedule.Rows.All(r=>r.Operation!="constant_price")||fixedSchedule.Rows.All(r=>r.Operation!="quantity_literal"))
            throw new Exception("A fixed side must retain its contractual literal rows independently of market values.");
        if(schedule.Rows.Any(r=>r.Operation=="price_lookup"&&r.Parameters.ContainsKey("value")))
            throw new Exception("A future price lookup cannot persist a sampled market price.");
        var referencedMarket=schedule.Bindings.First(binding=>binding.Kind=="contract_price").Market;
        Reject(()=>new ScheduleDefinition(schedule.SideId,schedule.Family,schedule.EconomicIdentity,schedule.PricingBindingIdentity,
            schedule.SchemaVersion,schedule.GeneratorVersion,schedule.Rows,schedule.Edges,
            [..schedule.Bindings.Select(binding=>binding.Kind=="index"?binding with{Market=referencedMarket}:binding)],schedule.Roots,schedule.Ports,schedule.Grids));
        var invalid=schedule.Rows.ToArray();invalid[0]=invalid[0] with{Parameters=invalid[0].Parameters.Add("unrecognized","1")};
        Reject(()=>new ScheduleDefinition(schedule.SideId,schedule.Family,schedule.EconomicIdentity,schedule.PricingBindingIdentity,
            schedule.SchemaVersion,schedule.GeneratorVersion,invalid,schedule.Edges,schedule.Bindings,schedule.Roots,schedule.Ports,schedule.Grids));
        Reject(()=>new ScheduleDefinition(schedule.SideId,schedule.Family,schedule.EconomicIdentity,schedule.PricingBindingIdentity,
            schedule.SchemaVersion,schedule.GeneratorVersion,schedule.Rows,
            [..schedule.Edges,new ScheduleDefinitionEdge(0,"cycle",0,schedule.Rows.Count-1)],schedule.Bindings,schedule.Roots,schedule.Ports,schedule.Grids));
        Reject(()=>new ScheduleDefinition(schedule.SideId,schedule.Family,schedule.EconomicIdentity,schedule.PricingBindingIdentity,
            schedule.SchemaVersion,schedule.GeneratorVersion,schedule.Rows,
            [..schedule.Edges.Where(e=>e.Port!="discount")],schedule.Bindings,schedule.Roots,schedule.Ports,schedule.Grids));
        var ppaFixture=new PpaAcceptanceFixture();
        var capacityPayment=ppaFixture.CapacityAgreement().Memberships[1].Side;
        var capacityIndex=ppaFixture.R("synthetic_capacity_index");
        var indexedCapacity=new FloatingSide(Guid.NewGuid(),capacityPayment.Terms,new(ppaFixture.C("synthetic_capacity"),
            ExactDecimal.Parse("1"),new(default,ppaFixture.CapacityPrice),PriceSettlementSelection.CurveTarget(capacityIndex)));
        var indexedSchedule=ScheduleDefinitionCompiler.Generate(indexedCapacity);
        if(indexedSchedule.Rows.Where(row=>row.Operation=="price_lookup").Any(row=>
            !indexedSchedule.Bindings.Any(binding=>binding.Row==row.Ordinal&&binding.Kind=="index"&&binding.Reference==capacityIndex)))
            throw new Exception("A retained period lookup must pin its selected index with a typed foreign-key binding, independently of operator parameters.");
        var daily=new IndexFixingChecks.FixingFixture();
        var mapped=daily.Floating().WithCustomProjection(new(daily.Base.Revision.CatalogId,Guid.NewGuid(),1,"synthetic_output",null,
            CustomCurveProjectionBinding.DeliverySelection,CustomCurveProjectionBinding.SupportedImplementation));
        var mappedSchedule=ScheduleDefinitionCompiler.Generate(mapped);
        if(mappedSchedule.Bindings.Count(b=>b.Kind=="custom_output"&&b.Position is null)!=3)
            throw new Exception("A retained daily custom output must preserve delivery mapping without inventing a fixed position.");
        var certificate=ppaFixture.WithCertificate().Memberships.Single(m=>m.Side is CertificateDeliverySide).Side;
        var native=ScheduleDefinitionCompiler.Generate(certificate);
        if(native.Roots.Count(r=>r.Measure=="schedule_quantity")!=native.Grids.Count(g=>g.FirstRowOrdinal is not null)||
            native.Rows.Count(r=>r.Operation=="quantity_transform")!=native.Grids.Count(g=>g.FirstRowOrdinal is not null)||
            native.Grids.Where(g=>g.FirstRowOrdinal is not null).Sum(g=>g.CellCount)!=ppaFixture.Hours.Length||
            native.Bindings.Any(b=>b.Port=="location"&&b.Kind!="quantity_source"))
            throw new Exception("Retained certificate blocks require explicit converted quantity columns, every exact interval cell and each source's own location-price reference.");
        var instrument=ppaFixture.Physical();
        var definitions=instrument.Sides.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side));
        var retained=RetainedSideSchedules.Attach(instrument,definitions);
        var scope=new RetainedSideSchedules.ReadScope();var firstAttached=scope.Attach(instrument,definitions);
        var originalPayment=(FixedSide)instrument.Memberships[1].Side;
        var otherPayment=new FixedSide(Guid.NewGuid(),originalPayment.Terms,new FlatPrice(new(ExactDecimal.Parse("42"),ppaFixture.EnergyPrice)));
        var otherInstrument=new PpaInstrument(Guid.NewGuid(),ppaFixture.Definitions,ppaFixture.Reference,
            instrument.Memberships.SetItem(1,instrument.Memberships[1] with{Side=otherPayment}),false,PpaInstrument.Implementation);
        var otherDefinitions=definitions.Where(pair=>pair.Key!=originalPayment.Id).ToDictionary(pair=>pair.Key,pair=>pair.Value);
        otherDefinitions.Add(otherPayment.Id,ScheduleDefinitionCompiler.Generate(otherPayment));
        var secondAttached=scope.Attach(otherInstrument,otherDefinitions);
        if(!ReferenceEquals(firstAttached.Memberships[0].Side.RetainedPlan,secondAttached.Memberships[0].Side.RetainedPlan))
            throw new Exception("Two instruments sharing one exact retained side must reuse its admitted static schedule within the read.");
        if(ReferenceEquals(firstAttached.Memberships[1].Side.RetainedPlan,secondAttached.Memberships[1].Side.RetainedPlan))
            throw new Exception("Distinct contractual payment schedules must retain independent admitted definitions.");

        long AttachAllocations(bool reuse)
        {
            var before=GC.GetAllocatedBytesForCurrentThread();
            for(var n=0;n<8;n++)_ = reuse?scope.Attach(instrument,definitions):RetainedSideSchedules.Attach(instrument,definitions);
            return GC.GetAllocatedBytesForCurrentThread()-before;
        }
        AttachAllocations(false);AttachAllocations(true);
        var independentBytes=AttachAllocations(false);var sharedBytes=AttachAllocations(true);
        Console.WriteLine($"Retained side allocation: eight two-side attachments; independent {independentBytes} bytes; shared {sharedBytes} bytes.");
        if(sharedBytes>independentBytes*0.75)throw new Exception("Repeated exact retained sides must avoid static schedule admission allocation within the read.");
        var physicalPlan=firstAttached.Memberships[0].Side.RetainedPlan!;
        var expectedPrices=physicalPlan.NativeRows.SelectMany(row=>row.Prices).Distinct().ToArray();
        _=physicalPlan.PriceReferences.ToArray();
        var allocatedBefore=GC.GetAllocatedBytesForCurrentThread();var references=physicalPlan.PriceReferences.ToArray();
        var referenceBytes=GC.GetAllocatedBytesForCurrentThread()-allocatedBefore;
        if(!references.SequenceEqual(expectedPrices))throw new Exception("Static retained price discovery must preserve every exact template reference.");
        if(referenceBytes>4096)throw new Exception($"Reading one month of static price references must not materialize every hourly obligation; allocated {referenceBytes} bytes.");
        var payment=definitions[instrument.Memberships[1].Side.Id];var changed=payment.Rows.ToArray();
        var missingSlot=payment.Ports[0] with{Slot=null};
        Reject(()=>new ScheduleDefinition(payment.SideId,payment.Family,payment.EconomicIdentity,payment.PricingBindingIdentity,
            payment.SchemaVersion,payment.GeneratorVersion,payment.Rows,payment.Edges,payment.Bindings,payment.Roots,[missingSlot,missingSlot],payment.Grids));
        var literal=Array.FindIndex(changed,row=>row.Operation=="constant_price");
        changed[literal]=changed[literal] with{Parameters=changed[literal].Parameters.SetItem("numerator","41")};
        var revised=new ScheduleDefinition(payment.SideId,payment.Family,payment.EconomicIdentity,payment.PricingBindingIdentity,
            payment.SchemaVersion,payment.GeneratorVersion,changed,payment.Edges,payment.Bindings,payment.Roots,payment.Ports,payment.Grids);
        var alternative=RetainedSideSchedules.Attach(instrument,definitions.ToDictionary(p=>p.Key,p=>p.Key==payment.SideId?revised:p.Value));
        var changedAttached=scope.Attach(instrument,definitions.ToDictionary(p=>p.Key,p=>p.Key==payment.SideId?revised:p.Value));
        if(ReferenceEquals(firstAttached.Memberships[1].Side.RetainedPlan,changedAttached.Memberships[1].Side.RetainedPlan))
            throw new Exception("An identical side UUID and economic identity cannot borrow a different retained content manifest.");

        var market=ppaFixture.Market(ppaFixture.From.AddDays(-1));var inputs=ppaFixture.Prepare(market,[retained]);
        var sharedInputs=ppaFixture.Prepare(market,[firstAttached,secondAttached]);
        var freshOther=RetainedSideSchedules.Attach(otherInstrument,otherDefinitions);
        var freshInputs=ppaFixture.Prepare(market,[retained,freshOther]);
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        foreach(var pair in new[]{(Shared:firstAttached,Fresh:retained),(Shared:secondAttached,Fresh:freshOther)})
            if(sharedInputs.Session(market.Build).Value(pair.Shared,new(ppaFixture.From,ppaFixture.To),discount).PresentValue!=
                freshInputs.Session(market.Build).Value(pair.Fresh,new(ppaFixture.From,ppaFixture.To),discount).PresentValue)
                throw new Exception("Shared static side admission must preserve each instrument's exact independent value.");

        try
        {
            inputs.Session(market.Build).Value(alternative,new(ppaFixture.From,ppaFixture.To),
                new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
            throw new Exception("A prepared scope cannot reuse values for different retained schedule content under identical economic fields.");
        }
        catch(MarketDataException error)when(error.Code=="ppa_context"){}
        if(retained.RetainedScheduleIdentity==alternative.RetainedScheduleIdentity)
            throw new Exception("Retained schedule contents must participate in calculation identity independently of side economic identity.");
        MultipleAssets(ppaFixture);
        ProfilePayments(ppaFixture);
        RejectUnusedFixingBindings();
        RejectUnsupportedAffineInput(ppaFixture);
        Console.WriteLine("PASS retained schedules: complete logical observations/applications, explicit roots, canonical identity, closed parameters and cycles");
        return 9;
    }
    private static void CompactMonthlyBounds()
    {
        var fixture=new PpaAcceptanceFixture(15);var instrument=fixture.Physical(volume:true);
        var definitions=instrument.Sides.Select(member=>ScheduleDefinitionCompiler.Generate(member.Side)).ToArray();
        if(definitions.Sum(definition=>definition.Rows.Count)>64||definitions.Sum(definition=>definition.Bindings.Count)>16)
            throw new Exception("One month of quarter-hour volume and linked fixed payment must retain at most64 typed block operations and16 source bindings, independently of2884 interval cells.");
    }
    private static void MultipleAssets(PpaAcceptanceFixture fixture)
    {
        var original=fixture.WithCertificate();
        var asset=fixture.Asset with{Key=new(fixture.Definitions,"synthetic_second_asset"),
            Versions=[new(fixture.From,fixture.To,ExactDecimal.Parse("100"),ExactDecimal.Parse("80"))]};
        var source=fixture.Source with{Key=new(fixture.Definitions,"synthetic_second_source"),Asset=asset.Key,
            ForecastSeries=Guid.NewGuid(),MeteredSeries=Guid.NewGuid()};
        var normalization=new PpaNormalization([fixture.Asset.Key,asset.Key],fixture.Capacity.Key,PpaNormalizationPolicy.SumCapacityInForce);
        var volume=new VolumeQuantity([fixture.Source.Key,source.Key],QuantityStage.Generated,fixture.Energy.Key);
        var members=original.Memberships.Select(member=>
        {
            var terms=member.Side.Terms with{Normalization=normalization,Quantity=member.Side.Terms.Quantity is VolumeQuantity?volume:member.Side.Terms.Quantity};
            Side side=member.Side switch
            {
                PowerPhysicalSide power=>new PowerPhysicalSide(Guid.NewGuid(),terms,power.Location),
                FixedSide fixedSide=>new FixedSide(Guid.NewGuid(),terms,fixedSide.Price),
                CertificateDeliverySide certificate=>new CertificateDeliverySide(Guid.NewGuid(),terms,certificate.CertificateQuantity,certificate.Conversion,
                    certificate.Eligibility,certificate.Holding,certificate.Disposition),
                _=>throw new Exception("Unexpected synthetic member.")
            };
            return member with{Side=side};
        }).ToImmutableArray();
        var instrument=new PpaInstrument(Guid.NewGuid(),fixture.Definitions,fixture.Reference,members,false,PpaInstrument.Implementation);
        var retained=RetainedSideSchedules.Attach(instrument,instrument.Sides.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side)));
        var market=fixture.Market(fixture.From.AddDays(-1));
        var inputs=fixture.Prepare(market,[retained],extraAssets:[asset],extraSources:[source],
            extraSeries:[fixture.Forecast() with{Id=source.ForecastSeries},fixture.Meter() with{Id=source.MeteredSeries}]);
        var value=inputs.Session(market.Build).Value(retained,new(fixture.From,fixture.To),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        if(value.PresentValue.ToExactString()!="7931")
            throw new Exception("Two generators at25% of their combined300MW basis must retain exact physical and certificate PV7931 over721 hours.");
    }
    private static void ProfilePayments(PpaAcceptanceFixture fixture)
    {
        var terms=fixture.Terms(fixture.Profile);
        var instrument=new PpaInstrument(Guid.NewGuid(),fixture.Definitions,fixture.Reference,
            [new(new(PpaRole.IndexedPayment),new FloatingSide(Guid.NewGuid(),terms,new(fixture.C("synthetic_energy"),ExactDecimal.Parse("1"),new(default,fixture.EnergyPrice),null)),ExactDecimal.Parse("1")),
             new(new(PpaRole.StrikePayment),new FixedSide(Guid.NewGuid(),terms,new FlatPrice(new(ExactDecimal.Parse("40"),fixture.EnergyPrice))),ExactDecimal.Parse("-1"))],
            false,PpaInstrument.Implementation);
        var retained=RetainedSideSchedules.Attach(instrument,instrument.Sides.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side)));
        var market=fixture.Market(fixture.From.AddDays(-1));var inputs=fixture.Prepare(market,[retained]);
        var value=inputs.Session(market.Build).Value(retained,new(fixture.From,fixture.To),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        if(value.PresentValue.ToExactString()!="1442")throw new Exception("A retained fixed profile payment and its price must have distinct source identities and preserve exact721-hour PV1442.");
    }
    private static void RejectUnusedFixingBindings()
    {
        var fixture=new CommodityLookupFixture(31);var instrument=fixture.Swap();
        var definitions=instrument.Sides.ToDictionary(member=>member.Side.Id,member=>ScheduleDefinitionCompiler.Generate(member.Side));
        var definition=definitions[instrument.Floating.Id];
        Reject(()=>
        {
            var obsoleteRows=definition.Rows.Select(row=>row.Operation=="output_guard"?
                row with{Parameters=row.Parameters.SetItem("firstFixingAt",instrument.Floating.Fixings[0].FixingAt.ToString("O"))}:row).ToArray();
            var obsolete=new ScheduleDefinition(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
                definition.SchemaVersion,definition.GeneratorVersion,obsoleteRows,definition.Edges,definition.Bindings,definition.Roots,definition.Ports,definition.Grids);
            var attached=RetainedSideSchedules.Attach(instrument,definitions.ToDictionary(pair=>pair.Key,pair=>pair.Key==definition.SideId?obsolete:pair.Value));
            _=attached.Sides[1].Side.GenerateSchedule();
        });
        var unused=definition.Bindings.Where(binding=>binding.Kind=="index_fixing")
            .Select(binding=>binding with{Port="unused_fixing",Coordinate="unsupported_fixing_selection_v1"}).ToArray();
        if(unused.Length==0)throw new Exception("The dated regression requires final-index fixing bindings.");
        Reject(()=>
        {
            definitions[definition.SideId]=new(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
                definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,[..definition.Bindings,..unused],definition.Roots,definition.Ports,definition.Grids);
            RetainedSideSchedules.Attach(instrument,definitions);
        });
    }
    private static void RejectUnsupportedAffineInput(PpaAcceptanceFixture fixture)
    {
        var terms=fixture.Terms(fixture.Profile);
        var floating=new FloatingSide(Guid.NewGuid(),terms,new(fixture.C("synthetic_energy"),ExactDecimal.Parse("1"),
            new(ExactDecimal.Parse("1"),fixture.EnergyPrice),null));
        var fixedSide=new FixedSide(Guid.NewGuid(),terms,new FlatPrice(new(ExactDecimal.Parse("40"),fixture.EnergyPrice)));
        var instrument=new PpaInstrument(Guid.NewGuid(),fixture.Definitions,fixture.Reference,
            [new(new(PpaRole.IndexedPayment),floating,ExactDecimal.Parse("1")),new(new(PpaRole.StrikePayment),fixedSide,ExactDecimal.Parse("-1"))],
            false,PpaInstrument.Implementation);
        var definitions=instrument.Sides.ToDictionary(member=>member.Side.Id,member=>ScheduleDefinitionCompiler.Generate(member.Side));
        var definition=definitions[floating.Id];
        var retained=RetainedSideSchedules.Attach(instrument,definitions);
        var market=fixture.Market(fixture.From.AddDays(-1));var inputs=fixture.Prepare(market,[retained]);
        var value=inputs.Session(market.Build).Value(retained,new(fixture.From,fixture.To),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        if(value.PresentValue.ToExactString()!="2163")
            throw new Exception("The supported two-input affine price must preserve exact721-hour PV2163 before rejecting additional inputs.");
        var additions=definition.Rows.Where(row=>row.Operation=="price_add").ToArray();
        if(additions.Length==0)throw new Exception("The native regression requires an explicit affine price adder.");
        var thirdInputs=additions.Select(row=>new ScheduleDefinitionEdge(row.Ordinal,"input",2,
            definition.Edges.Single(edge=>edge.Target==row.Ordinal&&edge.Ordinal==1).Source));
        Reject(()=>
        {
            definitions[definition.SideId]=new(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
                definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,[..definition.Edges,..thirdInputs],definition.Bindings,definition.Roots,definition.Ports,definition.Grids);
            RetainedSideSchedules.Attach(instrument,definitions);
        });
    }
    private static void Reject(Action action)
    {try{action();}catch(MarketDataException error)when(error.Code=="side_schedule_definition"){return;}throw new Exception("Expected side_schedule_definition.");}
}
