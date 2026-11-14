using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Scope is resolved by its registered native handler. It may identify a trade,
// titled lot, operator account or loan; no generic table interprets those keys.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleTarget(string Handler,Guid ScopeId,Guid BindingId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleReadContext(Guid ContextId,[property:JsonRequired]DateTimeOffset AsOf,[property:JsonRequired]DateOnly BusinessDate,Guid? SnapshotId=null);
public sealed record LifecycleActionKindChoice(string Code,bool Automatic,string ConfigurationPermission,string ExecutionPermission);
public sealed record LifecycleSettingsChoices(ImmutableArray<LifecycleActionKindChoice> Actions,ImmutableArray<string> Modes,string PolicyImplementation,
    ImmutableArray<string> SwapRounding,ImmutableArray<string> SwapDue,ImmutableArray<string> SwapRecognition,ImmutableArray<string> SwapCorrection,
    LifecycleFuturesChoices Futures);
public sealed record LifecycleFuturesChoices(string PolicyImplementation,ImmutableArray<string> Opening,ImmutableArray<string> Ties,
    ImmutableArray<string> RoundingStages,ImmutableArray<string> RoundingScopes,ImmutableArray<string> Phases,ImmutableArray<string> Adoption)
{public LifecycleDeliveryChoices? Delivery{get;init;}}
public sealed record LifecycleDeliveryChoices(ImmutableArray<string> Implementations,ImmutableArray<string> Carrying,
    ImmutableArray<string> PrincipalScopes,ImmutableArray<string> CorrectionDuePolicies);
public sealed record LifecycleActionSubject(string Reference,string Product,Guid BookId,string Book);
public sealed record LifecycleInputChoice(string Value,string Label);
public sealed record LifecycleActionOffer(LifecycleTarget Target,string Action,string Label,Guid OccurrenceId,
    DateTimeOffset At,DateTimeOffset? Before,bool Eligible,string? Reason,int Revision,int StateVersion)
{
    public LifecycleActionSubject? Subject{get;init;}
    public Guid? PolicyId{get;init;}
    public int? PolicyRevision{get;init;}
    public JsonElement? Selection{get;init;}
    public ImmutableArray<string> RequiredInputs{get;init;}=[];
    public string? AvailableQuantity{get;init;}
    public ImmutableDictionary<string,ImmutableArray<LifecycleInputChoice>> InputChoices{get;init;}=ImmutableDictionary<string,ImmutableArray<LifecycleInputChoice>>.Empty;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleActionRequest(Guid OperationId,LifecycleReadContext Context,LifecycleTarget Target,
    string Action,Guid PolicyId,int PolicyRevision,[property:JsonRequired]bool Automatic,string PreviewHash,JsonElement Selection);
internal sealed record LifecycleActionReview(LifecycleActionOffer Offer,string Hash,JsonElement Details,
    [property:JsonIgnore]Guid Catalog,[property:JsonIgnore]DateTimeOffset AuthorityUntil):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT lifecycle.require_read($1,$2)",Catalog,AuthorityUntil);
        await command.ExecuteNonQueryAsync(ct);
    }
}
internal sealed record LifecycleReadAdmission(Guid Catalog,DateTimeOffset AuthorityUntil):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT lifecycle.require_read($1,$2)",Catalog,AuthorityUntil);
        await command.ExecuteNonQueryAsync(ct);
    }
}
internal sealed record LifecycleActionWrite(JsonElement Outcome,ICommitAdmission Admission):ICommitAdmission
{
    public Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)=>Admission.RequireCommit(connection,ct);
}
internal interface ILifecycleActionHandler
{
    string Kind {get;}
    ImmutableArray<string> Permissions {get;}
    Task<ImmutableArray<LifecycleActionOffer>> Discover(NpgsqlConnection connection,LifecycleReadContext context,LifecycleTarget? target,CancellationToken ct);
    Task<LifecycleActionReview> Preview(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct);
    Task<LifecycleActionWrite> Execute(NpgsqlConnection connection,LifecycleActionRequest request,CancellationToken ct);
}
internal sealed class LifecycleActionRegistry
{
    private readonly ImmutableDictionary<string,ILifecycleActionHandler> handlers;
    internal LifecycleActionRegistry(IEnumerable<ILifecycleActionHandler> handlers)=>this.handlers=handlers.ToImmutableDictionary(h=>h.Kind,StringComparer.Ordinal);
    internal ILifecycleActionHandler Require(string kind)=>handlers.GetValueOrDefault(kind)
        ??throw new MarketDataException("lifecycle_action_unsupported","The selected native lifecycle handler is not registered.");
    internal IEnumerable<ILifecycleActionHandler> Handlers=>handlers.Values.OrderBy(h=>h.Kind,StringComparer.Ordinal);
}
