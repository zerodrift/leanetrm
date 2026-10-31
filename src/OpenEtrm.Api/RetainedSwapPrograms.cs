using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

internal sealed record RetainedSwapPeriod(int Ordinal,DateOnly From,DateOnly To,ExactRatio Allocation);
internal sealed record RetainedSwapMember(RetainedSideProgram Program,int Coefficient,ExactRatio QuantityRatio,
    ReferenceKey Quote,ReferenceKey Currency,ReferenceKey UnitQuantity,ImmutableArray<RetainedSwapPeriod> Periods);
internal sealed record RetainedSwapPrograms(Guid InstrumentId,string Implementation,bool Discounting,
    ImmutableArray<RetainedSwapMember> Members,Guid? FactorId,QuantityFactor? Factor,string? UnitConversionImplementation)
{
    internal ReferenceKey Currency=>Members[0].Currency;
    internal ReferenceKey UnitQuantity=>Members[0].UnitQuantity;
}

internal sealed partial class RetainedScheduleStore
{
    internal async Task<RetainedSwapPrograms> LoadSwapInstrumentPrograms(NpgsqlConnection connection,Guid instrument,
        Guid? context,DateTimeOffset? asOf,CancellationToken cancellation)
    {
        var retained=await LoadInstrumentPrograms(connection,instrument,context,asOf,cancellation);
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.read_swap_program_metadata($1,$2,$3,$4)::text",Catalog,instrument,
            (object?)context??DBNull.Value,(object?)asOf??DBNull.Value);
        using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(cancellation))!);
        var metadata=document.RootElement;var implementation=metadata.GetProperty("implementation").GetString()!;
        if(implementation is not (FinancialSwapInstrument.Implementation or ScheduledFinancialSwap.Implementation or CrossCommodityScheduledSwap.Implementation))
            throw new MarketDataException("instrument_program_unsupported","The retained swap implementation is unsupported.");
        if(retained.Members.Length!=2||!retained.Connections.IsEmpty)throw InvalidSwapMetadata();
        var members=ImmutableArray.CreateBuilder<RetainedSwapMember>();
        foreach(var row in metadata.GetProperty("members").EnumerateArray())
        {
            var slot=row.GetProperty("slot").GetInt32();var side=row.GetProperty("sideId").GetGuid();
            if(slot!=members.Count||slot>=retained.Members.Length||retained.Members[slot].SideId!=side)throw InvalidSwapMetadata();
            var reference=new ReferenceRevision(row.GetProperty("referenceCatalog").GetGuid(),row.GetProperty("referenceRevision").GetGuid());
            ReferenceKey Key(string name)=>new(reference,row.GetProperty(name).GetString()!);
            var coefficient=row.GetProperty("coefficient").GetInt32();
            var ratio=new ExactRatio(BigInteger.Parse(row.GetProperty("numerator").GetString()!,CultureInfo.InvariantCulture),
                BigInteger.Parse(row.GetProperty("denominator").GetString()!,CultureInfo.InvariantCulture));
            var program=retained.Members[slot].Program.Document;
            if(coefficient is not (-1 or 1)||program.Family is not ("fixed" or "floating")||!program.Grids.IsEmpty||
                program.Instructions[program.Roots.Single(root=>root.Name=="pv").Row].Output.Currency!=Key("currency"))throw InvalidSwapMetadata();
            var periods=row.GetProperty("periods").EnumerateArray().Select(p=>new RetainedSwapPeriod(p.GetProperty("ordinal").GetInt32(),
                p.GetProperty("from").Deserialize<DateOnly>(),p.GetProperty("to").Deserialize<DateOnly>(),
                new(BigInteger.Parse(p.GetProperty("numerator").GetString()!,CultureInfo.InvariantCulture),
                    BigInteger.Parse(p.GetProperty("denominator").GetString()!,CultureInfo.InvariantCulture)))).ToImmutableArray();
            RequireProgramPeriods(retained.Members[slot].Program,Key("unitQuantity"),periods);
            members.Add(new(retained.Members[slot],coefficient,ratio,Key("quote"),Key("currency"),Key("unitQuantity"),periods));
        }
        if(members.Count!=2||members[1].Program.Program.Document.Family!="floating"||members[0].Coefficient!=-members[1].Coefficient||members[0].Currency!=members[1].Currency||
            members[0].QuantityRatio!=ExactRatio.One||implementation==FinancialSwapInstrument.Implementation&&members[1].QuantityRatio!=ExactRatio.One)
            throw InvalidSwapMetadata();
        if(implementation!=FinancialSwapInstrument.Implementation)
            RequirePeriodAllocations(members[0].Periods,members[1].Periods,implementation==CrossCommodityScheduledSwap.Implementation);
        else if(members.Any(member=>member.Periods.Length!=1||member.Periods[0].Allocation!=ExactRatio.One))throw InvalidSwapMetadata();
        Guid? factorId=null;QuantityFactor? factor=null;string? conversion=null;
        var factorRow=metadata.GetProperty("factor");
        if(implementation==CrossCommodityScheduledSwap.Implementation)
        {
            if(factorRow.ValueKind!=JsonValueKind.Object)throw InvalidSwapMetadata();
            var reference=new ReferenceRevision(factorRow.GetProperty("referenceCatalog").GetGuid(),factorRow.GetProperty("referenceRevision").GetGuid());
            conversion=factorRow.GetProperty("conversionImplementation").GetString();
            if(reference!=members[0].Currency.Revision||conversion!=UnitConverter.Implementation||members.Any(member=>member.Program.Program.Document.Family!="floating"))
                throw InvalidSwapMetadata();
            factorId=factorRow.GetProperty("id").GetGuid();
            factor=new(ExactDecimal.Parse(factorRow.GetProperty("value").GetString()!),
                new(reference,factorRow.GetProperty("numeratorQuantity").GetString()!),new(reference,factorRow.GetProperty("denominatorQuantity").GetString()!),
                factorRow.GetProperty("implementation").GetString()!);
            var references=new[]{"units","unit_conversions","quantity_specifications"}.ToImmutableDictionary(
                name=>name,name=>metadata.GetProperty(name).EnumerateArray().ToImmutableArray());
            var converter=UnitOperations.Converter(new(reference,references));
            RequireFactorMapping(factor,members[0].UnitQuantity,members[1].UnitQuantity,members[1].QuantityRatio,converter);
        }
        else if(factorRow.ValueKind!=JsonValueKind.Null||members[0].UnitQuantity!=members[1].UnitQuantity)throw InvalidSwapMetadata();
        await using var authority=ScheduleStore.Command(connection,
            "SELECT security.check_swap_program_metadata_scope($1,$2,$3,$4)",Catalog,instrument,
            (object?)context??DBNull.Value,(object?)asOf??DBNull.Value);
        if((long)(await authority.ExecuteScalarAsync(cancellation))! !=members.Count)throw InvalidSwapMetadata();
        cancellation.ThrowIfCancellationRequested();
        return new(instrument,implementation,metadata.GetProperty("discounting").GetBoolean(),members.ToImmutable(),factorId,factor,conversion);
    }
    internal static void RequirePeriodAllocations(ImmutableArray<RetainedSwapPeriod> first,ImmutableArray<RetainedSwapPeriod> second,bool factor)
    {
        foreach(var periods in new[]{first,second})
        {
            if(periods.IsDefaultOrEmpty)throw InvalidSwapMetadata();
            for(var n=0;n<periods.Length;n++)
            {
                var period=periods[n];
                if(period.Ordinal!=n||period.From.Day!=1||period.From.Year==9999&&period.From.Month==12||
                    period.To!=period.From.AddMonths(1)||n>0&&period.From!=periods[n-1].To)throw InvalidSwapMetadata();
            }
            if(periods.Select(p=>p.Allocation).Aggregate((a,b)=>a.Add(b))!=ExactRatio.One)throw InvalidSwapMetadata();
        }
        if(factor&&!first.SequenceEqual(second))throw InvalidSwapMetadata();
    }
    internal static void RequireProgramPeriods(BookedSideProgram program,ReferenceKey unit,ImmutableArray<RetainedSwapPeriod> periods)
    {
        var quantities=program.Document.Instructions.Where(i=>i.Output.Operation=="quantity_literal").Select(i=>i.Output).ToArray();
        if(periods.IsDefaultOrEmpty||quantities.Length!=periods.Length)throw InvalidSwapMetadata();
        for(var n=0;n<periods.Length;n++)
        {
            var row=quantities[n];var period=periods[n];
            if(period.Ordinal!=n||row.PaymentOrdinal!=n||row.DeliveryFrom!=period.From||row.DeliveryTo!=period.To||row.Unit!=unit||
                new ExactRatio(BigInteger.Parse(row.Parameters["numerator"],CultureInfo.InvariantCulture),
                    BigInteger.Parse(row.Parameters["denominator"],CultureInfo.InvariantCulture))!=period.Allocation)throw InvalidSwapMetadata();
        }
    }
    internal static void RequireFactorMapping(QuantityFactor factor,ReferenceKey first,ReferenceKey second,ExactRatio ratio,UnitConverter converter)
    {
        if(converter.Specification(first).Commodity==converter.Specification(second).Commodity||
            factor.NativeRatio(first,second,converter)!=ratio)throw InvalidSwapMetadata();
    }
    private static MarketDataException InvalidSwapMetadata()=>new("instrument_program_metadata",
        "Retained swap metadata requires exact member units, directions, supported factor policy and explicit native quantity ratios.");
}
