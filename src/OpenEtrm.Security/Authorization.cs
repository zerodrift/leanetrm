using System.Collections.Immutable;

namespace OpenEtrm.Security;

public enum PrincipalKind { Human = 1, Service, Agent }
public enum LoginKind { HumanInteractive = 1, ServiceCredential, AgentCredential }
public enum Permission
{
    ReadMarketData = 1, WriteMarketDefinitions, SealMarketDefinitions,
    ImportObservations, WriteReferenceData, SealReferenceData,
    ManageIdentities, ManageAccess, CreateDelegations,
    BookTrade, AmendTrade, RunValuation, RunScenario, RunDealRisk, ApproveMarketData, PublishMarketData, ReadTrades,
    ReadTradeParties, WriteTradeParties, RunJobs, ManageJobs, LaunchWorkloads, ManageInfrastructure, ManageModelProviders,
    ManageAgents, UseAgents, ReadJobs
}
public sealed record Principal(Guid Id, PrincipalKind Kind, bool Enabled);
public readonly record struct Scope(Guid TenantId, Guid WorkspaceId);
public sealed record IdentityBinding(string Issuer, string Subject, Guid PrincipalId, LoginKind Kind);
public sealed record Role(Guid Id, ImmutableHashSet<Permission> Permissions);
public sealed record RoleGrant(
    Guid Id, Guid PrincipalId, Guid RoleId, Scope Scope,
    DateTimeOffset From, DateTimeOffset To, bool Revoked);
public sealed record AgentDelegation(
    Guid Id, Guid AgentId, Guid DelegatorId, Scope Scope,
    ImmutableHashSet<Permission> Permissions,
    DateTimeOffset From, DateTimeOffset To, bool Revoked);
public sealed record AuthorizationDecision(
    Guid SecurityRevision, Guid ActorId, Guid EffectivePrincipalId,
    Scope Scope, Permission Permission, DateTimeOffset CheckedAt, Guid? DelegationId);
public sealed class AccessDeniedException(string reason) : Exception(reason);

// Evaluation of a supplied CURRENT security state. This is not token validation,
// sign-in, administration or transactional grant/revocation persistence.
public sealed class SecurityState
{
    public Guid Revision { get; }
    private readonly ImmutableDictionary<Guid, Principal> principals;
    private readonly ImmutableDictionary<(string, string), IdentityBinding> identities;
    private readonly ImmutableDictionary<Guid, Role> roles;
    private readonly ImmutableArray<RoleGrant> grants;
    private readonly ImmutableDictionary<Guid, AgentDelegation> delegations;

    public SecurityState(Guid revision, IEnumerable<Principal> principals,
        IEnumerable<IdentityBinding> identities, IEnumerable<Role> roles,
        ImmutableArray<RoleGrant> grants, IEnumerable<AgentDelegation> delegations)
    {
        if (revision == Guid.Empty || grants.IsDefault) throw new ArgumentException("Explicit current security revision/grants are required.");
        Revision = revision;
        this.principals = principals.ToImmutableDictionary(x => x.Id);
        var bindings = identities.ToImmutableArray();
        this.identities = bindings.ToImmutableDictionary(x => (x.Issuer, x.Subject));
        this.roles = roles.ToImmutableDictionary(x => x.Id);
        this.grants = grants;
        this.delegations = delegations.ToImmutableDictionary(x => x.Id);
        foreach (var principal in this.principals.Values)
            if (principal.Id == Guid.Empty || !Enum.IsDefined(principal.Kind)) throw new ArgumentException("Invalid principal.");
        foreach (var pair in this.identities)
            if (string.IsNullOrWhiteSpace(pair.Key.Item1) || string.IsNullOrWhiteSpace(pair.Key.Item2) || !this.principals.ContainsKey(pair.Value.PrincipalId))
                throw new ArgumentException("Identity binding requires exact issuer/subject and a principal.");
        foreach (var binding in bindings)
        {
            var expected = this.principals[binding.PrincipalId].Kind switch
            {
                PrincipalKind.Human => LoginKind.HumanInteractive,
                PrincipalKind.Service => LoginKind.ServiceCredential,
                PrincipalKind.Agent => LoginKind.AgentCredential,
                _ => throw new ArgumentException("Unsupported principal kind.")
            };
            if (binding.Kind != expected) throw new ArgumentException("Login kind must match principal kind.");
        }
        foreach (var role in this.roles.Values)
            if (role.Id == Guid.Empty || role.Permissions is null || role.Permissions.Any(p => !Enum.IsDefined(p)))
                throw new ArgumentException("Invalid role permissions.");
        if (grants.Select(x => x.Id).Distinct().Count() != grants.Length) throw new ArgumentException("Duplicate role grant.");
        foreach (var grant in grants)
        {
            ValidateScopeTime(grant.Scope, grant.From, grant.To);
            if (grant.Id == Guid.Empty || !this.principals.ContainsKey(grant.PrincipalId) || !this.roles.ContainsKey(grant.RoleId))
                throw new ArgumentException("Grant requires exact principal and role references.");
        }
        foreach (var delegation in this.delegations.Values)
        {
            ValidateScopeTime(delegation.Scope, delegation.From, delegation.To);
            if (delegation.Id == Guid.Empty || delegation.Permissions is null || delegation.Permissions.Any(p => !Enum.IsDefined(p)) ||
                !this.principals.TryGetValue(delegation.AgentId, out var agent) || agent.Kind != PrincipalKind.Agent ||
                !this.principals.TryGetValue(delegation.DelegatorId, out var delegator) || delegator.Kind == PrincipalKind.Agent)
                throw new ArgumentException("Delegation requires a non-agent delegator, an agent and explicit permissions.");
        }
    }

    private static void ValidateScopeTime(Scope scope, DateTimeOffset from, DateTimeOffset to)
    {
        if (scope.TenantId == Guid.Empty || scope.WorkspaceId == Guid.Empty || from.Offset != TimeSpan.Zero ||
            to.Offset != TimeSpan.Zero || from >= to)
            throw new ArgumentException("Exact tenant/workspace and a half-open UTC permission interval are required.");
    }

    // Call ONLY after verifying token signature, issuer, audience, expiry and
    // applicable authentication policy. Caller-supplied principal IDs are unsafe.
    public Guid ResolveVerifiedIdentity(string issuer, string subject, LoginKind verifiedLoginKind)
    {
        if (!identities.TryGetValue((issuer, subject), out var binding) || binding.Kind != verifiedLoginKind ||
            !principals[binding.PrincipalId].Enabled)
            throw new AccessDeniedException("Unbound or disabled identity.");
        return binding.PrincipalId;
    }

    public AuthorizationDecision AuthorizeCurrent(Guid authenticatedActor, Scope scope,
        Permission permission, DateTimeOffset currentUtc, Guid? delegationId)
    {
        if (currentUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(permission) ||
            scope.TenantId == Guid.Empty || scope.WorkspaceId == Guid.Empty)
            throw new AccessDeniedException("Explicit current UTC authorization context is required.");
        if (!principals.TryGetValue(authenticatedActor, out var actor) || !actor.Enabled ||
            !HasPermission(actor.Id, scope, permission, currentUtc))
            throw new AccessDeniedException("No current scoped grant.");
        var effective = actor.Id;
        if (actor.Kind == PrincipalKind.Agent)
        {
            if (delegationId is null || !delegations.TryGetValue(delegationId.Value, out var delegation) ||
                delegation.AgentId != actor.Id || delegation.Scope != scope || delegation.Revoked ||
                currentUtc < delegation.From || currentUtc >= delegation.To || !delegation.Permissions.Contains(permission) ||
                !principals[delegation.DelegatorId].Enabled ||
                !HasPermission(delegation.DelegatorId, scope, Permission.CreateDelegations, currentUtc) ||
                !HasPermission(delegation.DelegatorId, scope, permission, currentUtc))
                throw new AccessDeniedException("Agent requires current delegation and permissions held by both identities.");
            effective = delegation.DelegatorId;
        }
        else if (delegationId is not null) throw new AccessDeniedException("Delegation is supported only for agent actors.");
        return new(Revision, actor.Id, effective, scope, permission, currentUtc, delegationId);
    }

    private bool HasPermission(Guid principal, Scope scope, Permission permission, DateTimeOffset now) =>
        grants.Any(g => g.PrincipalId == principal && g.Scope == scope && !g.Revoked &&
            now >= g.From && now < g.To && roles[g.RoleId].Permissions.Contains(permission));
}
