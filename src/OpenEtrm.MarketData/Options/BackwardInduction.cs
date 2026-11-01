using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Options;

// A discrete-time scenario tree. Steps are periods: nodes exist at steps 0..Steps,
// step 0 has exactly one node and step Steps is terminal. Next returns the
// explicit successors of a node at step+1 (no recombination is assumed), and
// Discount(step) is the one-step factor applied when rolling values back from
// step+1 to step, defined for steps 0..Steps-1.
public interface ILattice
{
    int Steps { get; }
    int Nodes(int step);
    LatticeDecimal Underlying(int step, int node);
    (int Node, LatticeDecimal Probability)[] Next(int step, int node);
    LatticeDecimal Discount(int step);
}

// The rights and obligations valued on a lattice. A state is the contract's own
// memory (for example "not yet exercised"). At each non-terminal node and state
// the contract offers decisions in a fixed order; each earns an immediate value
// and moves to a next state, which must be one of States.
public interface IInductionContract<TState> where TState : struct, Enum
{
    // The first state is the start state whose value at the root is reported.
    ImmutableArray<TState> States { get; }
    LatticeDecimal Terminal(LatticeDecimal underlying, TState state);
    ImmutableArray<InductionDecision<TState>> Decisions(int step, LatticeDecimal underlying, TState state);
}

public sealed record InductionDecision<TState>(string Code, LatticeDecimal Immediate, TState Next);

// Chosen[step][node][state] is the code of the decision that maximises value at
// that node and state, for steps 0..Steps-1.
public sealed record InductionResult<TState>(
    LatticeDecimal Root,
    ImmutableArray<ImmutableArray<ImmutableDictionary<TState, string>>> Chosen) where TState : struct, Enum;

public static class BackwardInduction
{
    // Value = max over decisions of Immediate + Discount(step) * sum(p * value of
    // the decision's next state at each successor). Ties between equal values pick
    // the first decision in declared order. Cancellation is checked once per step.
    public static InductionResult<TState> Solve<TState>(ILattice lattice, IInductionContract<TState> contract, CancellationToken ct)
        where TState : struct, Enum
    {
        var steps = lattice.Steps;
        if (steps < 1) throw Invalid($"A lattice needs at least one step; got {steps}.");
        if (lattice.Nodes(0) != 1) throw Invalid("Step 0 must have exactly one node.");
        for (var step = 1; step <= steps; step++)
            if (lattice.Nodes(step) < 1) throw Invalid($"Step {step} has no nodes.");
        var states = contract.States;
        if (states.IsDefaultOrEmpty) throw Invalid("The contract declares no states.");
        if (states.Distinct().Count() != states.Length) throw Invalid("The contract declares a state more than once.");
        var stateIndex = states.Select((state, index) => (state, index)).ToDictionary(pair => pair.state, pair => pair.index);

        var values = new LatticeDecimal[lattice.Nodes(steps)][];
        for (var node = 0; node < values.Length; node++)
        {
            var underlying = lattice.Underlying(steps, node);
            values[node] = states.Select(state => contract.Terminal(underlying, state)).ToArray();
        }

        var chosen = new ImmutableArray<ImmutableDictionary<TState, string>>[steps];
        for (var step = steps - 1; step >= 0; step--)
        {
            ct.ThrowIfCancellationRequested();
            var discount = lattice.Discount(step);
            var nextNodes = values.Length;
            var current = new LatticeDecimal[lattice.Nodes(step)][];
            var choices = ImmutableArray.CreateBuilder<ImmutableDictionary<TState, string>>(current.Length);
            for (var node = 0; node < current.Length; node++)
            {
                var successors = CheckedSuccessors(lattice, step, node, nextNodes);
                var underlying = lattice.Underlying(step, node);
                current[node] = new LatticeDecimal[states.Length];
                var nodeChoices = ImmutableDictionary.CreateBuilder<TState, string>();
                foreach (var state in states)
                {
                    var decisions = contract.Decisions(step, underlying, state);
                    if (decisions.IsDefaultOrEmpty) throw Invalid($"No decisions for state {state} at step {step}, node {node}.");
                    InductionDecision<TState>? best = null;
                    var bestValue = default(LatticeDecimal);
                    foreach (var decision in decisions)
                    {
                        if (!stateIndex.TryGetValue(decision.Next, out var next))
                            throw Invalid($"Decision {decision.Code} leads to undeclared state {decision.Next}.");
                        var continuation = successors.Aggregate(
                            default(LatticeDecimal), (sum, s) => sum + s.Probability * values[s.Node][next]);
                        var value = decision.Immediate + discount * continuation;
                        if (best is null || value.CompareTo(bestValue) > 0) { best = decision; bestValue = value; }
                    }
                    current[node][stateIndex[state]] = bestValue;
                    nodeChoices[state] = best!.Code;
                }
                choices.Add(nodeChoices.ToImmutable());
            }
            chosen[step] = choices.MoveToImmutable();
            values = current;
        }
        return new InductionResult<TState>(values[0][0], [.. chosen]);
    }

    private static (int Node, LatticeDecimal Probability)[] CheckedSuccessors(ILattice lattice, int step, int node, int nextNodes)
    {
        var successors = lattice.Next(step, node) ?? throw Invalid($"Step {step}, node {node} has no successor list.");
        var total = default(LatticeDecimal);
        foreach (var (next, probability) in successors)
        {
            if (next < 0 || next >= nextNodes)
                throw Invalid($"Successor {next} of step {step}, node {node} is outside 0..{nextNodes - 1}.");
            if (probability.CompareTo(default) < 0)
                throw Invalid($"Negative probability {probability} at step {step}, node {node}.");
            total += probability;
        }
        if (total != LatticeDecimal.Parse("1"))
            throw Invalid($"Probabilities at step {step}, node {node} sum to {total}, not 1.");
        return successors;
    }

    private static MarketDataException Invalid(string message) => new("induction_contract", message);
}
