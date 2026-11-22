using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeskReadRequest(Guid SnapshotId, Guid? Id = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeskCommand(Guid OperationId, JsonElement Record);
public sealed record FacilityTradeWindow(FacilityQuantityContext Context,Guid ProfileId,DateOnly From,DateOnly To);
public sealed record FacilityAttribution(Guid TradeId,Guid ProfileId);
public sealed record DeskChoice(Guid OperationId, Guid AgreementId, string? ExpectedProfile=null);
public sealed record DeskEligibility(Guid LegalEntityId, Guid CounterpartyId, string Product);

internal sealed record DeskAdmission(Guid Catalog,string Permission,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT desk.require_authority($1,$2,$3)",Catalog,Permission,Until);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task Capture(PreviewDatabase database,NpgsqlConnection connection,string permission,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.authority_until($1)",permission);
        var until=new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(connection,new DeskAdmission(database.Manifest.CatalogId,permission,until));
    }
}

internal static class BilateralDesk
{
    internal static string Permission(string kind)=>kind switch{
        "agreement"=>"ManageAgreements","credit"=>"ManageCounterpartyCredit","confirmation"=>"ConfirmBilateralTrades",
        _=>throw new MarketDataException("desk_terms","Choose an agreement, credit decision, or commercial confirmation.")};

    internal static string Error(string code)=>code switch{
        "desk_agreement_required"=>"An executed agreement must cover this legal entity, counterparty, product and execution date.",
        "desk_agreement_ambiguous"=>"Choose the applicable agreement. Several executed agreements cover this booking.",
        "desk_credit_required"=>"An approved credit decision is required for these parties and product.",
        "desk_credit_hold"=>"Credit has placed this counterparty and product on hold.",
        "desk_credit_expired"=>"The credit clearance has expired.",
        "desk_document_required"=>"Attach the executed agreement PDF.",
        "desk_document" or "desk_document_size"=>"Choose a PDF of at most 8 MiB with a plain filename.",
        "desk_confirmation"=>"Choose a visible booked revision and a confirmation time between execution and now.",
        "desk_request_conflict" or "desk_stale"=>"Reload the current revision before changing these terms.",
        "desk_denied"=>"Your current role does not authorize this operation.",
        "desk_facility"=>"Choose a compatible facility and pricing profile for the complete delivery period.",
        _=>"Check the dates, parties, product and required terms."};
}
