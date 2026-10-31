using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record RetainedPpaMember(Guid SideId,SideSlot Slot,int Coefficient,PpaNormalization Normalization);
internal sealed record RetainedPpaPrograms(InstrumentScheduleExecution Execution,PpaDelivery Delivery,
    ReferenceKey Currency,ReferenceKey UnitQuantity,bool Discounting,ImmutableArray<RetainedPpaMember> Members);

internal sealed partial class RetainedScheduleStore
{
    internal async Task<RetainedPpaPrograms> LoadPpaInstrumentPrograms(NpgsqlConnection connection,Guid instrument,
        Guid? context,DateTimeOffset? asOf,CancellationToken cancellation)
    {
        var retained=await LoadInstrumentPrograms(connection,instrument,context,asOf,cancellation);
        await using var command=ScheduleStore.Command(connection,
            "SELECT security.read_ppa_program_metadata($1,$2,$3,$4)::text",Catalog,instrument,
            (object?)context??DBNull.Value,(object?)asOf??DBNull.Value);
        using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(cancellation))!);
        var metadata=document.RootElement;
        if(metadata.GetProperty("implementation").GetString()!=PpaInstrument.Implementation)
            throw new MarketDataException("instrument_program_unsupported","The retained instrument implementation is unsupported.");
        var reference=new ReferenceRevision(metadata.GetProperty("referenceCatalog").GetGuid(),metadata.GetProperty("referenceRevision").GetGuid());
        ReferenceKey Key(string name)=>new(reference,metadata.GetProperty(name).GetString()!);
        var delivery=new PpaDelivery(Key("clock"),metadata.GetProperty("intervalMinutes").GetInt32(),
            metadata.GetProperty("deliveryFrom").Deserialize<DateOnly>(),metadata.GetProperty("deliveryTo").Deserialize<DateOnly>());
        var members=ImmutableArray.CreateBuilder<RetainedPpaMember>();
        var execution=ImmutableArray.CreateBuilder<InstrumentScheduleMember>();
        foreach(var row in metadata.GetProperty("members").EnumerateArray())
        {
            var position=row.GetProperty("position").GetInt32();var id=row.GetProperty("sideId").GetGuid();
            if(position!=members.Count||position>=retained.Members.Length||retained.Members[position].SideId!=id)
                throw InvalidMetadata();
            var role=row.GetProperty("role").GetString() switch
            {
                "energy_delivery"=>PpaRole.EnergyDelivery,"certificate_delivery"=>PpaRole.CertificateDelivery,
                "capacity_delivery"=>PpaRole.CapacityDelivery,"strike_payment"=>PpaRole.StrikePayment,
                "indexed_payment"=>PpaRole.IndexedPayment,"fixed_charge"=>PpaRole.FixedCharge,
                "certificate_payment"=>PpaRole.CertificatePayment,"capacity_payment"=>PpaRole.CapacityPayment,
                _=>throw InvalidMetadata()
            };
            var policy=row.GetProperty("normalizationPolicy").GetString() switch
            {
                "contract_unit_v1"=>PpaNormalizationPolicy.ContractUnit,
                "sum_capacity_in_force_v1"=>PpaNormalizationPolicy.SumCapacityInForce,_=>throw InvalidMetadata()
            };
            var memberReference=new ReferenceRevision(row.GetProperty("referenceCatalog").GetGuid(),row.GetProperty("referenceRevision").GetGuid());
            var definition=new DefinitionRevision(Catalog,row.GetProperty("definitionRevision").GetGuid());
            if(memberReference!=reference||definition.RevisionId!=metadata.GetProperty("definitionRevision").GetGuid())throw InvalidMetadata();
            var normalization=new PpaNormalization([..row.GetProperty("assets").EnumerateArray().Select(asset=>new AssetKey(definition,asset.GetString()!))],
                new(memberReference,row.GetProperty("normalizationBasis").GetString()!),policy);
            var coefficient=row.GetProperty("coefficient").GetInt32();
            if(coefficient is not (-1 or 1)||normalization.Basis!=Key("unitQuantity"))throw InvalidMetadata();
            members.Add(new(id,new(role,row.GetProperty("ordinal").GetInt32()),coefficient,normalization));
            var attribute=role switch
            {
                PpaRole.CertificateDelivery or PpaRole.CertificatePayment=>ValuedAttribute.Certificate,
                PpaRole.CapacityDelivery or PpaRole.CapacityPayment=>ValuedAttribute.Capacity,
                _=>ValuedAttribute.Energy
            };
            execution.Add(new(id,retained.Members[position].Program,DefinitionIdentity.For(normalization).Canonical,attribute));
        }
        if(members.Count!=retained.Members.Length||delivery.From>=delivery.To||delivery.IntervalMinutes is not (15 or 60))throw InvalidMetadata();
        var assembled=new InstrumentScheduleExecution(instrument,execution.ToImmutable(),retained.Connections);
        // Decoding and connection validation may outlast a grant. Recheck before returning.
        await using var authority=ScheduleStore.Command(connection,
            "SELECT count(*) FROM security.read_instrument_side_schedule_headers($1,$2,$3,$4)",Catalog,instrument,
            (object?)context??DBNull.Value,(object?)asOf??DBNull.Value);
        if((long)(await authority.ExecuteScalarAsync(cancellation))! !=members.Count)throw InvalidMetadata();
        cancellation.ThrowIfCancellationRequested();
        return new(assembled,delivery,Key("currency"),Key("unitQuantity"),metadata.GetProperty("discounting").GetBoolean(),members.ToImmutable());
    }
    private static MarketDataException InvalidMetadata()=>new("instrument_program_metadata","Retained instrument metadata must match its exact executable memberships and explicit normalization.");
}
