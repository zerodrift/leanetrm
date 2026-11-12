using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed partial class PpaStore
{
    // Bind only the admission scope. Price/series readers retain their own
    // durable contexts; a savepoint must not roll those receipt identities back.
    internal async Task RequireContractClose(NpgsqlConnection connection,InputFrame frame,string knownAt,CancellationToken ct)
    {
        var id=await Bind(connection,frame,knownAt,ct);
        var context=(await new KnowledgeStore(Catalog).Read(connection,id,ct)).Context;
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT power_close_admission"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            var pin=PinnedContext.Retained(context,frame with{ContextId=id});
            await pin.Bind(connection,Catalog,ct);
            _=await MarketInputSelection.RootSource(database,connection,pin.RequireFrame(),ct);
        }
        finally
        {
            await using(var rollback=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT power_close_admission"))await rollback.ExecuteNonQueryAsync(ct);
            await using(var release=ScheduleStore.Command(connection,"RELEASE SAVEPOINT power_close_admission"))await release.ExecuteNonQueryAsync(ct);
        }
    }

    internal async Task<object> ContractChange(NpgsqlConnection connection,InputFrame openingFrame,InputFrame closingFrame,PpaContractChangeRequest request,CancellationToken ct)
    {
        static MarketDataException Invalid()=>PpaCompiler.Error("ppa_pnl_scope","Compare one unchanged full-month fixed power holding at two ordered recorded closes, before payment, under explicit knowledge cutoffs.");
        var first=request.Opening;var last=request.Closing;
        if(request.Convention!=PpaContractualChange.Implementation||first is null||last is null||
            openingFrame.Close is null||closingFrame.Close is null||openingFrame.At>=closingFrame.At||
            openingFrame.Close.Session!=closingFrame.Close.Session||openingFrame.Close.SessionVersion!=closingFrame.Close.SessionVersion||
            first.Scope!="trades"||last.Scope!="trades"||first.SelectedIds is not{Length:1}||last.SelectedIds is not{Length:1}||
            first.SelectedIds[0]!=last.SelectedIds[0]||!first.ExplicitSubset||!last.ExplicitSubset||first.DetailMonths is not{Length:1}||last.DetailMonths is not{Length:1}||
            IntervalSeriesStore.ParseInstant(first.KnownAt)>IntervalSeriesStore.ParseInstant(last.KnownAt))throw Invalid();
        await RequireContractClose(connection,openingFrame,first.KnownAt,ct);
        await RequireContractClose(connection,closingFrame,last.KnownAt,ct);
        PpaPrepared? opening=null,closing=null;
        ImmutableArray<PpaBinding> openingBindings=[],closingBindings=[];
        ImmutableArray<ValueMeasureSet> openingValues=[],closingValues=[];
        _ = await Preview(connection,openingFrame,first,ct,capture:(p,b,v)=>{opening=p;openingBindings=b;openingValues=v;});
        _ = await Preview(connection,closingFrame,last,ct,capture:(p,b,v)=>{closing=p;closingBindings=b;closingValues=v;});
        if(opening is null||closing is null||openingBindings.Length!=1||closingBindings.Length!=1||openingValues.Length!=1||closingValues.Length!=1)throw Invalid();
        var a=openingBindings[0];var b=closingBindings[0];var instrument=a.Instrument;
        if(a.TradeId!=b.TradeId||a.Revision!=b.Revision||a.BindingId!=b.BindingId||a.Mw!=b.Mw||a.Coverage!=b.Coverage||instrument.Id!=b.Instrument.Id||
            DefinitionIdentity.For(instrument).Digest!=DefinitionIdentity.For(b.Instrument).Digest||instrument.Capability!="fixed_physical"||instrument.Discounting||
            instrument.Memberships.Length!=2||instrument.Memberships.Single(m=>m.Side is PowerPhysicalSide).Side.Terms.Quantity is not ProfileQuantity||
            instrument.Delivery.IntervalMinutes!=60||instrument.Delivery.From.Day!=1||instrument.Delivery.To!=instrument.Delivery.From.AddMonths(1)||
            instrument.NormalizationAssets.Length!=0||instrument.Memberships.Single(m=>m.Side is FixedSide).Side is not FixedSide{Price:FlatPrice}||
            first.DetailMonths[0]!=instrument.Delivery.From.ToString("yyyy-MM-dd")||last.DetailMonths[0]!=first.DetailMonths[0])throw Invalid();
        var clock=opening.Inputs.Reference.Clocks[instrument.Delivery.Clock];
        if(a.Coverage.From!=clock.StartOfDay(instrument.Delivery.From).ToUniversalTime()||a.Coverage.To!=clock.StartOfDay(instrument.Delivery.To).ToUniversalTime()||
            a.Coverage.From>=openingFrame.At||a.Coverage.To<=closingFrame.At||instrument.Memberships.Any(m=>new DateTimeOffset(m.Side.Terms.DateRule
                .Generate(instrument.Delivery.From,instrument.Delivery.To).Date.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)<=closingFrame.At))throw Invalid();
        // This first bridge attributes prices under one unchanged declared graph.
        // A model/assignment/publication change needs its own explicit attribution.
        var before=opening.PowerLocations;var after=closing.PowerLocations;
        if(before.Length!=1||after.Length!=1||before[0].Output!=after[0].Output||before[0].Settlement!=after[0].Settlement||
            before[0].ActualPrefix!=after[0].ActualPrefix||before[0].PrefixActivationRevision!=after[0].PrefixActivationRevision)throw Invalid();
        var earlierShapes=opening.Inputs.Receipt.CurveInputs.Values.SelectMany(c=>c.ShapeInputs).ToDictionary(p=>p.Anchor);
        foreach(var shape in closing.Inputs.Receipt.CurveInputs.Values.SelectMany(c=>c.ShapeInputs))
            if(earlierShapes.TryGetValue(shape.Anchor,out var prior)&&(prior.Series!=shape.Series||!prior.Cells.SequenceEqual(shape.Cells)))
                throw PpaCompiler.Error("ppa_pnl_shape","A changed live price-shape model requires separate attribution; this comparison holds the shape inputs fixed.");
        var change=PpaContractualChange.Calculate(openingValues[0],closingValues[0]);
        await RecheckScope(connection,openingFrame,first.KnownAt,first.Scope,first.SelectedIds,first.ReferenceCatalogId,ct);
        await RecheckScope(connection,closingFrame,last.KnownAt,last.Scope,last.SelectedIds,last.ReferenceCatalogId,ct);
        await Recheck(connection,openingFrame,ct);await Recheck(connection,closingFrame,ct);
        return new{implementation=PpaContractualChange.Implementation,measure="contractual_value_change",change,
            opening=new{snapshotId=openingFrame.Id,at=openingFrame.At,knownAt=first.KnownAt,receipt=InputReceipt(opening,opening.Inputs.Receipt.KnowledgeContext,first.PendingActuals)},
            closing=new{snapshotId=closingFrame.Id,at=closingFrame.At,knownAt=last.KnownAt,receipt=InputReceipt(closing,closing.Inputs.Receipt.KnowledgeContext,last.PendingActuals)},
            tradeId=a.TradeId,revision=a.Revision,bindingId=a.BindingId,instrumentId=instrument.Id,quantity=a.Mw.ToString(),coverage=a.Coverage};
    }
}
