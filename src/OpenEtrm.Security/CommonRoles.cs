using System.Collections.Immutable;

namespace OpenEtrm.Security;

// Permission templates only: no automatic principal assignment or global scope.
public sealed record RoleTemplate(string Code, ImmutableHashSet<Permission> Permissions);
public static class CommonRoles
{
    private static RoleTemplate Template(string code, params Permission[] permissions) => new(code, permissions.ToImmutableHashSet());
    public static ImmutableArray<RoleTemplate> Templates { get; } =
    [
        Template("viewer", Permission.ReadMarketData, Permission.RunJobs, Permission.ReadJobs),
        Template("trader", Permission.ReadMarketData, Permission.ReadTrades, Permission.ReadTradeParties, Permission.BookTrade, Permission.AmendTrade, Permission.RunDealRisk, Permission.CreateDelegations, Permission.RunJobs, Permission.ReadJobs, Permission.UseAgents),
        Template("risk_analyst", Permission.ReadMarketData, Permission.ReadTrades, Permission.ReadTradeParties, Permission.RunValuation, Permission.RunScenario, Permission.RunDealRisk, Permission.CreateDelegations, Permission.RunJobs, Permission.ReadJobs, Permission.UseAgents),
        Template("market_data_editor", Permission.ReadMarketData, Permission.WriteMarketDefinitions, Permission.ImportObservations,
            Permission.WriteReferenceData, Permission.ReadTradeParties, Permission.WriteTradeParties, Permission.CreateDelegations, Permission.RunJobs, Permission.ReadJobs, Permission.UseAgents),
        Template("market_data_approver", Permission.ReadMarketData, Permission.SealMarketDefinitions, Permission.SealReferenceData,
            Permission.ApproveMarketData, Permission.PublishMarketData, Permission.RunJobs, Permission.ReadJobs),
        Template("access_administrator", Permission.ManageIdentities, Permission.ManageAccess, Permission.CreateDelegations, Permission.RunJobs, Permission.ReadJobs, Permission.ManageJobs, Permission.ManageModelProviders,
            Permission.ManageAgents),
        // Held only by agent principals (docs/ai-assistant.md §5.4); DraftCustomCurves is its own draft authority (D260).
        Template("agent_reader", Permission.ReadMarketData, Permission.ReadTrades, Permission.ReadTradeParties, Permission.ReadJobs, Permission.DraftCustomCurves)
    ];
}
