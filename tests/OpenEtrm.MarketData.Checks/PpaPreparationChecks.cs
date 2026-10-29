using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

internal static class PpaPreparationChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action test){test();count++;Console.WriteLine("PASS PPA preparation: "+name);}
        var f=new PpaAcceptanceFixture();var market=f.Market(f.From.AddDays(-2));
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>> Full(PpaInstrument instrument)=>
            ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>>.Empty.Add(instrument.Id,[new(f.From,f.To)]);
        Check("market pins and quantity inputs cannot claim different resolved knowledge contexts",()=>{
            var instrument=f.Physical();var original=f.Prepare(market,[instrument]);
            try {IntervalValuationInputs.Prepare(original.Reference,original.Read,[instrument],market.Build,original.KnownAt,
                Guid.NewGuid(),IntervalKnowledgePolicy.ResolvedCutoff,original.Policy,"ppa_coverage_v1",[],Full(instrument));}
            catch(MarketDataException e) when(e.Code=="ppa_context"){return;}
            throw new Exception("Expected ppa_context for independently mixed market and quantity contexts.");
        });
        Check("a close freezes source knowledge at valuation under its later certified context",()=>{
            var instrument=f.Physical();var original=f.Prepare(market,[instrument]);var cutoff=market.Snapshot.ValuationAt;
            var resolved=cutoff.AddHours(1);var snapshot=market.Snapshot.WithKnowledge(f.QuantityKnowledge,resolved);
            var later=market.Model.Build(snapshot,[..market.Build.Values.Select(value=>value.Request)]);
            var inputs=IntervalValuationInputs.Prepare(original.Reference,original.Read,[instrument],later,cutoff,
                f.QuantityKnowledge,IntervalKnowledgePolicy.ValuationInstant,original.Policy,"ppa_coverage_v1",[],Full(instrument));
            var value=inputs.Session(later).Value(instrument,new(f.From,f.To),discount);
            if(value.PresentValue.ToMoney().Amount.ToString()!="1442"||inputs.Receipt.KnownAt!=cutoff||
                inputs.Receipt.MarketInputs.Knowledge?.KnownAt!=resolved||inputs.Receipt.KnowledgePolicy!=IntervalKnowledgePolicy.ValuationInstant)
                throw new Exception("A later certificate must preserve the close's exact source cutoff and interval PV.");
            foreach(var (source,policy) in new[]{(cutoff,IntervalKnowledgePolicy.ResolvedCutoff),
                (cutoff.AddMinutes(1),IntervalKnowledgePolicy.ValuationInstant),(cutoff,(IntervalKnowledgePolicy)999)})
            {
                try {IntervalValuationInputs.Prepare(original.Reference,original.Read,[instrument],later,source,
                    f.QuantityKnowledge,policy,original.Policy,"ppa_coverage_v1",[],Full(instrument));}
                catch(MarketDataException error) when(error.Code=="ppa_context"){continue;}
                throw new Exception("An explicit source policy cannot admit a different or unspecified cutoff.");
            }
        });
        Check("a late forecast correction changes resolved-cutoff PV but cannot enter the close or reuse its session",()=>{
            var instrument=f.Physical(volume:true);var cutoff=market.Snapshot.ValuationAt;var resolved=cutoff.AddHours(1);
            var forecast=f.Forecast();var history=forecast with {Cells=forecast.Cells.AddRange(forecast.Cells.Select(cell=>
                cell with {Value=ExactDecimal.Parse("0.5"),Revision=2,KnownAt=cutoff.AddMinutes(30)}))};
            var span=new DeliveryCoverage(f.Hours[0].Start,f.Hours[0].End);
            var coverage=ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>>.Empty.Add(instrument.Id,[span]);
            var original=f.Prepare(market,[instrument],forecastOverride:history,coverage:coverage);
            var snapshot=market.Snapshot.WithKnowledge(f.QuantityKnowledge,resolved);
            var later=market.Model.Build(snapshot,[..market.Build.Values.Select(value=>value.Request)]);
            var closeInputs=IntervalValuationInputs.Prepare(original.Reference,original.Read,[instrument],later,cutoff,
                f.QuantityKnowledge,IntervalKnowledgePolicy.ValuationInstant,original.Policy,"ppa_coverage_v1",[],coverage);
            var curves=original.Read.Curves.ToImmutableDictionary(pair=>pair.Key,pair=>{
                ImmutableArray<(DateTimeOffset,DateTimeOffset)> spans=pair.Value.Curve is PeriodContractCurve period?
                    [..period.Contracts.Select(contract=>(new DateTimeOffset(contract.DeliveryFrom.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero),
                        new DateTimeOffset(contract.DeliveryTo.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)))]:[(f.From,f.To)];
                return PreparedCurve.Prepare(pair.Value.Curve,spans,
                    pair.Value.Curve is ShapedIntervalCurve?[..f.Hours.Select(hour=>new SeriesCell(hour.Start,hour.End,ExactDecimal.Parse("0"),1))]:[],[],cutoff,resolved);
            });
            var currentInputs=IntervalValuationInputs.Prepare(original.Reference,original.Read with {Curves=curves},[instrument],later,resolved,
                f.QuantityKnowledge,IntervalKnowledgePolicy.ResolvedCutoff,original.Policy,"ppa_coverage_v1",[],coverage);
            var close=closeInputs.Session(later);var current=currentInputs.Session(later);
            if(close.Value(instrument,span,discount).PresentValue.ToMoney().Amount.ToString()!="0.5"||
                current.Value(instrument,span,discount).PresentValue.ToMoney().Amount.ToString()!="1"||
                close.Value(instrument,span,discount).PresentValue.ToMoney().Amount.ToString()!="0.5")
                throw new Exception("The one-hour forecast 0.25 must remain frozen at close; only the later cutoff selects 0.5.");
            try {currentInputs.Session(later,close);}
            catch(MarketDataException error) when(error.Code=="ppa_context"){return;}
            throw new Exception("Different source policies and quantities cannot reuse a prior valuation session.");
        });
        Check("asset delivery cannot ignore its explicit physical location",()=>{
            var original=f.Physical(volume:true);
            var members=original.Memberships.Select(m=>m.Side is PowerPhysicalSide?
                m with {Side=new PowerPhysicalSide(Guid.NewGuid(),m.Side.Terms,f.R("other_node"))}:m).ToImmutableArray();
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,members,false,PpaInstrument.Implementation);
            try {f.Prepare(market,[instrument]);}
            catch(MarketDataException e) when(e.Code=="ppa_delivery_location"){return;}
            throw new Exception("Expected ppa_delivery_location for an asset at a different physical location.");
        });
        Check("receipt exposes exact shape series and revisions",()=>{
            var inputs=f.Prepare(market,[f.Physical()]);
            var shape=inputs.Receipt.Shapes.Single(s=>s.Curve==f.C("synthetic_energy"));
            if(shape.Curve!=f.C("synthetic_energy") || shape.Series==Guid.Empty || shape.Revision!=1 ||
                !inputs.Receipt.MarketInputs.HasSameInputs(market.Snapshot) || inputs.Receipt.ReferenceInputs.Reference!=f.Reference ||
                inputs.Receipt.Instruments.Length!=1 || !inputs.Receipt.DetailMonths.SetEquals([f.Month]))
                throw new Exception("The valuation receipt must retain the selected exact inputs and shape revision.");
        });
        Check("summary-only valuation retains its exact scope and resolved factors",()=>{
            var instrument=f.Physical(discounting:true);var original=f.Prepare(market,[instrument]);
            var inputs=IntervalValuationInputs.Prepare(original.Reference,original.Read,[instrument],market.Build,original.KnownAt,original.KnowledgeContext,IntervalKnowledgePolicy.ResolvedCutoff,original.Policy,"ppa_coverage_v1",[],Full(instrument));
            var value=inputs.Session(market.Build).Value(instrument,new(f.From,f.From.AddHours(1)),new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit));
            var side=(IntervalSideValuation)value.Sides[0].Value;
            if(!side.Rows.IsEmpty || side.Receipt.Instrument!=instrument.EconomicIdentity || side.Receipt.Side!=side.SideId ||
                side.Receipt.Coverage!=new DeliveryCoverage(f.From,f.From.AddHours(1)) || side.Receipt.Shock is not null ||
                !ReferenceEquals(side.Receipt.Inputs,inputs.Receipt) || side.Receipt.Factors.Length!=1 ||
                side.Receipt.Factors[0].DiscountFactor!=ExactDecimal.Parse("0.9") || !side.Receipt.Factors[0].Snapshot.HasSameInputs(market.Snapshot))
                throw new Exception("Summary-only valuation must retain the scope and exact discount factors when detailed rows are omitted.");
        });
        Check("indexed capacity payment consumes its linked MW-day period quantity",()=>{
            var original=f.CapacityAgreement();
            var members=original.Memberships.Select(m=>m.Slot.Role==PpaRole.CapacityPayment?
                m with {Side=new FloatingSide(Guid.NewGuid(),m.Side.Terms,new(f.C("synthetic_capacity"),ExactDecimal.Parse("1"),new(default,f.CapacityPrice),PriceSettlementSelection.CurveTarget(f.R("synthetic_capacity_index"))))}:m).ToImmutableArray();
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,members,false,PpaInstrument.Implementation);
            var inputs=f.Prepare(market,[instrument]);
            var values=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,new(f.From,f.To),discount));
            if(values.ValueExcludingOverdue.Numerator!="0" || values.Rows.Length!=2 || values.Rows.Any(r=>r.Quantity.Numerator!="24"))
                throw new Exception("Matching indexed capacity legs must retain 24 MW-day per MW and offset exactly.");
        });
        Check("indexed certificate payment consumes its linked generation-vintage quantity",()=>{
            var original=f.WithCertificate();
            var members=original.Memberships.Select(m=>m.Slot.Role==PpaRole.CertificatePayment?
                m with {Side=new FloatingSide(Guid.NewGuid(),m.Side.Terms,new(f.C("synthetic_certificate"),ExactDecimal.Parse("1"),new(default,f.CertificatePrice),PriceSettlementSelection.CurveTarget(f.R("synthetic_certificate_index"))))}:m).ToImmutableArray();
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,members,false,PpaInstrument.Implementation);
            var inputs=f.Prepare(market,[instrument]);
            var values=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,new(f.From,f.From.AddHours(1)),discount));
            var attributes=values.Rows.Where(r=>r.Slot.Role is PpaRole.CertificateDelivery or PpaRole.CertificatePayment).ToArray();
            if(attributes.Length!=2 || attributes.Select(r=>r.Amount).Aggregate((a,b)=>a.Add(b)).Numerator!="0")
                throw new Exception("Matching indexed certificate legs must offset at the selected vintage mark.");
        });
        Check("profile quantity cannot borrow a self-consistent different reference revision",()=>{
            try
            {
            var other=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
            ReferenceKey R(string code)=>new(other,code);
            var energy=new QuantitySpecification(R("mwh"),R("power"),R("electricity"),R("megawatt_hour"),R("energy"));
            var capacity=new QuantitySpecification(R("mw"),R("power"),R("electricity"),R("megawatt"),R("power"));
            var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(new ProfileQuantity(capacity.Key,energy.Key,f.Profile.Hours)),f.Asset.Location);
            var payment=new FixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)])),new FlatPrice(new(default,f.EnergyPrice)));
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
                [new(new(PpaRole.EnergyDelivery),physical,ExactDecimal.Parse("1")),new(new(PpaRole.StrikePayment),payment,ExactDecimal.Parse("-1"))],false,PpaInstrument.Implementation);
            var original=f.Prepare(market,[f.Physical()]);
            var reference=original.Reference with {Quantities=original.Reference.Quantities.Add(energy.Key,energy).Add(capacity.Key,capacity)};
                IntervalValuationInputs.Prepare(reference,original.Read,[instrument],market.Build,original.KnownAt,original.KnowledgeContext,IntervalKnowledgePolicy.ResolvedCutoff,original.Policy,"ppa_coverage_v1",[],Full(instrument));
            }
            catch(MarketDataException e) when(e.Code=="ppa_reference_revision"){return;}
            throw new Exception("Expected ppa_reference_revision for quantities outside the prepared reference pin.");
        });
        Check("payment calendar must retain the instrument reference revision",()=>{
            var original=f.Physical();
            var calendar=new PaymentCalendar(new(new(Guid.NewGuid(),Guid.NewGuid()),"other_calendar"),
                DateSet.Create(new(2031,1,1),new(2033,1,1),[new(2031,12,10)]));
            var rule=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DayOfFollowingMonth,null,10,null,ShortMonth.Reject,DateRoll.Following,calendar,PaymentRule.Implementation);
            var payment=(FixedSide)original.Memberships[1].Side;
            var members=original.Memberships.SetItem(1,original.Memberships[1] with {Side=new FixedSide(Guid.NewGuid(),payment.Terms with {DateRule=rule},payment.Price)});
            try {var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,members,false,PpaInstrument.Implementation);f.Prepare(market,[instrument]);}
            catch(MarketDataException e) when(e.Code=="ppa_reference_revision"){return;}
            throw new Exception("Expected ppa_reference_revision for a payment calendar outside the instrument pin.");
        });
        Check("missing prepared dictionaries refuse through the structured context boundary",()=>{
            var instrument=f.Physical();var original=f.Prepare(market,[instrument]);
            void Refuse(IntervalReferenceData reference,IntervalReadData read)
            {
                try {IntervalValuationInputs.Prepare(reference,read,[instrument],market.Build,original.KnownAt,original.KnowledgeContext,IntervalKnowledgePolicy.ResolvedCutoff,original.Policy,"ppa_coverage_v1",[],Full(instrument));}
                catch(MarketDataException e) when(e.Code=="ppa_context"){return;}
                throw new Exception("Expected ppa_context for missing immutable input dictionaries.");
            }
            Refuse(original.Reference,original.Read with {Curves=null!});
            Refuse(original.Reference with {Quantities=null!},original.Read);
            Refuse(original.Reference,original.Read with {Series=null!});
        });
        Check("a null scenario refuses through the structured context boundary",()=>{
            var inputs=f.Prepare(market,[f.Physical()]);
            try {inputs.Session(null!);}
            catch(MarketDataException e) when(e.Code=="ppa_context"){return;}
            throw new Exception("Expected ppa_context for a missing scenario.");
        });
        Check("a flat price requires an explicit nonnull quote",()=>{
            try {var side=new FixedSide(Guid.NewGuid(),f.Terms(f.Profile),new FlatPrice(null!));_=side.Price.Unit;}
            catch(MarketDataException e) when(e.Code=="ppa_price_rule"){return;}
            throw new Exception("Expected ppa_price_rule for a missing flat price.");
        });
        Check("interactive and returned-row admission honors exact Q8 bounds",()=>{
            IntervalValuationSession.RequireWork(2_000_000,35_136);
            foreach(var counts in new[]{(2_000_001L,0L),(0L,35_137L),(-1L,0L),(0L,-1L)})
            {
                try {IntervalValuationSession.RequireWork(counts.Item1,counts.Item2);}
                catch(MarketDataException e) when(e.Code=="ppa_size"){continue;}
                throw new Exception("Expected ppa_size outside the admitted work bounds.");
            }
        });
        Check("a fixed charge must bind its explicit energy delivery quantity",()=>{
            var original=f.Physical();var charge=new FixedSide(Guid.NewGuid(),f.Terms(f.Profile),new FlatPrice(new(ExactDecimal.Parse("2"),f.EnergyPrice)));
            try {_=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,original.Memberships.Add(new(new(PpaRole.FixedCharge),charge,ExactDecimal.Parse("-1"))),false,PpaInstrument.Implementation);}
            catch(MarketDataException e) when(e.Code=="ppa_quantity_reference"){return;}
            throw new Exception("Expected ppa_quantity_reference for a charge with an unlinked quantity source.");
        });
        Check("unknown versions and temporal policies are refused explicitly",()=>{
            void Refuse(string code,Action action)
            {try {action();}catch(MarketDataException e) when(e.Code==code){return;}throw new Exception("Expected "+code);}
            var instrument=f.Physical();var original=f.Prepare(market,[instrument]);
            Refuse("ppa_term_unsupported",()=>new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,instrument.Memberships,false,"unknown_implementation"));
            Refuse("ppa_term_unsupported",()=>new CapacityDeliverySide(Guid.NewGuid(),f.Terms(new AccreditedQuantity([f.Asset.Key])),f.CapacityDays.Key,"unknown_capacity_rule"));
            Refuse("ppa_term_unsupported",()=>new CertificateDeliverySide(Guid.NewGuid(),f.Terms(f.Volume),f.Certificates.Key,ExactDecimal.Parse("1"),"unknown_eligibility","held_at_current_vintage_mark_v1","none_recorded_v1"));
            Refuse("ppa_context",()=>IntervalValuationInputs.Prepare(original.Reference,original.Read,[instrument],market.Build,original.KnownAt,original.KnowledgeContext,IntervalKnowledgePolicy.ResolvedCutoff,(PendingActualsPolicy)999,"ppa_coverage_v1",[],Full(instrument)));
            Refuse("ppa_context",()=>IntervalValuationInputs.Prepare(original.Reference,original.Read,[instrument],market.Build,original.KnownAt,original.KnowledgeContext,IntervalKnowledgePolicy.ResolvedCutoff,original.Policy,"unknown_coverage",[],Full(instrument)));
            Refuse("ppa_context",()=>IntervalValuationInputs.Prepare(original.Reference,original.Read,[instrument],market.Build,original.KnownAt.AddTicks(1),original.KnowledgeContext,IntervalKnowledgePolicy.ResolvedCutoff,original.Policy,"ppa_coverage_v1",[],Full(instrument)));
            try {new EscalatedPrice(new(ExactDecimal.Parse("40"),f.EnergyPrice),ExactDecimal.Parse("0.02"),f.Month,(ContractYearBasis)999).At(f.Month);}
            catch(TemporalException e) when(e.Code=="contract_year_basis"){return;}
            throw new Exception("Expected explicit contract_year_basis refusal for an unknown convention.");
        });
        Check("a selected off-hour has explicit zero peak-profile delivery and payment",()=>{
            var original=f.Physical();var physical=(PowerPhysicalSide)original.Memberships[0].Side;
            var peak=f.Profile with {Hours=new(BlockMembership.Peak,new(7,23,false),[])};
            var schedule=f.Schedule(peak);var cash=(FixedSide)original.Memberships[1].Side;
            var members=original.Memberships.SetItem(0,original.Memberships[0] with {
                Side=new PowerPhysicalSide(Guid.NewGuid(),physical.Terms with {Quantity=peak,Schedule=schedule},physical.Location)})
                .SetItem(1,original.Memberships[1] with {Side=new FixedSide(Guid.NewGuid(),cash.Terms with {Schedule=schedule,
                    InputPorts=[..cash.Terms.InputPorts.Select(p=>p with{Schedule=schedule})]},cash.Price)});
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,members,false,PpaInstrument.Implementation);
            var sunday=f.Hours.First(h=>h.OperatingDate.DayOfWeek==DayOfWeek.Sunday);
            var coverage=new DeliveryCoverage(sunday.Start,sunday.End);
            var inputs=f.Prepare(market,[instrument],coverage:ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>>.Empty.Add(instrument.Id,[coverage]));
            var value=ValueMeasures.From(inputs.Session(market.Build).Value(instrument,coverage,discount));
            if(!value.Rows.IsEmpty || value.ValueExcludingOverdue.Numerator!="0")
                throw new Exception("The explicit peak mask has no selected Sunday quantity or payment.");
        });
        return count;
    }
}
