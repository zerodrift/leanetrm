using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class ContractIdentityChecks
{
    public static int Run()
    {
        var f = new Fixture();
        var model = f.Model();
        var contract = model.FuturesContract(f.Contracts[0].Key);
        if (contract is not Instrument || contract.SeriesKey != f.Contracts[0].FuturesSeries)
            throw new Exception("A curve's future must resolve to the exact series instrument.");
        if (!ReferenceEquals(contract, model.FuturesContract(f.Contracts[0].Key)))
            throw new Exception("A curve lookup must reuse its immutable contract.");
        if (contract.EconomicIdentity != DefinitionIdentity.For(contract) || contract.LotSize.Amount.ToString() != "40")
            throw new Exception("The exact contract key must determine lot terms.");
        if (!ReferenceEquals(contract, model.FuturesContract(contract.SeriesKey)))
            throw new Exception("Primary-key and constituent lookups must return the same contract.");
        var q = f.Quotes[0];
        CurveModel Model(DefinitionRevision revision, string curveCode, QuoteSpecification quote)
        {
            var member = f.Contracts[0] with {Key = new(revision, Guid.NewGuid())};
            var curve = f.Curves[0] with {Key = new(revision,curveCode), Constituents = [member.Key], Quote = quote.Key};
            return new(revision,f.Reference,[f.Quantities[0]],[quote],[member],[curve],[contract.Series],[],
                [new(new(revision,Guid.NewGuid()),member.Key,quote.Key,null)]);
        }
        var other = Model(new(f.Revision.CatalogId,Guid.NewGuid()),"other_recipe",q);
        var revised = other.FuturesContract(contract.SeriesKey);
        if (revised.Id != contract.Id || revised.EconomicIdentity != contract.EconomicIdentity)
            throw new Exception("Changing a curve recipe or its constituent alias must not change the future.");
        var changedPolicy = Model(new(f.Revision.CatalogId,Guid.NewGuid()),"other_policy",q with {ClearingIncrement=ExactDecimal.Parse("0.0001")});
        if (changedPolicy.FuturesContract(contract.SeriesKey).EconomicIdentity != contract.EconomicIdentity)
            throw new Exception("Quote-admission policy belongs to the explicit pricing binding.");
        var missing = new ReferenceKey(contract.SeriesKey.Revision,"unknown_series");
        try { model.FuturesContract(missing); throw new Exception("Missing key was accepted."); }
        catch (MarketDataException e) when(e.Code=="futures_definition") { }
        var goldenKey = new ReferenceKey(new(Guid.Parse("11111111-1111-1111-1111-111111111111"),Guid.Parse("22222222-2222-2222-2222-222222222222")),"synthetic_jan");
        if(ContractIdentity.Futures(goldenKey)!=Guid.Parse("d0ee21d0-047f-5450-926e-89a2679c7a94"))
            throw new Exception("Canonical key encoding or UUID byte order changed.");
        foreach(var invalid in new[] {contract.Series with {Multiplier=default},contract.Series with {TradingTick=default},
            contract.Series with {Currency=new(contract.SeriesKey.Revision,"")},contract.Series with {SettlementMethod="unsupported"}})
        {
            try { _=new FuturesContract(invalid); throw new Exception("Incomplete contract terms were accepted."); }
            catch(MarketDataException e)when(e.Code=="futures_definition") { }
        }
        var member=f.Contracts[0];var terms=contract.Series;
        var alternateQuote=q with {Key=f.R("alternate_quote"),ClearingIncrement=ExactDecimal.Parse("0.0001")};
        var alternateCurve=f.Curves[0] with {Key=f.C("alternate_curve"),Quote=alternateQuote.Key};
        var alternateSeries=new SeriesKey(f.Revision,Guid.NewGuid());
        var dual=new CurveModel(f.Revision,f.Reference,[f.Quantities[0]],[q,alternateQuote],[member],
            [f.Curves[0],alternateCurve],[terms],[],[new(f.S(1),member.Key,q.Key,null),new(alternateSeries,member.Key,alternateQuote.Key,null)]);
        var a=dual.BindFutures(Guid.NewGuid(),new(f.C("future"),member.Key),"cash",FuturesMarketBinding.Implementation);
        var b=dual.BindFutures(Guid.NewGuid(),new(alternateCurve.Key,member.Key),"cash",FuturesMarketBinding.Implementation);
        if(!ReferenceEquals(a.Instrument,b.Instrument))throw new Exception("Pricing bindings duplicated their future.");
        var selected=f.Snapshot().Selections.Single(s=>s.Input.Series==f.S(1));
        var snapshot=f.Snapshot() with {Selections=[selected with {Observation=selected.Observation with {Value=ExactDecimal.Parse("3.2")}},
            new(new(alternateSeries,InputUse.Variable),selected.Observation with {Key=new(f.Revision.CatalogId,Guid.NewGuid()),Series=alternateSeries,Value=ExactDecimal.Parse("4")})]};
        var book=Guid.NewGuid();
        TradeInstrument Position(FuturesMarketBinding binding,Guid? alias=null)=>FuturesExecution.Bind(Guid.NewGuid(),binding,ExactDecimal.Parse("1"),new(ExactDecimal.Parse("3"),q),snapshot.ValuationAt,FuturesSettlementState.Unprocessed,alias);
        var trades=ImmutableArray.Create(new Trade(Guid.NewGuid(),book,[Position(a)]),new Trade(Guid.NewGuid(),book,[Position(b)]));
        var holdings=FuturesHoldings.Price(trades,snapshot,dual.Build(snapshot,[a.Market,b.Market]),FuturesMarketBinding.Implementation);
        if(string.Join(",",holdings.Select(h=>h.ExecutionPnl.Amount))!="8,40")throw new Exception("One contract's distinct explicit pricing bindings were conflated.");
        var bump=new QuoteBump(new(f.S(1),InputUse.Variable),q.Key,PriceUnit.FromQuote(q),ExactDecimal.Parse("0.1"),QuoteBump.Convention);
        try{FuturesRisk.Calculate(trades,snapshot,dual,[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy);throw new Exception("Conflicting prices for one instrument/context were accepted.");}
        catch(MarketDataException e)when(e.Code=="valuation_binding"){}
        var run=FuturesRisk.Calculate([trades[1]],snapshot,dual,[bump],FuturesRisk.Implementation,RiskEngine.NumericalPolicy);
        if(run.Work.UniqueValuations!=1||run.Factors[0].Positions.Single().UpPnl.Amount.ToString()!="0")
            throw new Exception("A separately selected pricing context must retain its exact independent factor.");
        try{Position(a,Guid.Empty);throw new Exception("Empty retained alias accepted.");}catch(MarketDataException e)when(e.Code=="futures_execution"){}
        var otherTerms=terms with {Key=f.R("other_future")};
        var otherMember=member with {FuturesSeries=otherTerms.Key};
        var otherBinding=new FuturesMarketBinding(Guid.NewGuid(),a.Market,otherMember,otherTerms,q,"cash",FuturesMarketBinding.Implementation);
        var conflicting=ImmutableArray.Create(trades[0],new Trade(Guid.NewGuid(),book,[Position(otherBinding,a.Instrument.Id)]));
        try{FuturesHoldings.Price(conflicting,snapshot,dual.Build(snapshot,[a.Market]),FuturesMarketBinding.Implementation);throw new Exception("Conflicting retained alias accepted.");}
        catch(MarketDataException e)when(e.Code=="futures_identity"){}
        Console.WriteLine("PASS contract identity: curve futures resolve the shared leaf instrument by exact series key before booking");
        Console.WriteLine("PASS contract identity: quote policies, bound risk work, exact terms and retained aliases remain distinct");
        return 2;
    }
}
