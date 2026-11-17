using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeliveryNominationBatchEntry(Guid ObligationId,Guid TradeId,int ExpectedRevision,string Day,GasNominationDetails Details);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeliveryNominationBatchInput(FuturesReadRequest Context,Guid AllocationPlanId,string Profile,ImmutableArray<DeliveryNominationBatchEntry> Entries);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeliveryNominationBatchPreviewRequest(Guid OperationId,string OwnerId,DeliveryNominationBatchInput Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeliveryNominationBatchRecordRequest(Guid OperationId,string OwnerId,DeliveryNominationBatchInput Input,string PreviewHash);

internal static class DeliveryNominationBatchContracts
{
    internal const string Profile="complete_allocation_nomination_batch_v1";
    internal const int MaximumEntries=128;
    internal static DeliveryNominationBatchInput Normalize(DeliveryNominationBatchInput input)
    {
        try
        {
            if(input is null||input.Context is not {SnapshotId:var snapshot,Convention:PhysicalGasStore.Convention,KnowledgeMode:"current",ContextId:null}||snapshot==Guid.Empty||
                input.AllocationPlanId==Guid.Empty||input.Profile!=Profile||input.Entries.IsDefaultOrEmpty||input.Entries.Length>MaximumEntries)
                throw Invalid();
            var entries=input.Entries.Select(entry=>
            {
                if(entry is null||entry.ObligationId==Guid.Empty||entry.TradeId==Guid.Empty||entry.ExpectedRevision<1||entry.Details is not {} details||
                    details.AccountId==Guid.Empty||details.ServiceVersionId==Guid.Empty||details.ExpectedPreviousId==Guid.Empty||
                    details.Quantity is null||details.SubmittedAt is null||
                    details.Channel is not("portal" or "edi" or "email" or "phone" or "manual_import"))throw Invalid();
                var day=DateWorkspace.Date(entry.Day);
                var quantity=ExactDecimal.Parse(details.Quantity);
                if(quantity.CompareTo(default)<=0)throw Invalid();
                var submitted=TradingInstant.Parse(details.SubmittedAt,"delivery_nomination_batch_input");
                return entry with{Day=day.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),Details=details with{
                    Quantity=quantity.ToString(),SubmittedAt=LifecycleNative.Instant(submitted),
                    ExternalReference=GasOperationText.Key(details.ExternalReference,"delivery_nomination_batch_input"),
                    Reason=GasOperationText.Evidence(details.Reason,"delivery_nomination_batch_input"),
                    Evidence=GasOperationText.Evidence(details.Evidence,"delivery_nomination_batch_input")}};
            }).OrderBy(entry=>entry.ObligationId.ToString("D"),StringComparer.Ordinal).ThenBy(entry=>entry.Day,StringComparer.Ordinal).ToImmutableArray();
            if(entries.Select(entry=>(entry.ObligationId,entry.Day)).Distinct().Count()!=entries.Length)throw Invalid();
            return input with{Entries=entries};
        }
        catch(Exception error)when(error is MarketDataException or FormatException or ArgumentException or OverflowException)
        {throw Invalid();}
    }
    internal static Guid Child(Guid operation,int ordinal)=>OperationIdentity.For("delivery_nomination_batch_child_v1",operation,ordinal);
    internal static MarketDataException Invalid()=>new("delivery_nomination_batch_input","Supply one complete explicit allocation plan, working native context and distinct supported nomination entries.");
}
