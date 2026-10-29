using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;
using System.Globalization;

namespace OpenEtrm.Api;

internal static class RetainedScheduleLimits
{
    internal const long MaximumEntities=1_500_000;
    internal static long Require(SidedInstrument instrument,CancellationToken cancellation)
    {
        RequireGeometry(instrument,cancellation);
        var definitions=new List<ScheduleDefinition>();
        foreach(var side in instrument.Sides.Select(member=>member.Side)
            .DistinctBy(side=>(DefinitionIdentity.For(side).Canonical,side.PricingBindingIdentity?.Canonical)))
        {
            cancellation.ThrowIfCancellationRequested();
            definitions.Add(ScheduleDefinitionCompiler.Generate(side,cancellation));
        }
        return Require(definitions);
    }
    internal static long Require(IEnumerable<ScheduleDefinition> definitions)
    {
        long entities=0;
        foreach(var definition in definitions.DistinctBy(d=>(d.Family,d.EconomicIdentity,d.PricingBindingIdentity)))
            entities=checked(entities+definition.Rows.Count+definition.Edges.Count+definition.Bindings.Count+
                definition.Roots.Count+definition.Ports.Count+definition.Grids.Count);
        Require(entities);return entities;
    }
    internal static void RequireGeometry(SidedInstrument instrument,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(instrument is not PpaInstrument ppa)return;
        var clock=ppa.Memberships[0].Side.Terms.Schedule.Clock;
        var coverage=new DeliveryCoverage(clock.StartOfDay(ppa.Delivery.From).ToUniversalTime(),clock.StartOfDay(ppa.Delivery.To).ToUniversalTime());
        // Count the complete stored term before materializing its UTC columns.
        // A short trade binding cannot reduce the side's retained definition.
        PpaCompiler.Work([(ppa,coverage)],[],_=>clock);
        cancellation.ThrowIfCancellationRequested();
    }
    internal static void RequireFullPreparation(PpaTermsInput terms,PowerClock clock)
    {
        var first=clock.StartOfDay(PpaCompiler.Day(terms.Delivery.From)).ToUniversalTime();
        var last=clock.StartOfDay(PpaCompiler.Day(terms.Delivery.To)).ToUniversalTime();
        const string utc="yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";
        PpaCompiler.PreflightWork([(terms,first.ToString(utc,CultureInfo.InvariantCulture),last.ToString(utc,CultureInfo.InvariantCulture),"retained_full_term")]);
    }
    internal static void Require(long entities)
    {
        if(entities<1||entities>MaximumEntities)
            throw new MarketDataException("side_schedule_limit",
                FormattableString.Invariant($"A booking supports at most {MaximumEntities:N0} retained operator blocks, edges, bindings, roots, ports and grid headers in total; this selection requires {entities:N0}. Reduce its term or shape."));
    }
}
