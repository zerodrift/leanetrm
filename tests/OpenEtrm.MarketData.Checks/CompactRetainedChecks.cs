using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class CompactRetainedChecks
{
    internal static int Run()
    {
        RoundtripEconomics();MonthScopedPreparation();LogicalIdentity();MalformedGeometry();
        Console.WriteLine("PASS compact retained blocks: exact loaded base/up/down rows, physical/linked/certificate/capacity/overdue measures, own-cell identity and structured geometry refusals");
        return 4;
    }
    internal static ScheduleDefinition Reload(ScheduleDefinition definition)
    {
        T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,ScheduleDefinition.Json),ScheduleDefinition.Json)!;
        var loaded=new ScheduleDefinition(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
            definition.SchemaVersion,definition.GeneratorVersion,Copy(definition.Rows.ToArray()),Copy(definition.Edges.ToArray()),
            Copy(definition.Bindings.ToArray()),Copy(definition.Roots.ToArray()),Copy(definition.Ports.ToArray()),Copy(definition.Grids.ToArray()));
        Equal(loaded.Manifest,definition.Manifest,"Typed compact artifact roundtrip preserves its complete manifest");return loaded;
    }
    private static PpaInstrument Retain(PpaInstrument instrument)=>RetainedSideSchedules.Attach(instrument,
        instrument.Sides.ToDictionary(member=>member.Side.Id,member=>Reload(ScheduleDefinitionCompiler.Generate(member.Side))));
    private static void RoundtripEconomics()
    {
        var fixture=new PpaAcceptanceFixture(15);var market=fixture.Market(fixture.From.AddDays(-1));
        var scenarios=market.Model.Scenarios(market.Build,[market.Bumps["synthetic_energy"]]).Single();
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var physical=fixture.Physical();
        var floating=new FloatingSide(Guid.NewGuid(),physical.Memberships[1].Side.Terms,
            new(fixture.C("synthetic_energy"),ExactDecimal.Parse("2"),new(ExactDecimal.Parse("1"),fixture.EnergyPrice),null));
        var indexed=new PpaInstrument(Guid.NewGuid(),fixture.Definitions,fixture.Reference,
            [physical.Memberships[0],new(new(PpaRole.IndexedPayment),floating,ExactDecimal.Parse("-1"))],false,PpaInstrument.Implementation);
        foreach(var(original,expected)in new[]{(fixture.Physical(),"1442"),(fixture.Physical(volume:true),"360.5"),
            (fixture.WithCertificate(),"7931"),(fixture.CapacityAgreement("2"),"48"),(indexed,"-31003")})
        {
            var loaded=Retain(original);var first=fixture.Prepare(market,[original]);var second=fixture.Prepare(market,[loaded]);
            foreach(var scenario in new[]{market.Build,scenarios.Up,scenarios.Down})
            {
                var draft=ValueMeasures.From(first.Session(scenario).Value(original,new(fixture.From,fixture.To),discount));
                var retained=ValueMeasures.From(second.Session(scenario).Value(loaded,new(fixture.From,fixture.To),discount));
                Equal(FinancialRows(retained),FinancialRows(draft),"SQL-shaped typed roundtrip retains every value and classification under the same immutable scenario");
                if(ReferenceEquals(scenario,market.Build))Equal(retained.ValueExcludingOverdue.ToExactString(),expected,"Independent whole-month base PV");
            }
        }
        var overdue=fixture.WithCertificate();var copy=Retain(overdue);
        var selected=fixture.Market(fixture.To.AddDays(9),actuals:_=>"40");
        var meter=fixture.Meter(interval=>interval.Start==fixture.From?"50":"0");
        var originalInputs=fixture.Prepare(selected,[overdue],meter);var loadedInputs=fixture.Prepare(selected,[copy],meter);
        var changed=selected.Model.Scenarios(selected.Build,[selected.Bumps["synthetic_certificate"]]).Single();
        foreach(var scenario in new[]{selected.Build,changed.Up,changed.Down})
        {
            var draft=ValueMeasures.From(originalInputs.Session(scenario).Value(overdue,new(fixture.From,fixture.To),discount));
            var retained=ValueMeasures.From(loadedInputs.Session(scenario).Value(copy,new(fixture.From,fixture.To),discount));
            Equal(FinancialRows(retained),FinancialRows(draft),"Held certificates and overdue cash retain their distinct loaded scopes");
            Equal(retained.OverdueKnownBalance.ToExactString(),"-0.75","Paid-date boundary keeps overdue certificate cash outside PV");
            if(ReferenceEquals(scenario,selected.Build))Equal(retained.ValueExcludingOverdue.ToExactString(),"1.25","Known generated certificate holding remains valued after payment is due");
        }
    }
    private static string FinancialRows(ValueMeasureSet value)=>JsonSerializer.Serialize(new{
        Primary=value.ValueExcludingOverdue.ToExactString(),Known=value.OverdueKnownBalance.ToExactString(),Estimated=value.OverdueEstimated.ToExactString(),value.HasOverdue,
        Rows=value.Rows.Select(row=>new{row.Slot,row.Kind,row.Interval,Quantity=row.Quantity.Numerator+"/"+row.Quantity.Denominator,row.Quantity.Specification,
            row.QuantityState,row.Stage,row.Class,row.Coverage,row.Lifecycle,row.ValueAt,row.DateRule,row.UnavailableReason,
            Amount=row.Amount.ToExactString(),Discount=row.Discount?.DiscountFactor.ToString(),Pv=row.PresentValue?.ToExactString(),
            Prices=row.PriceComponents.Select(price=>new{Value=price.Price.ToExactString(),price.Price.Unit,price.State,price.Curve,price.Multiplier,price.Adder,
                Quantity=price.Quantity.Numerator+"/"+price.Quantity.Denominator})})},ScheduleDefinition.Json);
    private static void MonthScopedPreparation()
    {
        var f=new PpaAcceptanceFixture(15);var original=f.Physical(volume:true);
        PpaSideTerms Extend(PpaSideTerms terms)
        {
            var delivery=terms.Delivery with{From=f.Month.AddMonths(-1)};
            var schedule=new IntervalScheduleParameters(delivery,f.Clock,terms.Schedule.Cadence,terms.Schedule.Hours);
            return terms with{Delivery=delivery,Schedule=schedule,InputPorts=[..terms.InputPorts.Select(port=>port with{Schedule=schedule})]};
        }
        var physical=(PowerPhysicalSide)original.Memberships[0].Side;var fixedSide=(FixedSide)original.Memberships[1].Side;
        var draft=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,
            [original.Memberships[0] with{Side=new PowerPhysicalSide(Guid.NewGuid(),Extend(physical.Terms),physical.Location)},
             original.Memberships[1] with{Side=new FixedSide(Guid.NewGuid(),Extend(fixedSide.Terms),fixedSide.Price)}],false,PpaInstrument.Implementation);
        var retained=Retain(draft);var span=new DeliveryCoverage(f.From.AddHours(25),f.From.AddHours(27));
        var coverage=ImmutableDictionary<Guid,ImmutableArray<DeliveryCoverage>>.Empty.Add(draft.Id,[span]);
        var cutoff=f.From.AddDays(-1);var forecast=f.Forecast();
        var history=forecast with{Cells=forecast.Cells.AddRange(forecast.Cells.Select(cell=>
            cell with{Value=ExactDecimal.Parse("0.5"),Revision=2,KnownAt=cutoff.AddMinutes(30)}))};
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        foreach(var(known,expected)in new[]{(cutoff,"1"),(cutoff.AddHours(1),"2"),(cutoff,"1")})
        {
            var market=f.Market(cutoff,known);var shocks=market.Model.Scenarios(market.Build,[market.Bumps["synthetic_energy"]]).Single();
            var draftInputs=f.Prepare(market,[draft],forecastOverride:history,coverage:coverage);
            var retainedInputs=f.Prepare(market,[retained],forecastOverride:history,coverage:coverage);
            foreach(var scenario in new[]{market.Build,shocks.Up,shocks.Down})
            {
                var first=ValueMeasures.From(draftInputs.Session(scenario).Value(draft,span,discount));
                var second=ValueMeasures.From(retainedInputs.Session(scenario).Value(retained,span,discount));
                Equal(FinancialRows(second),FinancialRows(first),"A partial month of a multi-month retained term preserves exact source revisions and scenario rows");
                if(second.Rows.Length!=16||second.Rows.Any(row=>row.Interval.Start<span.From||row.Interval.End>span.To))
                    throw new Exception("Two repeated-hour windows contain eight quarter-hour cells per side, without reading the uncovered month.");
                if(ReferenceEquals(scenario,market.Build))Equal(second.ValueExcludingOverdue.ToExactString(),expected,
                    "The two-hour 0.25 forecast gives PV1; only the later eligible 0.5 correction gives PV2");
            }
        }
    }
    private static void LogicalIdentity()
    {
        var f=new PpaAcceptanceFixture();var original=f.Physical();var first=Retain(original);
        var oldPayment=(FixedSide)original.Memberships[1].Side;
        var stepped=new FixedSide(Guid.NewGuid(),oldPayment.Terms,new SteppedPrice([
            new(f.Month,new(ExactDecimal.Parse("40"),f.EnergyPrice)),new(f.Month.AddDays(15),new(ExactDecimal.Parse("41"),f.EnergyPrice))]));
        var amended=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[original.Memberships[0],original.Memberships[1] with{Side=stepped}],false,PpaInstrument.Implementation);
        var second=Retain(amended);var firstRows=first.Memberships[1].Side.GenerateSchedule().Rows;var secondRows=second.Memberships[1].Side.GenerateSchedule().Rows;
        var before=firstRows[0].RetainedReference!.Value;var after=secondRows[0].RetainedReference!.Value;
        if(before.CellOrdinal is null||before.Obligation!=after.Obligation||before.Manifest==after.Manifest)
            throw new Exception("A later strike change may repartition the stored graph but cannot change an earlier cell's own occurrence or terms identity.");
        if(firstRows[1].RetainedReference!.Value.Obligation.OccurrenceKey==before.Obligation.OccurrenceKey)
            throw new Exception("Different retained interval cells must have distinct own-coordinate obligation identities even when they share a payment and root.");
        var late=Enumerable.Range(0,firstRows.Count).First(n=>firstRows[n].Interval!.OperatingDate==f.Month.AddDays(15));
        if(firstRows[late].RetainedReference!.Value.Obligation.TermsDigest==secondRows[late].RetainedReference!.Value.Obligation.TermsDigest)
            throw new Exception("Changing a retained cell's strike must change that cell's own terms digest.");
        var capacity=f.CapacityAgreement();var capacitySide=capacity.Memberships[0].Side;
        var definition=ScheduleDefinitionCompiler.Generate(capacitySide);var output=definition.Roots.Single(root=>root.Measure=="contribution_pv").Row;
        var shifted=new ScheduleDefinition(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
            definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,definition.Bindings,definition.Roots,definition.Ports,
            [..definition.Grids.Select(grid=>grid.FirstRowOrdinal is null?grid with{Origin=grid.Origin.AddHours(1)}:grid)]);
        if(definition.ObligationIdentity(output,0).TermsDigest==shifted.ObligationIdentity(output,0).TermsDigest)
            throw new Exception("Changing an accredited-capacity input window must change own terms even when its source, day count and output month are unchanged.");
        Reject(()=>RetainedSideSchedules.Attach(capacity,capacity.Sides.ToDictionary(member=>member.Side.Id,
            member=>member.Side.Id==capacitySide.Id?shifted:ScheduleDefinitionCompiler.Generate(member.Side))));
    }
    private static void MalformedGeometry()
    {
        var f=new PpaAcceptanceFixture();var side=f.Physical().Memberships[0].Side;var original=ScheduleDefinitionCompiler.Generate(side);
        ScheduleDefinition Rewrite(ScheduleDefinitionGrid changed)=>new(original.SideId,original.Family,original.EconomicIdentity,original.PricingBindingIdentity,
            original.SchemaVersion,original.GeneratorVersion,original.Rows,original.Edges,original.Bindings,original.Roots,original.Ports,[changed]);
        var grid=original.Grids.Single();
        Reject(()=>Rewrite(grid with{Month=new(9999,12,1)}));
        Reject(()=>Rewrite(grid with{StepTicks=long.MaxValue-7}));
        Reject(()=>Rewrite(grid with{Selection=grid.Selection.SetItem(1,grid.Selection[0])}));
        Reject(()=>new ScheduleDefinition(original.SideId,original.Family,original.EconomicIdentity,original.PricingBindingIdentity,
            "side_schedule_rows_v1","whole_term_side_schedule_v1",original.Rows,original.Edges,original.Bindings,original.Roots,original.Ports,original.Grids));
        var reference=Retain(f.Physical()).Memberships[0].Side.GenerateSchedule().Rows[0].RetainedReference!.Value;
        Reject(()=>reference.Definition.ObligationIdentity(reference.RootOrdinal,null));
        var wrongLabels=f.Physical();
        Reject(()=>RetainedSideSchedules.Attach(wrongLabels,wrongLabels.Sides.ToDictionary(member=>member.Side.Id,member=>
        {
            var definition=ScheduleDefinitionCompiler.Generate(member.Side);
            return new ScheduleDefinition(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
                definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,definition.Bindings,definition.Roots,definition.Ports,
                [..definition.Grids.Select(axis=>axis with{Clock=f.R("different_unselected_clock")})]);
        })));
        Reject(()=>RetainedSideSchedules.Attach(wrongLabels,wrongLabels.Sides.ToDictionary(member=>member.Side.Id,member=>
        {
            var definition=ScheduleDefinitionCompiler.Generate(member.Side);
            return new ScheduleDefinition(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
                definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,definition.Bindings,definition.Roots,definition.Ports,
                [..definition.Grids.Select(axis=>axis with{LocalMinutes=axis.LocalMinutes.SetItem(0,60)})]);
        })));
    }
    private static void Reject(Action run)
    {try{run();}catch(MarketDataException e)when(e.Code=="side_schedule_definition"){return;}throw new Exception("Expected structured side_schedule_definition refusal.");}
    private static void Equal(string actual,string expected,string label){if(actual!=expected)throw new Exception(label+": expected "+expected+"; got "+actual);}
}
