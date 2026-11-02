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

// Chosen is present only when decisions were recorded.
public sealed record InductionResult<TState>(LatticeDecimal Root, InductionChoices<TState>? Chosen) where TState : struct, Enum;

// The decision that maximises value at each node and state, for steps 0..Steps-1.
// Each step holds one code id per (node, state) in a flat array, indexed by
// node * state count + the state's position in the contract's States.
public sealed class InductionChoices<TState> where TState : struct, Enum
{
    private readonly IReadOnlyDictionary<TState, int> stateIndex;
    private readonly string[] codes;
    private readonly int[][] chosen;

    internal InductionChoices(IReadOnlyDictionary<TState, int> stateIndex, string[] codes, int[][] chosen) =>
        (this.stateIndex, this.codes, this.chosen) = (stateIndex, codes, chosen);

    public int Steps => chosen.Length;
    public string Code(int step, int node, TState state) => codes[chosen[step][node * stateIndex.Count + stateIndex[state]]];
}

public static class BackwardInduction
{
    // Value = max over decisions of Immediate + Discount(step) * sum(p * value of
    // the decision's next state at each successor). Ties between equal values pick
    // the first decision in declared order. Cancellation is checked once per step.
    // Decision bookkeeping is allocated only when recordDecisions is true.
    public static InductionResult<TState> Solve<TState>(ILattice lattice, IInductionContract<TState> contract, CancellationToken ct, bool recordDecisions)
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
        var width = states.Length;
        var stateIndex = states.Select((state, index) => (state, index)).ToDictionary(pair => pair.state, pair => pair.index);

        // values[node * width + state index] at the step being rolled back into.
        var values = new LatticeDecimal[lattice.Nodes(steps) * width];
        for (var node = 0; node < values.Length / width; node++)
        {
            var underlying = lattice.Underlying(steps, node);
            for (var s = 0; s < width; s++) values[node * width + s] = contract.Terminal(underlying, states[s]);
        }

        var codeIds = recordDecisions ? new Dictionary<string, int>() : null;
        var chosen = recordDecisions ? new int[steps][] : null;
        for (var step = steps - 1; step >= 0; step--)
        {
            ct.ThrowIfCancellationRequested();
            var discount = lattice.Discount(step);
            var nextNodes = values.Length / width;
            var nodes = lattice.Nodes(step);
            var current = new LatticeDecimal[nodes * width];
            var choices = chosen is null ? null : chosen[step] = new int[nodes * width];
            for (var node = 0; node < nodes; node++)
            {
                var successors = CheckedSuccessors(lattice, step, node, nextNodes);
                var underlying = lattice.Underlying(step, node);
                for (var s = 0; s < width; s++)
                {
                    var decisions = contract.Decisions(step, underlying, states[s]);
                    if (decisions.IsDefaultOrEmpty) throw Invalid($"No decisions for state {states[s]} at step {step}, node {node}.");
                    InductionDecision<TState>? best = null;
                    var bestValue = default(LatticeDecimal);
                    foreach (var decision in decisions)
                    {
                        if (!stateIndex.TryGetValue(decision.Next, out var next))
                            throw Invalid($"Decision {decision.Code} leads to undeclared state {decision.Next}.");
                        var continuation = default(LatticeDecimal);
                        foreach (var (successor, probability) in successors)
                            continuation += probability * values[successor * width + next];
                        var value = decision.Immediate + discount * continuation;
                        if (best is null || value.CompareTo(bestValue) > 0) { best = decision; bestValue = value; }
                    }
                    current[node * width + s] = bestValue;
                    if (choices is not null) choices[node * width + s] = CodeId(codeIds!, best!.Code);
                }
            }
            values = current;
        }
        var recorded = chosen is null ? null
            : new InductionChoices<TState>(stateIndex, [.. codeIds!.OrderBy(pair => pair.Value).Select(pair => pair.Key)], chosen);
        return new InductionResult<TState>(values[0], recorded);
    }

    private static int CodeId(Dictionary<string, int> codeIds, string code)
    {
        if (!codeIds.TryGetValue(code, out var id)) codeIds[code] = id = codeIds.Count;
        return id;
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
