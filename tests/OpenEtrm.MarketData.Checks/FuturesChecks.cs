using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class FuturesChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine($"PASS futures: {name}"); }
        void Equal<T>(T actual, T expected) where T : notnull
        { if (!actual.Equals(expected)) throw new Exception($"Expected {expected}; got {actual}."); }
        void Reject(string code, Action test)
        { try { test(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception($"Expected {code}."); }
        var f = new Fixture(); var contract = f.Contracts[0]; var quote = f.Quotes[0];
        var first = Fixture.Utc("2040-01-01T00:00:00Z");
        var series = new FuturesSeriesSpecification(f.R("future_jan"), quote.Quantity, quote.Currency,
            quote.Multiplier, quote.TradingTick, f.R("location"), f.R("uniform"), contract.DeliveryFrom,
            contract.DeliveryTo, contract.Expiry, quote.Quantity, ExactDecimal.Parse("40"), LotBasis.FixedTotal, first,"cash");
        FuturesMarketBinding Side(FuturesSeriesSpecification? terms = null, string settlement = "cash") =>
            new(Guid.NewGuid(), new(f.C("future"), f.K(1)), contract, terms ?? series, quote, settlement, FuturesMarketBinding.Implementation);
        var side = Side(); var instrument = side;
        var bookA = Guid.NewGuid(); var bookB = Guid.NewGuid();
        Trade Trade(Guid book, string lots, string execution, DateTimeOffset? at = null) => new(Guid.NewGuid(), book,
            [FuturesExecution.Bind(Guid.NewGuid(), instrument, ExactDecimal.Parse(lots), new UnitPrice(ExactDecimal.Parse(execution), quote),
                at ?? f.Snapshot().ValuationAt, FuturesSettlementState.Unprocessed)]);
        var trades = ImmutableArray.Create(Trade(bookA, "2", "3"), Trade(bookA, "1", "3.05"), Trade(bookB, "-1", "3.1"));
        MarketSnapshot Mark(string price) => f.Snapshot() with { Selections = f.Snapshot().Selections.Select(s => s.Input.Series == f.S(1)
            ? s with { Observation = s.Observation with { Value = ExactDecimal.Parse(price) } } : s).ToImmutableArray() };
        ImmutableArray<FuturesHolding> Price(ImmutableArray<Trade> holdings, MarketSnapshot snapshot) =>
            FuturesHoldings.Price(holdings, snapshot, f.Model().Build(snapshot, [side.Market]), FuturesMarketBinding.Implementation);
        var snapshot = Mark("3.2");
        Check("nested futures risk counts original and intermediate market preparation",()=>
        {
            var bump=new QuoteBump(new(f.S(1),InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),ExactDecimal.Parse("0.1"),QuoteBump.Convention);
            var shift=new QuoteShift(bump.Input,bump.Quote,bump.Unit,ExactDecimal.Parse("0.2"),QuoteShift.Convention);
            var result=FuturesRisk.Calculate(trades,snapshot,f.Model(),[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy,
                stress:[shift,shift with{Size=ExactDecimal.Parse("-0.05")}]);
            // One leaf market, built once originally, once per stress layer and twice for risk.
            Equal(result.Work.RebuiltCurveNodes,5);Equal(result.Work.ReusedCurveNodes,0);
            Equal(result.Work.ScenarioCount,3);Equal(result.Work.UnitPricerCalls,3);
            Equal(result.Work.PreparationScenarioCount,2);
        });
        Check("a simple future is a leaf instrument with pricing selected outside its identity", () =>
        {
            Instrument common = instrument.Instrument;
            Equal(common is SidedInstrument, false);
            Equal(common.Id, ContractIdentity.Futures(series.Key));
            Equal(instrument.Market, side.Market);
        });
        Check("unknown first-trading time requires explicit eligibility outside instrument identity",()=>
        {
            var terms=series with{FirstTradingAt=null};var shared=new FuturesContract(terms);
            Reject("futures_definition",()=>new FuturesMarketBinding(Guid.NewGuid(),side.Market,contract,terms,quote,"cash",FuturesMarketBinding.Implementation,shared));
            var bound=new FuturesMarketBinding(Guid.NewGuid(),side.Market,contract,terms,quote,"cash",FuturesMarketBinding.Implementation,shared,eligibleFrom:first);
            var later=new FuturesMarketBinding(Guid.NewGuid(),side.Market,contract,terms,quote,"cash",FuturesMarketBinding.Implementation,shared,eligibleFrom:first.AddDays(1));
            Equal(bound.EligibleFrom,first);Equal(later.EligibleFrom,first.AddDays(1));
            Equal(ReferenceEquals(bound.Instrument,later.Instrument),true);
            Equal(bound.Instrument.Id,ContractIdentity.Futures(series.Key));
            Reject("futures_definition",()=>new FuturesMarketBinding(Guid.NewGuid(),side.Market,contract,terms,quote,"cash",FuturesMarketBinding.Implementation,eligibleFrom:contract.Expiry));
            Reject("futures_definition",()=>new FuturesMarketBinding(Guid.NewGuid(),side.Market,contract,terms,quote,"cash",FuturesMarketBinding.Implementation,eligibleFrom:first.ToOffset(TimeSpan.FromHours(1))));
            Reject("futures_definition",()=>new FuturesMarketBinding(Guid.NewGuid(),side.Market,contract,series,quote,"cash",FuturesMarketBinding.Implementation,eligibleFrom:first.AddTicks(-10)));
            var price=new UnitPrice(ExactDecimal.Parse("3"),quote);
            _=FuturesExecution.Bind(Guid.NewGuid(),bound,ExactDecimal.Parse("1"),price,first,FuturesSettlementState.Unprocessed);
            Reject("futures_execution",()=>FuturesExecution.Bind(Guid.NewGuid(),bound,ExactDecimal.Parse("1"),price,first.AddTicks(-10),FuturesSettlementState.Unprocessed));
        });
        Check("unit side contribution, instrument lot size and booked lots apply once at their own levels", () =>
        {
            Equal(instrument.UnitQuantity.Amount.ToString(), "1");
            Equal(instrument.LotSize.Amount.ToString(), "40");
            Equal(side.UnitQuantity.Amount.ToString(), "1");
            var mark = new UnitPrice(ExactDecimal.Parse("3.2"), quote);
            var execution = new UnitPrice(ExactDecimal.Parse("3"), quote);
            Equal(side.PriceChange(mark, execution).Value.ToString(), "0.2");
            Equal(instrument.UnitExecutionPnl(mark, execution).Scale(ExactDecimal.Parse("-2")).Amount.ToString(), "-16");
            Equal(instrument.ExecutionPnl(mark, execution, ExactDecimal.Parse("-2")).Amount.ToString(), "-16");
        });
        Check("futures definitions reject incomplete dates references and quote policies before pricing", () =>
        {
            Reject("futures_definition", () => Side(series with { Location = f.R("location") with { Revision = default } }));
            Reject("futures_definition", () => Side(series with { DeliveryProfile = default }));
            Reject("futures_definition", () => Side(series with { TradingTick = default }));
            Reject("futures_definition", () => Side(series with { FirstTradingAt = contract.Expiry }));
            Reject("futures_definition", () => Side(series with { FirstTradingAt = first.AddTicks(1) }));
            Reject("futures_definition", () => Side(series with { FirstTradingAt = first.ToOffset(TimeSpan.FromHours(1)) }));
            Reject("futures_definition", () => new FuturesMarketBinding(Guid.NewGuid(), side.Market, contract, series, quote with { ClearingIncrement = default }, "cash", FuturesMarketBinding.Implementation));
            Reject("futures_definition", () => new FuturesMarketBinding(Guid.NewGuid(), side.Market, contract, series, quote with { Minimum = ExactDecimal.Parse("1001") }, "cash", FuturesMarketBinding.Implementation));
            var reversed = contract with { DeliveryTo = contract.DeliveryFrom };
            Reject("futures_definition", () => new FuturesMarketBinding(Guid.NewGuid(), side.Market, reversed, series with { DeliveryTo = reversed.DeliveryTo }, quote, "cash", FuturesMarketBinding.Implementation));
        });
        Check("standalone holdings and risk reject terms contradicting the market model's exact definition", () =>
        {
            var bump = new QuoteBump(new(f.S(1), InputUse.Variable), quote.Key, PriceUnit.FromQuote(quote), ExactDecimal.Parse("0.1"), QuoteBump.Convention);
            foreach (var altered in new[]
            {
                Side(series with { ContractSize = ExactDecimal.Parse("80") }),
                Side(series with { FirstTradingAt = first.AddYears(-1) }),
                new FuturesMarketBinding(Guid.NewGuid(), side.Market, contract, series, quote with { Key = f.R("another_quote") }, "cash", FuturesMarketBinding.Implementation)
            })
            {
                var held = altered;
                var trade = new Trade(Guid.NewGuid(), bookA, [FuturesExecution.Bind(Guid.NewGuid(), held, ExactDecimal.Parse("1"), new UnitPrice(ExactDecimal.Parse("3"), altered.Quote), snapshot.ValuationAt, FuturesSettlementState.Unprocessed)]);
                Reject("futures_definition", () => Price([trade], snapshot));
                Reject("futures_definition", () => FuturesRisk.Calculate([trade], snapshot, f.Model(), [bump], FuturesRisk.Implementation, RiskEngine.NumericalPolicy));
            }
        });
        Check("negative futures prices and shocks through zero preserve long and short economics", () =>
        {
            var held = ImmutableArray.Create(Trade(bookA, "2", "0.025"), Trade(bookB, "-1", "-0.025"));
            var negative = Mark("-0.003");
            Equal(string.Join(",", Price(held, negative).Select(h => h.ExecutionPnl.Amount)), "-2.24,-0.88");
            var bump = new QuoteBump(new(f.S(1), InputUse.Variable), quote.Key, PriceUnit.FromQuote(quote), ExactDecimal.Parse("0.01"), QuoteBump.Convention);
            var risk = FuturesRisk.Calculate(held, negative, f.Model(), [bump], FuturesRisk.Implementation, RiskEngine.NumericalPolicy);
            var a = risk.Factors.Single().Totals.Single(t => t.BookId == bookA);
            var b = risk.Factors.Single().Totals.Single(t => t.BookId == bookB);
            Equal(a.Base.Amount.ToString(), "-2.24"); Equal(a.UpPnl.Amount.ToString(), "0.8");
            Equal(b.Base.Amount.ToString(), "-0.88"); Equal(b.UpPnl.Amount.ToString(), "-0.4");
            Equal(a.Gamma.Value.ToString(), "0"); Equal(b.Delta.Value.ToString(), "-40");
        });
        Check("settlement marks follow clearing precision independently of execution ticks", () =>
        {
            Equal(Price([Trade(bookA, "2", "3.2")], Mark("3.201"))[0].ExecutionPnl.Amount.ToString(), "0.08");
            Reject("futures_execution", () => Trade(bookA, "1", "3.201"));
            Reject("clearing_precision", () => side.PriceChange(new UnitPrice(ExactDecimal.Parse("3.2005"), quote), new UnitPrice(ExactDecimal.Parse("3.2"), quote)));
        });
        Check("fractional lot size preserves representable final P&L without rounding a per-lot intermediate", () =>
        {
            var fine = quote with { TradingTick = ExactDecimal.Parse("0.000000000000000001"), ClearingIncrement = ExactDecimal.Parse("0.000000000000000001") };
            var terms = series with { ContractSize = ExactDecimal.Parse("0.5"), TradingTick = fine.TradingTick };
            var fractional = new FuturesMarketBinding(Guid.NewGuid(), side.Market, contract, terms, fine, "cash", FuturesMarketBinding.Implementation);
            var held = fractional;
            var trade = new Trade(Guid.NewGuid(), bookA, [FuturesExecution.Bind(Guid.NewGuid(), held, ExactDecimal.Parse("2"), new UnitPrice(ExactDecimal.Parse("3"), fine), snapshot.ValuationAt, FuturesSettlementState.Unprocessed)]);
            var selected = Mark("3.000000000000000001") with { Selections = [Mark("3.000000000000000001").Selections.Single(s => s.Input.Series == f.S(1))] };
            var model = new CurveModel(f.Revision, f.Reference, [f.Quantities[0]], [fine], [contract], [f.Curves[0]], [terms], [], [new(f.S(1), contract.Key, fine.Key, null)]);
            var holding = FuturesHoldings.Price([trade], selected, model.Build(selected, [side.Market]), FuturesMarketBinding.Implementation).Single();
            Equal(holding.ExecutionPnl.Amount.ToString(), "0.000000000000000001"); Equal(holding.Quantity.Amount.ToString(), "1");
            var bump = new QuoteBump(new(f.S(1), InputUse.Variable), fine.Key, PriceUnit.FromQuote(fine), ExactDecimal.Parse("0.000000000000000001"), QuoteBump.Convention);
            var risk = FuturesRisk.Calculate([trade], selected, model, [bump], FuturesRisk.Implementation, RiskEngine.NumericalPolicy).Factors.Single().Positions.Single();
            Equal(risk.Base, holding.ExecutionPnl); Equal(risk.Delta.Value.ToString(), "1");
        });
        Check("risk preserves sub-decimal unit cash until the final position quantity is applied", () =>
        {
            var fine = quote with { Multiplier = ExactDecimal.Parse("0.01"), TradingTick = ExactDecimal.Parse("0.000000000000000001"), ClearingIncrement = ExactDecimal.Parse("0.000000000000000001") };
            var terms = series with { ContractSize = ExactDecimal.Parse("100"), TradingTick = fine.TradingTick, Multiplier = fine.Multiplier };
            var scaled = new FuturesMarketBinding(Guid.NewGuid(), side.Market, contract, terms, fine, "cash", FuturesMarketBinding.Implementation);
            var held = scaled;
            var trade = new Trade(Guid.NewGuid(), bookA, [FuturesExecution.Bind(Guid.NewGuid(), held, ExactDecimal.Parse("1"), new UnitPrice(default, fine), snapshot.ValuationAt, FuturesSettlementState.Unprocessed)]);
            var selected = Mark("0.000000000000000001");
            selected = selected with { Selections = [selected.Selections.Single(s => s.Input.Series == f.S(1))] };
            var model = new CurveModel(f.Revision, f.Reference, [f.Quantities[0]], [fine], [contract], [f.Curves[0]], [terms], [], [new(f.S(1), contract.Key, fine.Key, null)]);
            Equal(FuturesHoldings.Price([trade], selected, model.Build(selected, [side.Market]), FuturesMarketBinding.Implementation).Single().ExecutionPnl.Amount.ToString(), "0.000000000000000001");
            var bump = new QuoteBump(new(f.S(1), InputUse.Variable), fine.Key, PriceUnit.FromQuote(fine), ExactDecimal.Parse("0.000000000000000001"), QuoteBump.Convention);
            var result=FuturesRisk.Calculate([trade],selected,model,[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy).Factors.Single().Positions.Single();
            Equal(result.Base.Amount.ToString(),"0.000000000000000001");Equal(result.UpPnl.Amount.ToString(),"0.000000000000000001");Equal(result.Delta.Value.ToString(),"1");Equal(result.Gamma.Value.ToString(),"0");
        });
        Check("null futures pricing bindings produce structured errors", () =>
        {
            Reject("futures_execution", () => FuturesExecution.Bind(Guid.NewGuid(), null!, ExactDecimal.Parse("1"), new UnitPrice(ExactDecimal.Parse("3"), quote), snapshot.ValuationAt, FuturesSettlementState.Unprocessed));
            Reject("futures_execution", () => FuturesExecution.Bind(Guid.NewGuid(), instrument, ExactDecimal.Parse("1"), null!, snapshot.ValuationAt, FuturesSettlementState.Unprocessed));
            Reject("trade_definition", () => new Trade(Guid.NewGuid(), bookA, [null!]));
            Reject("futures_context", () => Price([null!], snapshot));
            Reject("risk_position", () => FuturesRisk.Calculate([null!], snapshot, f.Model(), [], FuturesRisk.Implementation, RiskEngine.NumericalPolicy));
        });
        Check("futures risk reuses one unit across books and retains independent execution bases",()=>
        {
            var bump=new QuoteBump(new(f.S(1),InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),ExactDecimal.Parse("0.1"),QuoteBump.Convention);
            var run=FuturesRisk.Calculate(trades,snapshot,f.Model(),[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy);
            var a=run.Factors.Single().Totals.Single(t=>t.BookId==bookA);var b=run.Factors.Single().Totals.Single(t=>t.BookId==bookB);
            Equal(a.Base.Amount.ToString(),"22");Equal(a.UpPnl.Amount.ToString(),"12");Equal(a.Delta.Value.ToString(),"120");
            Equal(b.Base.Amount.ToString(),"-4");Equal(b.UpPnl.Amount.ToString(),"-4");
            Equal(run.Work.UniqueValuations,1);Equal(run.Work.UnitPricerCalls,3);
            var markets=f.Model().BuildScenarios(snapshot,[side.Market],[bump]);
            Reject("futures_context",()=>FuturesHoldings.Price(trades,snapshot,markets.Factors[0].Up,FuturesMarketBinding.Implementation));
        });
        Check("risk subtracts execution basis before scaling a large supported quantity",()=>
        {
            var terms=series with{ContractSize=ExactDecimal.Parse("100000000000000000000000")};
            var largeSide=Side(terms);var largeInstrument=largeSide;
            var trade=new Trade(Guid.NewGuid(),bookA,[FuturesExecution.Bind(Guid.NewGuid(),largeInstrument,ExactDecimal.Parse("1"),new UnitPrice(ExactDecimal.Parse("998.975"),quote),snapshot.ValuationAt,FuturesSettlementState.Unprocessed)]);
            var selected=Mark("999");
            var bump=new QuoteBump(new(f.S(1),InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),ExactDecimal.Parse("0.1"),QuoteBump.Convention);
            // Keep this direct-contract fixture independent of unused derived definitions.
            var largeModel=new CurveModel(f.Revision,f.Reference,[f.Quantities[0]],[quote],[contract],[f.Curves[0]],[terms],[],[new(f.S(1),contract.Key,quote.Key,null)]);
            selected=selected with{Selections=[selected.Selections.Single(s=>s.Input.Series==f.S(1))]};
            var result=FuturesRisk.Calculate([trade],selected,largeModel,[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy).Factors[0].Positions.Single();
            Equal(result.Base.Amount.ToString(),"2500000000000000000000");Equal(result.Delta.Value.ToString(),"100000000000000000000000");
            Equal(result.UpPnl.Amount.ToString(),"10000000000000000000000");
        });
        Check("three obligations in two books share one instrument, side and curve contract", () =>
        {
            var result = Price(trades, snapshot);
            Equal(result.Length, 3); Equal(result.Select(r => r.InstrumentId).Distinct().Count(), 1);
            Equal(result.Select(r => r.PricingBindingId).Distinct().Count(), 1); Equal(result.Select(r => r.Contract).Distinct().Count(), 1);
            Equal(result.Select(r => r.BindingId).Distinct().Count(), 3); Equal(result.Select(r => r.BookId).Distinct().Count(), 2);
            Equal(result[0].Quantity.Amount.ToString(), "80"); Equal(result[2].Quantity.Amount.ToString(), "-40");
            Equal(result[0].Quantity.Specification, quote.Quantity);
            Equal(string.Join(",", result.Select(r => r.ExecutionPnl.Amount)), "16,6,-4");
            Equal(result.Select(r => r.ExecutionPnl).Aggregate((a,b) => a.Add(b)).Amount.ToString(), "18");
            Equal(result.All(r => r.Settlement == FuturesSettlementState.Unprocessed), true);
            Equal(ReferenceEquals(result[0].Mark, result[2].Mark), true);
        });
        Check("one changed pinned contract mark reprices every holding without changing execution history", () =>
        {
            var changed = Price(trades, Mark("3.3"));
            Equal(string.Join(",", changed.Select(r => r.ExecutionPnl.Amount)), "24,10,-8");
            Equal(changed.Select(r => r.ExecutionPnl).Aggregate((a,b) => a.Add(b)).Amount.ToString(), "26");
            Equal(Price(trades, snapshot)[0].ExecutionPnl.Amount.ToString(), "16");
            Equal(FuturesExecution.Require(trades[0].Instruments[0]).ExecutionPrice.Value.ToString(), "3");
        });
        Check("lots must be explicit nonzero signed integers within the supported bound", () =>
        {
            foreach (var lots in new[] { "0", "0.5", "100001", "-100001" }) Reject("futures_lots", () => Trade(bookA, lots, "3"));
        });
        Check("missing risk holdings raise a structured error",()=>
        {
            Reject("risk_position",()=>FuturesRisk.Calculate(default,snapshot,f.Model(),[],FuturesRisk.Implementation,RiskEngine.NumericalPolicy));
        });
        Check("execution price uses the contract trading tick without implicit rounding", () =>
            Reject("futures_execution", () => Trade(bookA, "1", "3.001")));
        Check("fixed-total cash terms and identical lot/quote bases are required", () =>
        {
            Reject("futures_convention", () => Side(series with { LotBasis = LotBasis.PerDeliveryDay }));
            Reject("futures_convention", () => Side(series with { LotQuantity = f.R("power_quantity") }));
            Reject("futures_convention", () => Side(settlement: "physical"));
        });
        Check("first trading is inclusive and expiry is exclusive for execution and valuation", () =>
        {
            _ = Trade(bookA, "1", "3", first);
            Reject("futures_execution", () => Trade(bookA, "1", "3", first.AddTicks(-1)));
            Reject("futures_execution", () => Trade(bookA, "1", "3", contract.Expiry));
            var expired = snapshot with { ValuationAt = contract.Expiry, MarketCutoff = contract.Expiry };
            Reject("source_expired", () => Price(trades, expired));
        });
        Check("a later same-day execution cannot enter an earlier valuation", () =>
            Reject("futures_effective", () => Price([Trade(bookA, "1", "3", snapshot.ValuationAt.AddMinutes(1))], snapshot)));
        Check("snapshot identity and convention are explicit calculation inputs", () =>
        {
            var build = f.Model().Build(snapshot, [side.Market]);
            Reject("futures_context", () => FuturesHoldings.Price(trades, snapshot with { Id = Guid.NewGuid() }, build, FuturesMarketBinding.Implementation));
            Reject("futures_convention", () => FuturesHoldings.Price(trades, snapshot, build, ""));
        });
        Check("reusing a snapshot name cannot reinterpret retained price inputs or valuation time", () =>
        {
            var build = f.Model().Build(snapshot, [side.Market]);
            Reject("futures_context", () => FuturesHoldings.Price(trades, snapshot with { ValuationAt = snapshot.ValuationAt.AddMinutes(1) }, build, FuturesMarketBinding.Implementation));
            Reject("futures_context", () => FuturesHoldings.Price(trades, snapshot with { MarketCutoff = snapshot.MarketCutoff.AddMinutes(-1) }, build, FuturesMarketBinding.Implementation));
            Reject("futures_context", () => FuturesHoldings.Price(trades, Mark("3.3") with { Id = snapshot.Id }, build, FuturesMarketBinding.Implementation));
            Equal(FuturesHoldings.Price(trades, snapshot with { Selections = snapshot.Selections.Reverse().ToImmutableArray() }, build, FuturesMarketBinding.Implementation)[0].ExecutionPnl.Amount.ToString(), "16");
        });
        Check("reusable definitions reject empty contract and version identities", () =>
        {
            var empty = contract with { Key = new(default, Guid.Empty) };
            Reject("futures_definition", () => new FuturesMarketBinding(Guid.NewGuid(), new(new(default, "future"), empty.Key), empty,
                series, quote, "cash", FuturesMarketBinding.Implementation));
        });
        Check("binding occurrence identities are scoped to their own trade", () =>
        {
            var prior=trades[1].Instruments[0];
            var reused=FuturesExecution.Bind(trades[0].Instruments[0].Id,instrument,FuturesExecution.Require(prior).Lots(prior.Quantity),FuturesExecution.Require(prior).ExecutionPrice,FuturesExecution.Require(prior).ExecutedAt,FuturesExecution.Require(prior).Settlement);
            var result=Price([trades[0],new Trade(Guid.NewGuid(),bookB,[reused])],snapshot);
            Equal(result.Length,2); Equal(result[0].BindingId,result[1].BindingId);
            Equal(result[0].ExecutionPnl.Amount.ToString(),"16"); Equal(result[1].ExecutionPnl.Amount.ToString(),"6");
        });
        Check("zero execution P&L leaves the open quantity and unprocessed settlement intact", () =>
        {
            var result=Price([Trade(bookA,"2","3.2")],snapshot)[0];
            Equal(result.ExecutionPnl.Amount.ToString(),"0"); Equal(result.Quantity.Amount.ToString(),"80");
            Equal(result.Settlement,FuturesSettlementState.Unprocessed);
        });
        Check("quoted cents apply their explicit multiplier exactly once to execution P&L", () =>
        {
            var cents=quote with { Multiplier=ExactDecimal.Parse("0.01") };
            var terms=series with { Multiplier=cents.Multiplier };
            var centsSide=new FuturesMarketBinding(Guid.NewGuid(),side.Market,contract,terms,cents,"cash",FuturesMarketBinding.Implementation);
            var centsInstrument=centsSide;
            var position=FuturesExecution.Bind(Guid.NewGuid(),centsInstrument,ExactDecimal.Parse("2"),new UnitPrice(ExactDecimal.Parse("300"),cents),snapshot.ValuationAt,FuturesSettlementState.Unprocessed);
            var selected=snapshot.Selections.Single(s=>s.Input.Series==f.S(1));
            var inputs=snapshot with { Selections=[selected with { Observation=selected.Observation with { Value=ExactDecimal.Parse("320") } }] };
            var centsModel=new CurveModel(f.Revision,f.Reference,[f.Quantities[0]],[cents],[contract],[f.Curves[0]],[terms],[],[new(f.S(1),contract.Key,cents.Key,null)]);
            var result=FuturesHoldings.Price([new Trade(Guid.NewGuid(),bookA,[position])],inputs,centsModel.Build(inputs,[side.Market]),FuturesMarketBinding.Implementation);
            Equal(result[0].ExecutionPnl.Amount.ToString(),"16");
            var bump=new QuoteBump(new(f.S(1),InputUse.Variable),cents.Key,PriceUnit.FromQuote(cents),ExactDecimal.Parse("1"),QuoteBump.Convention);
            var risk=FuturesRisk.Calculate([new Trade(Guid.NewGuid(),bookA,[position])],inputs,centsModel,[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy).Factors[0].Positions.Single();
            Equal(risk.UpPnl.Amount.ToString(),"0.8");Equal(risk.Delta.Value.ToString(),"0.8");Equal(risk.Delta.Unit.Factor.Multiplier.ToString(),"0.01");
        });
        Check("shared pricing binding identities cannot carry conflicting immutable economics", () =>
        {
            var changed=new FuturesMarketBinding(side.Id,side.Market,contract,series with { ContractSize=ExactDecimal.Parse("80") },quote,"cash",FuturesMarketBinding.Implementation);
            var alternate=changed;
            var position=FuturesExecution.Bind(Guid.NewGuid(),alternate,ExactDecimal.Parse("1"),new UnitPrice(ExactDecimal.Parse("3"),quote),snapshot.ValuationAt,FuturesSettlementState.Unprocessed);
            Reject("futures_identity",()=>Price([trades[0],new(Guid.NewGuid(),bookB,[position])],snapshot));
        });
        Check("contract identities retain exact terms across independently rebuilt definitions", () =>
        {
            var rebuilt=new FuturesMarketBinding(side.Id,side.Market,contract,series,quote,"cash",FuturesMarketBinding.Implementation);
            Trade Using(FuturesMarketBinding member)=>new(Guid.NewGuid(),bookB,[FuturesExecution.Bind(Guid.NewGuid(),member,ExactDecimal.Parse("1"),new UnitPrice(ExactDecimal.Parse("3"),quote),snapshot.ValuationAt,FuturesSettlementState.Unprocessed)]);
            Equal(Price([trades[0],Using(rebuilt)],snapshot).Length,2);
            Equal(Price([trades[0],Using(Side())],snapshot).Length,2);
        });
        Check("missing settlement state reject", () =>
        {
            Reject("futures_settlement",()=>FuturesExecution.Bind(Guid.NewGuid(),instrument,ExactDecimal.Parse("1"),new UnitPrice(ExactDecimal.Parse("3"),quote),snapshot.ValuationAt,0));
        });
        var portfolio=Guid.NewGuid();
        var entries=Price(trades,snapshot).Select(h=>new FuturesBlotterEntry(portfolio,h)).ToImmutableArray();
        Check("pivot sums exact quantities and money per book and dated contract",()=>
        {
            var groups=FuturesBlotter.Pivot(entries,[BlotterDimension.Book,BlotterDimension.Contract],[BlotterMeasure.TradeCount,BlotterMeasure.Lots,BlotterMeasure.Quantity,BlotterMeasure.ExecutionPnl],FuturesBlotter.Implementation);
            var a=groups.Single(g=>g.Key.BookId==bookA);var b=groups.Single(g=>g.Key.BookId==bookB);
            Equal(a.TradeCount!.Value,2);Equal(a.Lots!.Value.ToString(),"3");Equal(a.Quantity!.Amount.ToString(),"120");Equal(a.ExecutionPnl!.Amount.ToString(),"22");
            Equal(b.Lots!.Value.ToString(),"-1");Equal(b.Quantity!.Amount.ToString(),"-40");Equal(b.ExecutionPnl!.Amount.ToString(),"-4");
            Equal(a.Key.Contract!.Value,contract.Key);
        });
        Check("portfolio pivot keeps zero net positions and independent execution economics",()=>
        {
            var balanced=Price([Trade(bookA,"1","3"),Trade(bookB,"-1","3.1")],snapshot).Select(h=>new FuturesBlotterEntry(portfolio,h)).ToImmutableArray();
            var group=FuturesBlotter.Pivot(balanced,[BlotterDimension.Portfolio],[BlotterMeasure.TradeCount,BlotterMeasure.Quantity,BlotterMeasure.ExecutionPnl],FuturesBlotter.Implementation).Single();
            Equal(group.TradeCount!.Value,2);Equal(group.Quantity!.Amount.ToString(),"0");Equal(group.ExecutionPnl!.Amount.ToString(),"4");Equal(group.Lots is null,true);
        });
        Check("pivot partitions unlike quantity specifications and currencies rather than adding them",()=>
        {
            var h=entries[0].Holding;var other=h with {TradeId=Guid.NewGuid(),Quantity=new Quantity(h.Quantity.Amount,f.R("other_quantity")),ExecutionPnl=new Money(h.ExecutionPnl.Amount,f.R("other_currency"))};
            var groups=FuturesBlotter.Pivot([entries[0],new(portfolio,other)],[BlotterDimension.Portfolio],[BlotterMeasure.Quantity,BlotterMeasure.ExecutionPnl],FuturesBlotter.Implementation);
            Equal(groups.Length,2);Equal(groups.Select(g=>g.Key.Currency).Distinct().Count(),2);Equal(groups.Select(g=>g.Key.Quantity).Distinct().Count(),2);
        });
        Check("pivot requires explicit unique dimensions measures and a supported version",()=>
        {
            Reject("blotter_view",()=>FuturesBlotter.Pivot(entries,[],[BlotterMeasure.Quantity],FuturesBlotter.Implementation));
            Reject("blotter_view",()=>FuturesBlotter.Pivot(entries,[BlotterDimension.Book],[],FuturesBlotter.Implementation));
            Reject("blotter_view",()=>FuturesBlotter.Pivot(entries,[BlotterDimension.Book,BlotterDimension.Book],[BlotterMeasure.Quantity],FuturesBlotter.Implementation));
            Reject("blotter_view",()=>FuturesBlotter.Pivot(entries,[BlotterDimension.Book],[BlotterMeasure.Lots],FuturesBlotter.Implementation));
            Reject("blotter_view",()=>FuturesBlotter.Pivot(entries,[BlotterDimension.Book],[BlotterMeasure.Quantity],""));
            Reject("blotter_view",()=>FuturesBlotter.Pivot([entries[0],entries[0]],[BlotterDimension.Book],[BlotterMeasure.Quantity],FuturesBlotter.Implementation));
        });
        Check("pivot trade count counts a multi-instrument trade once",()=>
        {
            var combined=new Trade(Guid.NewGuid(),bookA,[trades[0].Instruments[0],trades[1].Instruments[0]]);
            var bindings=Price([combined],snapshot).Select(h=>new FuturesBlotterEntry(portfolio,h)).ToImmutableArray();
            Equal(FuturesBlotter.Pivot(bindings,[BlotterDimension.Book],[BlotterMeasure.TradeCount],FuturesBlotter.Implementation).Single().TradeCount!.Value,1);
        });
        Check("pivot results are independent of supplied trade enumeration",()=>
        {
            var a=FuturesBlotter.Pivot(entries,[BlotterDimension.Book],[BlotterMeasure.ExecutionPnl],FuturesBlotter.Implementation);
            var b=FuturesBlotter.Pivot(entries.Reverse().ToImmutableArray(),[BlotterDimension.Book],[BlotterMeasure.ExecutionPnl],FuturesBlotter.Implementation);
            Equal(string.Join(",",a.Select(g=>g.ExecutionPnl!.Amount)),string.Join(",",b.Select(g=>g.ExecutionPnl!.Amount)));
        });
        Check("generic side and instrument PV cannot substitute execution P&L for futures settlement state", () =>
        {
            var context = new SideValuationContext(f.Snapshot(), new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted));
            Reject("futures_pv_unsupported", () => instrument.Instrument.PV(context));
        });
        Check("book-only futures risk nets exact cash before bounded holding output",()=>
        {
            var wide=quote with{Maximum=ExactDecimal.Parse("999999999999999999999999")};
            var bound=new FuturesMarketBinding(Guid.NewGuid(),side.Market,contract,series,wide,"cash",FuturesMarketBinding.Implementation);
            var inputs=Mark("900000000000000000000000");
            var model=new CurveModel(f.Revision,f.Reference,[f.Quantities[0]],[wide],[contract],[f.Curves[0]],[series],[],[new(f.S(1),contract.Key,wide.Key,null)]);
            inputs=inputs with{Selections=[inputs.Selections.Single(s=>s.Input.Series==f.S(1))]};
            Trade Offset(string lots)=>new(Guid.NewGuid(),bookA,[FuturesExecution.Bind(Guid.NewGuid(),bound,ExactDecimal.Parse(lots),new UnitPrice(default,wide),inputs.ValuationAt,FuturesSettlementState.Unprocessed)]);
            var held=ImmutableArray.Create(Offset("1"),Offset("-1"));
            var bump=new QuoteBump(new(f.S(1),InputUse.Variable),wide.Key,PriceUnit.FromQuote(wide),ExactDecimal.Parse("0.01"),QuoteBump.Convention);
            var risk=FuturesRisk.Calculate(held,inputs,model,[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy,detail:RiskDetail.Books);
            var total=risk.Factors[0].Totals.Single();
            Equal(total.Base.Amount.ToString(),"0");Equal(total.Delta.Value.ToString(),"0");Equal(total.Gamma.Value.ToString(),"0");
            Reject("numeric_range",()=>FuturesRisk.Calculate(held,inputs,model,[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy));
        });
        Console.WriteLine($"{count} futures checks passed."); return count;
    }
}
