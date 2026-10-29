using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

public enum ValuationMeasure { PresentValue=1, ExecutionPnl }

// Operation-owned immutable inputs. A missing discount provider is explicitly
// unavailable, never an instruction to substitute undiscounted cashflows.
public sealed record ValuationSetup(ValuationMeasure Measure,
    ImmutableDictionary<Guid,FuturesMarketBinding> FuturesBindings,ISideDiscounting? Discounting)
{
    // Providers belong to instruments: two instruments can share the same side
    // while selecting different retained discounting terms.
    public IntervalValuationInputs? Interval{get;init;}
    public ImmutableDictionary<Guid,ISideDiscounting> InstrumentDiscounting{get;init;}=ImmutableDictionary<Guid,ISideDiscounting>.Empty;
    public ImmutableDictionary<MeasureBinding,SwapBindingValuation> Settlements{get;init;}=ImmutableDictionary<MeasureBinding,SwapBindingValuation>.Empty;
}

// Operation/scenario-local reuse. Reference identity of the resolved discount
// provider is intentional; equal-looking providers do not establish equal pins.
internal sealed class SideValuationContexts
{
    private sealed record Key(ISideDiscounting? Provider,ImmutableHashSet<CurveRequest> Dependencies);
    private sealed class Keys:IEqualityComparer<Key>
    {
        public bool Equals(Key? a,Key? b)=>ReferenceEquals(a,b)||a is not null&&b is not null&&ReferenceEquals(a.Provider,b.Provider)&&a.Dependencies.SetEquals(b.Dependencies);
        public int GetHashCode(Key key)
        {
            var hash=key.Provider is null?0:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Provider);
            foreach(var dependency in key.Dependencies)hash^=dependency.GetHashCode();
            return HashCode.Combine(hash,key.Dependencies.Count);
        }
    }
    private readonly Dictionary<Key,SideValuationContext> contexts=new(new Keys());
    internal int Calculations=>contexts.Values.Sum(c=>c.EvaluatedSides);
    internal SideValuationContext Get(ISideDiscounting? provider,ImmutableArray<CurveRequest> dependencies,Func<SideValuationContext> create)
    {
        var key=new Key(provider,dependencies.ToImmutableHashSet());
        if(!contexts.TryGetValue(key,out var context)){context=create();contexts.Add(key,context);}
        return context;
    }
}

public sealed class UnitValuationContext
{
    internal CurveBuild? Market=>pricing?.Authority;
    internal ValuationSetup Setup {get;}
    internal MarketSnapshot Snapshot {get;}
    private readonly PreparedValuationMarket? pricing;
    private readonly UnitPricingContract? declaration;
    private readonly CancellationToken cancellation;
    internal CancellationToken Cancellation=>cancellation;
    private readonly SideValuationContexts sideContexts;
    internal SwapBindingValuation? Settlement{get;}
    internal int SideCalculations=>sideContexts.Calculations;
    public PricingInputs Inputs=>declaration is {} contract&&pricing is {} market?new(market,contract,cancellation):
        throw new MarketDataException("risk_dependency","A unit valuation requires its declared market inputs.");
    public ValuationMeasure Measure=>Setup.Measure;
    public DeliveryCoverage? Coverage{get;}
    internal ValueMeasureSet? Measures{get;private set;}
    internal void RecordMeasures(ValueMeasureSet measures)
    {
        if(Measures is not null || declaration is null || measures.Currency!=declaration.Currency)
            throw new MarketDataException("valuation_measures","Companion measures require one exact declared unit valuation.");
        Measures=measures;
    }
    public IntervalValuationSession? IntervalSession{get;}
    public ISideDiscounting DiscountProvider=>Setup.Discounting??
        (declaration is not null?Setup.InstrumentDiscounting.GetValueOrDefault(declaration.Id):null)??
        throw new MarketDataException("side_context","Supply this instrument's explicit discount inputs.");
    internal UnitValuationContext(MarketSnapshot snapshot,ValuationSetup setup,PreparedValuationMarket? market=null,UnitPricingContract? declaration=null,CancellationToken cancellation=default,DeliveryCoverage? coverage=null,IntervalValuationSession? intervalSession=null,
        SideValuationContexts? sideContexts=null,SwapBindingValuation? settlement=null)
    {
        if(market is not null&&declaration is null&&(setup.Discounting??setup.InstrumentDiscounting.Values.FirstOrDefault()) is {} provider)
            SideValuationContext.Validate(snapshot,provider);
        Snapshot=snapshot;Setup=setup;Coverage=coverage;IntervalSession=intervalSession;pricing=market;this.declaration=declaration;this.cancellation=cancellation;
        this.sideContexts=sideContexts??new();Settlement=settlement;
    }
    internal UnitValuationContext For(UnitPricingContract contract,DeliveryCoverage? coverage=null,SwapBindingValuation? settlement=null)=>new(Snapshot,Setup,pricing,contract,cancellation,coverage,IntervalSession,sideContexts,settlement);
    internal UnitValuationContext ForCoverage(DeliveryCoverage? coverage)=>new(Snapshot,Setup,pricing,declaration,cancellation,coverage,IntervalSession);
    public ImmutableArray<QuoteFactor> ResolveFactors(ImmutableArray<CurveRequest> dependencies)
    {
        if(declaration is null||pricing is null||dependencies.IsDefault||dependencies.Any(d=>!declaration.Dependencies.Contains(d)))
            throw new MarketDataException("risk_dependency","Factor resolution requires this instrument's exact declared inputs.");
        return pricing.Factors(dependencies,cancellation);
    }
    internal FuturesMarketBinding Futures(Guid instrument)
    {
        if(!Setup.FuturesBindings.TryGetValue(instrument,out var binding)||binding.Instrument.Id!=instrument)
            throw new MarketDataException("valuation_binding","An exact futures pricing binding is required for this instrument.");
        if(pricing is not null&&pricing.Authority is null)
            throw new MarketDataException("valuation_binding","Futures pricing requires its exact qualified market definition authority.");
        return binding;
    }
    internal SideValuationContext SideContext(ISideDiscounting? selected=null)
    {
        if(pricing is null||declaration is null)
            throw new MarketDataException("side_context","Supply the declared market and explicit payment discount inputs.");
        var provider=selected??Setup.Discounting??Setup.InstrumentDiscounting.GetValueOrDefault(declaration.Id);
        return sideContexts.Get(provider,declaration.Dependencies,()=>pricing.Context(provider,declaration.Dependencies,cancellation));
    }
}

public sealed record BookRiskFactors(Guid BookId,ImmutableArray<QuoteFactor> Factors);

public sealed record BookCurrencyValue(Guid BookId,ReferenceKey Currency,ExactMoney Value);
public sealed record MeasureBinding(Guid TradeId,Guid BindingId);
public sealed record BookValueMeasures(Guid BookId,ReferenceKey Currency,ValueMeasureSet Measures,
    ImmutableArray<MeasureBinding> Bindings);
public sealed class PortfolioValue
{
    public ImmutableArray<ExactMoney> Positions{get;}
    public ImmutableArray<BookCurrencyValue> Totals{get;}
    public ImmutableArray<ExactMoney> UnitValues{get;}
    // Companion measures retain the base context even when this result is a shocked PV.
    public ImmutableArray<BookValueMeasures> BaseMeasures{get;}
    public int UnitValuations{get;}
    public int QuantityApplications{get;}
    public int SideCalculations{get;}
    public IntervalValuationSession? IntervalSession{get;}
    internal ValuationPortfolio Owner{get;}
    internal PreparedValuationMarket Market{get;}
    internal ImmutableArray<UnitPricingContract> Declarations{get;}
    internal PortfolioValue(ValuationPortfolio owner,PreparedValuationMarket market,ImmutableArray<UnitPricingContract> declarations,
        ImmutableArray<ExactMoney> positions,ImmutableArray<BookCurrencyValue> totals,ImmutableArray<ExactMoney> units,int calls,int quantityApplications,IntervalValuationSession? intervalSession,ImmutableArray<BookValueMeasures> baseMeasures,int sideCalculations)
    {Owner=owner;Market=market;Declarations=declarations;Positions=positions;Totals=totals;UnitValues=units;UnitValuations=calls;QuantityApplications=quantityApplications;IntervalSession=intervalSession;BaseMeasures=baseMeasures;SideCalculations=sideCalculations;}
}

// This is a resolved, authorized valuation projection of the stored trade ->
// instrument/quantity bindings. It creates no alternative persisted trade model.
public sealed class Book
{
    public Guid Id{get;}
    private readonly ImmutableArray<(int Position,int Unit)> bindings;
    private sealed record NetHolding(int Unit,ExactQuantity Quantity,ExactMoney Basis);
    private readonly ImmutableArray<NetHolding> net;
    internal Book(Guid id,ImmutableArray<(int Position,int Unit)> bindings,ImmutableArray<RiskPosition> positions)
    {
        Id=id;this.bindings=bindings;
        net=bindings.GroupBy(b=>b.Unit).Select(group=>
        {
            var rows=group.Select(b=>positions[b.Position]).ToArray();
            var quantity=rows.Select(p=>ExactQuantity.FromQuantity(p.Quantity)).Aggregate((a,b)=>a.Add(b));
            var basis=ExactMoney.FromMoney(new(default,rows[0].FixedBasis.Currency));
            foreach(var p in rows)
            {
                if(p.UnitBasis.Money.Numerator!="0")basis=basis.Add(p.UnitBasis.Money.Scale(p.Quantity.Amount));
                if(p.FixedBasis.Amount!=default)basis=basis.Add(ExactMoney.FromMoney(p.FixedBasis));
            }
            return new NetHolding(group.Key,quantity,basis);
        }).ToImmutableArray();
    }
    internal int PV(ImmutableArray<RiskPosition> positions,ImmutableArray<ExactMoney> units,
        PortfolioValue? prior,ExactMoney[] results,Dictionary<(Guid,ReferenceKey),ExactMoney> totals,CancellationToken ct)
    {
        var applications=0;
        if(results.Length==0)
        {
            foreach(var holding in net)
            {
                ct.ThrowIfCancellationRequested();var unit=units[holding.Unit];var key=(Id,unit.Currency);
                if(prior is not null&&ReferenceEquals(unit,prior.UnitValues[holding.Unit]))continue;
                var cash=prior is null?unit:unit.Subtract(prior.UnitValues[holding.Unit]);
                var change=cash.Scale(holding.Quantity,holding.Quantity.Specification);
                if(prior is null)change=change.Subtract(holding.Basis);
                totals[key]=totals.TryGetValue(key,out var total)?total.Add(change):change;applications++;
            }
            return applications;
        }
        foreach(var (index,unit) in bindings)
        {
            ct.ThrowIfCancellationRequested();
            var p=positions[index];var key=(Id,p.FixedBasis.Currency);
            if(prior is not null&&ReferenceEquals(units[unit],prior.UnitValues[unit]))
            {results[index]=prior.Positions[index];continue;}
            var value=units[unit].Subtract(p.UnitBasis.Money).Scale(p.Quantity.Amount).Subtract(ExactMoney.FromMoney(p.FixedBasis));
            results[index]=value;applications++;
            var change=prior is null?value:value.Subtract(prior.Positions[index]);
            totals[key]=totals.TryGetValue(key,out var total)?total.Add(change):change;
        }
        return applications;
    }
}

public sealed record UnitValuationScope(Instrument Instrument,DeliveryCoverage? Coverage,MeasureBinding? Settlement=null);

public sealed class ValuationPortfolio
{
    public static ValuationPortfolio FromTrades(ImmutableArray<Trade> trades,ValuationSetup setup)
    {
        if(setup is null)throw Error("valuation_measure","Select an explicit valuation measure and its inputs.");
        if(trades.IsDefaultOrEmpty||trades.Any(t=>t is null)||trades.Select(t=>t.Id).Distinct().Count()!=trades.Length)
            throw Error("trade_definition","Select distinct explicit trades for valuation.");
        var instruments=new Dictionary<Guid,Instrument>();
        var positions=ImmutableArray.CreateBuilder<RiskPosition>();
        foreach(var trade in trades)foreach(var binding in trade.Instruments)
        {
            var instrument=binding.Instrument;
            if(instruments.TryGetValue(instrument.Id,out var prior)&&!ReferenceEquals(prior,instrument))prior.RequireSameDefinition(instrument);
            instruments.TryAdd(instrument.Id,instrument);
            if(setup.Measure==ValuationMeasure.ExecutionPnl&&binding.Execution is null)
                throw Error("valuation_measure","Execution P&L requires explicit execution facts for every trade binding.");
            var basis=binding.Execution?.Basis(instrument)??new UnitMoney(new Money(default,instrument.Currency),binding.Quantity.Specification);
            positions.Add(new(trade.Id,binding.Id,trade.BookId,instrument.Id,binding.Quantity,basis,new Money(default,instrument.Currency)){Coverage=binding.Coverage});
        }
        return new([..instruments.Values],positions.ToImmutable(),setup);
    }
    public ImmutableArray<Instrument> Instruments{get;}
    public ImmutableArray<UnitValuationScope> Scopes{get;}
    public ImmutableArray<RiskPosition> Positions{get;}
    public ImmutableArray<Book> Books{get;}
    public ValuationSetup Setup{get;}
    private readonly Dictionary<Guid,int> indexes;
    public ValuationPortfolio(ImmutableArray<Instrument> instruments,ImmutableArray<RiskPosition> positions,ValuationSetup setup)
    {
        Setup=setup??throw Error("valuation_measure","Choose an explicit valuation measure and its resolved inputs.");
        if(!Enum.IsDefined(Setup.Measure)||Setup.FuturesBindings is null||instruments.IsDefaultOrEmpty||positions.IsDefaultOrEmpty||
            instruments.Length>100000||positions.Length>100000||instruments.Any(i=>i is null)||positions.Any(p=>p is null)||
            instruments.Select(i=>i.Id).Distinct().Count()!=instruments.Length)
            throw Error("risk_contract","Use a bounded nonempty portfolio with unique unit instrument identities and an explicit valuation measure.");
        Instruments=instruments.OrderBy(i=>i.Id).ToImmutableArray();
        Positions=positions.OrderBy(p=>p.TradeId).ThenBy(p=>p.BindingId).ToImmutableArray();
        indexes=Instruments.Select((i,n)=>(i.Id,n)).ToDictionary(p=>p.Id,p=>p.n);
        if(Setup.InstrumentDiscounting is null||Setup.Discounting is not null&&!Setup.InstrumentDiscounting.IsEmpty||
            Setup.InstrumentDiscounting.Any(p=>!indexes.ContainsKey(p.Key)||p.Value is null))
            throw Error("side_context","Select one explicit shared discount provider or exact held-instrument providers.");
        if(Positions.Select(p=>(p.TradeId,p.BindingId)).Distinct().Count()!=Positions.Length||
            Positions.GroupBy(p=>p.TradeId).Any(g=>g.Select(p=>p.BookId).Distinct().Count()!=1))
            throw Error("risk_position","Trade bindings must be distinct and one trade must have one book.");
        foreach(var p in Positions)
        {
            if(p.TradeId==Guid.Empty||p.BindingId==Guid.Empty||p.BookId==Guid.Empty||!indexes.TryGetValue(p.ValuationId,out var index))
                throw Error("risk_position","Each holding requires an exact trade, binding, book and unit instrument.");
            var instrument=Instruments[index];
            if(!instrument.SupportsQuantityScaling)
                throw Error("risk_quantity_scaling","This instrument has not declared linear scaling in booked quantity.");
            instrument.ValidateBinding(p.Quantity,p.Coverage);
            if(p.Quantity.Specification!=instrument.UnitQuantity.Specification||p.UnitBasis.Quantity!=p.Quantity.Specification||
                p.FixedBasis.Currency!=instrument.Currency||p.UnitBasis.Money.Currency!=instrument.Currency)
                throw Error("risk_position","Each external holding requires the instrument's exact quantity basis and currency.");
            if(Setup.Measure==ValuationMeasure.PresentValue&&
                (p.FixedBasis.Amount!=default||p.UnitBasis.Money.Numerator!="0"))
                throw Error("valuation_measure","Execution bases require the explicitly selected execution-P&L operation.");
        }
        // This projection cannot infer an intended termination or coverage envelope.
        // It can still refuse double-counted delivery across retained/amended bindings.
        foreach(var trade in Positions.Where(p=>p.Coverage is not null).GroupBy(p=>p.TradeId))
        {
            var coverage=trade.Select(p=>p.Coverage!).OrderBy(c=>c.From).ToArray();
            if(coverage.Zip(coverage.Skip(1)).Any(pair=>pair.First.To>pair.Second.From))
                throw Error("ppa_segment_coverage","A trade's PPA bindings cannot overlap, including across an amended instrument identity.");
        }
        if(Positions.Select(p=>p.ValuationId).Distinct().Count()!=Instruments.Length)
            throw Error("risk_contract","Every unit instrument must have a held consumer.");
        if(Setup.Settlements is null || Setup.Settlements.Any(s=>s.Value is null || s.Value.Discounting is null ||
            !Positions.Any(p=>new MeasureBinding(p.TradeId,p.BindingId)==s.Key && p.Quantity==s.Value.Settlement.Quantity &&
                p.TradeId==s.Value.Settlement.TradeId && p.BindingId==s.Value.Settlement.BindingId && Instruments[indexes[p.ValuationId]].SupportsSettlement)))
            throw Error("settlement_scope","Every settlement input must belong to its exact held trade binding and booked quantity.");
        Scopes=Positions.Select(ScopeKey).Distinct().OrderBy(s=>s.ValuationId)
            .ThenBy(s=>s.Coverage?.From).ThenBy(s=>s.Coverage?.To)
            .ThenBy(s=>s.Settlement?.TradeId).ThenBy(s=>s.Settlement?.BindingId)
            .Select(s=>new UnitValuationScope(Instruments[indexes[s.ValuationId]],s.Coverage,s.Settlement)).ToImmutableArray();
        var scopeIndexes=Scopes.Select((s,n)=>(s,n)).ToDictionary(x=>(x.s.Instrument.Id,x.s.Coverage,x.s.Settlement),x=>x.n);
        Books=Positions.Select((p,n)=>(p,n)).GroupBy(x=>x.p.BookId).OrderBy(g=>g.Key)
            .Select(g=>new Book(g.Key,g.Select(x=>(x.n,scopeIndexes[ScopeKey(x.p)])).ToImmutableArray(),Positions)).ToImmutableArray();
    }
    private (Guid ValuationId,DeliveryCoverage? Coverage,MeasureBinding? Settlement) ScopeKey(RiskPosition p) =>
        (p.ValuationId,p.Coverage,Setup.Settlements.ContainsKey(new(p.TradeId,p.BindingId))?new(p.TradeId,p.BindingId):null);
    public ImmutableArray<BookRiskFactors> RiskFactors(CurveModel model,MarketSnapshot snapshot,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        var declarations=Declare(snapshot);
        var market=model.Build(snapshot,declarations.SelectMany(d=>d.Dependencies).Distinct().ToImmutableArray(),cancellation);
        return RiskFactors(market,declarations,cancellation);
    }
    internal ImmutableArray<BookRiskFactors> RiskFactors(CurveBuild market,ImmutableArray<UnitPricingContract> declarations,CancellationToken cancellation)
        =>RiskFactors(market.Pricing,declarations,cancellation);
    internal ImmutableArray<BookRiskFactors> RiskFactors(PreparedValuationMarket market,ImmutableArray<UnitPricingContract> declarations,CancellationToken cancellation)
    {
        var context=new UnitValuationContext(market.Snapshot,Setup,market,cancellation:cancellation);
        var byScope=Scopes.Select((scope,n)=>
        {
            cancellation.ThrowIfCancellationRequested();
            var unitContext=context.For(declarations[n],scope.Coverage);
            var supplied=scope.Settlement is null?scope.Instrument.MarketFactors(unitContext):unitContext.ResolveFactors(declarations[n].Dependencies);
            var expected=unitContext.ResolveFactors(declarations[n].Dependencies);
            if(supplied.IsDefault||supplied.Any(f=>f is null)||supplied.Select(f=>f.Input).Distinct().Count()!=supplied.Length||
                !supplied.ToHashSet().SetEquals(expected))
                throw Error("risk_contract","An instrument's factor list must match its complete declared inputs in this exact context.");
            return (Key:(scope.Instrument.Id,scope.Coverage,scope.Settlement),Factors:supplied);
        }).ToDictionary(p=>p.Key,p=>p.Factors);
        return Books.Select(book=>
        {
            cancellation.ThrowIfCancellationRequested();
            var supplied=Positions.Where(p=>p.BookId==book.Id).Select(ScopeKey).Distinct()
                .SelectMany(id=>byScope[id]).DistinctBy(f=>f.Input).OrderBy(f=>f.Input.Series.Id).ToImmutableArray();
            return new BookRiskFactors(book.Id,supplied);
        }).ToImmutableArray();
    }
    public ImmutableArray<UnitPricingContract> Declare(MarketSnapshot snapshot)
    {
        var context=new UnitValuationContext(snapshot,Setup);
        var lifecycleInstruments=Scopes.Where(s=>s.Settlement is not null).Select(s=>s.Instrument.Id).ToHashSet();
        return Scopes.Select(scope=>
        {
            var instrument=scope.Instrument;
            var c=instrument.DeclareScope(context.ForCoverage(scope.Coverage),lifecycleInstruments.Contains(instrument.Id));
            if(c is null||c.Id!=instrument.Id||string.IsNullOrWhiteSpace(c.ModelVersion)||c.Dependencies.IsDefault||
                c.Dependencies.Distinct().Count()!=c.Dependencies.Length||c.Quantity!=instrument.UnitQuantity.Specification||
                c.Currency!=instrument.Currency||instrument.UnitQuantity.Amount!=ExactDecimal.Parse("1"))
                throw Error("risk_contract","Each unit instrument requires a complete versioned dependency declaration, currency and normalized unit basis.");
            Money.RequireKey(c.Currency);Money.RequireKey(c.Quantity);
            if(c.Currency.Revision!=c.Quantity.Revision)throw Error("risk_contract","Currency and quantity require the same exact reference revision.");
            return c with{ScheduleIdentity=instrument.RetainedScheduleIdentity,SettlementProjections=instrument.SettlementProjections};
        }).ToImmutableArray();
    }
    internal PortfolioValue PV(CurveBuild market,ImmutableArray<UnitPricingContract> declarations,CurveBuild? baseMarket=null,
        PortfolioValue? baseValue=null,CancellationToken ct=default,RiskDetail detail=RiskDetail.Holdings)
        =>PV(market.Pricing,declarations,baseMarket is null?null:baseMarket.Pricing,baseValue,ct,detail);
    internal PortfolioValue PV(PreparedValuationMarket market,ImmutableArray<UnitPricingContract> declarations,PreparedValuationMarket? baseMarket=null,
        PortfolioValue? baseValue=null,CancellationToken ct=default,RiskDetail detail=RiskDetail.Holdings)
    {
        if(declarations.Length!=Scopes.Length||(baseMarket is null)!=(baseValue is null)||
            baseMarket is not null&&(!market.Compatible(baseMarket)||
                !ReferenceEquals(baseValue!.Owner,this)||!ReferenceEquals(baseValue.Market.Identity,baseMarket.Identity)||
                !baseValue.Declarations.SequenceEqual(declarations)))
            throw Error("instrument_context","Unit reuse requires complete declarations and the same pinned base inputs.");
        if(Setup.Interval is not null && market.NativeIntervalScenario is null)
            throw Error("ppa_context","This interval consumer requires its exact native quote scenario; connected output overlays are unsupported.");
        var session=Setup.Interval?.Session(market.NativeIntervalScenario??throw Error("ppa_context","Interval valuation requires an exact native market scenario."),baseValue?.IntervalSession);
        var context=new UnitValuationContext(market.Snapshot,Setup,market,cancellation:ct,intervalSession:session);
        var unit=ImmutableArray.CreateBuilder<ExactMoney>(Scopes.Length);var calls=0;
        var measures=new Dictionary<(Guid,DeliveryCoverage?),ValueMeasureSet>();
        for(var n=0;n<Scopes.Length;n++)
        {
            ct.ThrowIfCancellationRequested();var contract=declarations[n];
            if(contract.Id!=Scopes[n].Instrument.Id)throw Error("risk_contract","Declaration ordering must match exact instrument identities.");
            var affected=baseMarket is null||!market.Unchanged(baseMarket,contract.Dependencies,ct);
            var scope=Scopes[n];
            var unitContext=context.For(contract,scope.Coverage,scope.Settlement is {} binding?Setup.Settlements[binding]:null);
            ExactMoney value;
            if(affected)
            {
                var result=scope.Instrument.PV(unitContext);
                if(result is null||!ReferenceEquals(result.Context,unitContext))
                    throw Error("instrument_context","A unit PV must belong to this exact instrument/scenario valuation context.");
                if(result.Money is null||result.Quantity!=contract.Quantity)
                    throw Error("risk_quantity","A unit PV must retain its declared quantity denominator.");
                value=result.Money;
            }
            else value=baseValue!.UnitValues[n];
            if(baseValue is null && unitContext.Measures is {} companion)
                measures.Add((contract.Id,Scopes[n].Coverage),companion);
            if(value.Currency!=contract.Currency)throw Error("risk_currency","An instrument must retain its declared output currency.");
            unit.Add(value);if(affected)calls++;
        }
        var units=unit.MoveToImmutable();var results=detail==RiskDetail.Holdings?new ExactMoney[Positions.Length]:[];
        var totals=baseValue?.Totals.ToDictionary(t=>(t.BookId,t.Currency),t=>t.Value)??new();
        var applications=0;
        foreach(var book in Books)applications+=book.PV(Positions,units,baseValue,results,totals,ct);
        var baseMeasures=baseValue?.BaseMeasures??AggregateMeasures(measures);
        return new(this,market,declarations,results.ToImmutableArray(),totals.OrderBy(p=>p.Key.Item1).ThenBy(p=>p.Key.Item2.Code,StringComparer.Ordinal)
            .Select(p=>new BookCurrencyValue(p.Key.Item1,p.Key.Item2,p.Value)).ToImmutableArray(),units,calls,applications,session,baseMeasures,context.SideCalculations);
    }
    private ImmutableArray<BookValueMeasures> AggregateMeasures(Dictionary<(Guid,DeliveryCoverage?),ValueMeasureSet> units)
    {
        var totals=new Dictionary<(Guid Book,ReferenceKey Currency),MeasureAccumulator>();
        var bindings=new Dictionary<(Guid Book,ReferenceKey Currency),List<MeasureBinding>>();
        foreach(var position in Positions)
        {
            if(!units.TryGetValue((position.ValuationId,position.Coverage),out var measures))continue;
            var key=(position.BookId,measures.Currency);
            if(!totals.TryGetValue(key,out var total))
            {totals.Add(key,total=new(measures.Currency));bindings.Add(key,[]);}
            bindings[key].Add(new(position.TradeId,position.BindingId));
            total.Add(measures.Scale(position.Quantity.Amount));
        }
        return [..totals.OrderBy(p=>p.Key.Book).ThenBy(p=>p.Key.Currency.Code,StringComparer.Ordinal)
            .Select(p=>new BookValueMeasures(p.Key.Book,p.Key.Currency,p.Value.Finish(),[..bindings[p.Key]]))];
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
