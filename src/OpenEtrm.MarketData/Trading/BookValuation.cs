using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

public enum ValuationMeasure { PresentValue=1, ExecutionPnl }

// Operation-owned immutable inputs. A missing discount provider is explicitly
// unavailable, never an instruction to substitute undiscounted cashflows.
public sealed record ValuationSetup(ValuationMeasure Measure,
    ImmutableDictionary<Guid,FuturesMarketBinding> FuturesBindings,ISideDiscounting? Discounting)
{
    // Providers belong to instruments: two instruments can share the same side
    // while selecting different retained discounting terms.
    public ImmutableDictionary<Guid,ISideDiscounting> InstrumentDiscounting{get;init;}=ImmutableDictionary<Guid,ISideDiscounting>.Empty;
}

public sealed class UnitValuationContext
{
    internal CurveBuild? Market=>pricing?.Authority;
    internal ValuationSetup Setup {get;}
    internal MarketSnapshot Snapshot {get;}
    private readonly PreparedValuationMarket? pricing;
    private readonly UnitPricingContract? declaration;
    private readonly CancellationToken cancellation;
    public PricingInputs Inputs=>declaration is {} contract&&pricing is {} market?new(market,contract,cancellation):
        throw new MarketDataException("risk_dependency","A unit valuation requires its declared market inputs.");
    public ValuationMeasure Measure=>Setup.Measure;
    internal UnitValuationContext(MarketSnapshot snapshot,ValuationSetup setup,PreparedValuationMarket? market=null,UnitPricingContract? declaration=null,CancellationToken cancellation=default)
    {
        if(market is not null&&declaration is null&&(setup.Discounting??setup.InstrumentDiscounting.Values.FirstOrDefault()) is {} provider)
            SideValuationContext.Validate(snapshot,provider);
        Snapshot=snapshot;Setup=setup;pricing=market;this.declaration=declaration;this.cancellation=cancellation;
    }
    internal UnitValuationContext For(UnitPricingContract contract)=>new(Snapshot,Setup,pricing,contract,cancellation);
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
    internal SideValuationContext SideContext()
    {
        if(pricing is null||declaration is null)
            throw new MarketDataException("side_context","Supply the declared market and explicit payment discount inputs.");
        var provider=Setup.Discounting??Setup.InstrumentDiscounting.GetValueOrDefault(declaration.Id);
        return pricing.Context(provider,declaration.Dependencies,cancellation);
    }
}

public sealed record BookRiskFactors(Guid BookId,ImmutableArray<QuoteFactor> Factors);

public sealed record BookCurrencyValue(Guid BookId,ReferenceKey Currency,ExactMoney Value);
public sealed class PortfolioValue
{
    public ImmutableArray<ExactMoney> Positions{get;}
    public ImmutableArray<BookCurrencyValue> Totals{get;}
    public ImmutableArray<ExactMoney> UnitValues{get;}
    public int UnitValuations{get;}
    public int QuantityApplications{get;}
    internal ValuationPortfolio Owner{get;}
    internal PreparedValuationMarket Market{get;}
    internal ImmutableArray<UnitPricingContract> Declarations{get;}
    internal PortfolioValue(ValuationPortfolio owner,PreparedValuationMarket market,ImmutableArray<UnitPricingContract> declarations,
        ImmutableArray<ExactMoney> positions,ImmutableArray<BookCurrencyValue> totals,ImmutableArray<ExactMoney> units,int calls,int quantityApplications)
    {Owner=owner;Market=market;Declarations=declarations;Positions=positions;Totals=totals;UnitValues=units;UnitValuations=calls;QuantityApplications=quantityApplications;}
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

public sealed class ValuationPortfolio
{
    public ImmutableArray<Instrument> Instruments{get;}
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
            if(p.Quantity.Specification!=instrument.UnitQuantity.Specification||p.UnitBasis.Quantity!=p.Quantity.Specification||
                p.FixedBasis.Currency!=instrument.Currency||p.UnitBasis.Money.Currency!=instrument.Currency)
                throw Error("risk_position","Each external holding requires the instrument's exact quantity basis and currency.");
            if(Setup.Measure==ValuationMeasure.PresentValue&&
                (p.FixedBasis.Amount!=default||p.UnitBasis.Money.Numerator!="0"))
                throw Error("valuation_measure","Execution bases require the explicitly selected execution-P&L operation.");
        }
        if(Positions.Select(p=>p.ValuationId).Distinct().Count()!=Instruments.Length)
            throw Error("risk_contract","Every unit instrument must have a held consumer.");
        Books=Positions.Select((p,n)=>(p,n)).GroupBy(x=>x.p.BookId).OrderBy(g=>g.Key)
            .Select(g=>new Book(g.Key,g.Select(x=>(x.n,indexes[x.p.ValuationId])).ToImmutableArray(),Positions)).ToImmutableArray();
    }
    public ImmutableArray<BookRiskFactors> RiskFactors(CurveModel model,MarketSnapshot snapshot,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        var declarations=Declare(snapshot);
        var market=model.Build(snapshot,declarations.SelectMany(d=>d.Dependencies).Distinct().ToImmutableArray(),cancellation);
        return RiskFactors(market,declarations,cancellation);
    }
    internal ImmutableArray<BookRiskFactors> RiskFactors(CurveBuild market,ImmutableArray<UnitPricingContract> declarations,CancellationToken cancellation)
        =>RiskFactors(new BuiltValuationMarket(market),declarations,cancellation);
    internal ImmutableArray<BookRiskFactors> RiskFactors(PreparedValuationMarket market,ImmutableArray<UnitPricingContract> declarations,CancellationToken cancellation)
    {
        var context=new UnitValuationContext(market.Snapshot,Setup,market,cancellation:cancellation);
        var byInstrument=Instruments.Select((instrument,n)=>
        {
            cancellation.ThrowIfCancellationRequested();
            var unitContext=context.For(declarations[n]);
            var supplied=instrument.MarketFactors(unitContext);
            var expected=unitContext.ResolveFactors(declarations[n].Dependencies);
            if(supplied.IsDefault||supplied.Any(f=>f is null)||supplied.Select(f=>f.Input).Distinct().Count()!=supplied.Length||
                !supplied.ToHashSet().SetEquals(expected))
                throw Error("risk_contract","An instrument's factor list must match its complete declared inputs in this exact context.");
            return (instrument.Id,Factors:supplied);
        }).ToDictionary(p=>p.Id,p=>p.Factors);
        return Books.Select(book=>
        {
            cancellation.ThrowIfCancellationRequested();
            var supplied=Positions.Where(p=>p.BookId==book.Id).Select(p=>p.ValuationId).Distinct()
                .SelectMany(id=>byInstrument[id]).DistinctBy(f=>f.Input).OrderBy(f=>f.Input.Series.Id).ToImmutableArray();
            return new BookRiskFactors(book.Id,supplied);
        }).ToImmutableArray();
    }
    public ImmutableArray<UnitPricingContract> Declare(MarketSnapshot snapshot)
    {
        var context=new UnitValuationContext(snapshot,Setup);
        return Instruments.Select(instrument=>
        {
            var c=instrument.Declare(context);
            if(c is null||c.Id!=instrument.Id||string.IsNullOrWhiteSpace(c.ModelVersion)||c.Dependencies.IsDefault||
                c.Dependencies.Distinct().Count()!=c.Dependencies.Length||c.Quantity!=instrument.UnitQuantity.Specification||
                c.Currency!=instrument.Currency||instrument.UnitQuantity.Amount!=ExactDecimal.Parse("1"))
                throw Error("risk_contract","Each unit instrument requires a complete versioned dependency declaration, currency and normalized unit basis.");
            Money.RequireKey(c.Currency);Money.RequireKey(c.Quantity);
            if(c.Currency.Revision!=c.Quantity.Revision)throw Error("risk_contract","Currency and quantity require the same exact reference revision.");
            return c;
        }).ToImmutableArray();
    }
    internal PortfolioValue PV(CurveBuild market,ImmutableArray<UnitPricingContract> declarations,CurveBuild? baseMarket=null,
        PortfolioValue? baseValue=null,CancellationToken ct=default,RiskDetail detail=RiskDetail.Holdings)
        =>PV(new BuiltValuationMarket(market),declarations,baseMarket is null?null:new BuiltValuationMarket(baseMarket),baseValue,ct,detail);
    internal PortfolioValue PV(PreparedValuationMarket market,ImmutableArray<UnitPricingContract> declarations,PreparedValuationMarket? baseMarket=null,
        PortfolioValue? baseValue=null,CancellationToken ct=default,RiskDetail detail=RiskDetail.Holdings)
    {
        if(declarations.Length!=Instruments.Length||(baseMarket is null)!=(baseValue is null)||
            baseMarket is not null&&(!market.Compatible(baseMarket)||
                !ReferenceEquals(baseValue!.Owner,this)||!ReferenceEquals(baseValue.Market.Identity,baseMarket.Identity)||
                !baseValue.Declarations.SequenceEqual(declarations)))
            throw Error("instrument_context","Unit reuse requires complete declarations and the same pinned base inputs.");
        var context=new UnitValuationContext(market.Snapshot,Setup,market,cancellation:ct);
        var unit=ImmutableArray.CreateBuilder<ExactMoney>(Instruments.Length);var calls=0;
        for(var n=0;n<Instruments.Length;n++)
        {
            ct.ThrowIfCancellationRequested();var contract=declarations[n];
            if(contract.Id!=Instruments[n].Id)throw Error("risk_contract","Declaration ordering must match exact instrument identities.");
            var affected=baseMarket is null||!market.Unchanged(baseMarket,contract.Dependencies,ct);
            var value=affected?Instruments[n].Value(context.For(contract)):baseValue!.UnitValues[n];
            if(value.Currency!=contract.Currency)throw Error("risk_currency","An instrument must retain its declared output currency.");
            unit.Add(value);if(affected)calls++;
        }
        var units=unit.MoveToImmutable();var results=detail==RiskDetail.Holdings?new ExactMoney[Positions.Length]:[];
        var totals=baseValue?.Totals.ToDictionary(t=>(t.BookId,t.Currency),t=>t.Value)??new();
        var applications=0;
        foreach(var book in Books)applications+=book.PV(Positions,units,baseValue,results,totals,ct);
        return new(this,market,declarations,results.ToImmutableArray(),totals.OrderBy(p=>p.Key.Item1).ThenBy(p=>p.Key.Item2.Code,StringComparer.Ordinal)
            .Select(p=>new BookCurrencyValue(p.Key.Item1,p.Key.Item2,p.Value)).ToImmutableArray(),units,calls,applications);
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
