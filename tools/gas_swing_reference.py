"""Independent finite swing reference using forward probability distributions between decision dates."""
from decimal import Decimal as D
from functools import cache
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOLERANCE = D('0.00000001')


def distribution(durations, spacing):
    """Distribution of the factor displacement from the beginning of this block."""
    weights = [D(1)]
    for elapsed in durations:
        side = D(elapsed) / (6 * spacing)
        middle = 1 - 2 * side
        weights = [side * a + middle * b + side * c for a, b, c in
                   zip(weights + [0, 0], [0] + weights + [0], [0, 0] + weights)]
    return list(zip(range(-(len(weights) // 2), len(weights) // 2 + 1), weights))


def reference(case, spacing):
    days = case['Days']
    ends = [d['Seconds'] for d in days]
    ticks = sorted({0, *ends, *range(ends[0] % spacing, ends[-1] + 1, spacing)})
    blocks = []
    payoffs = []
    h = (D(3 * spacing) / (365 * 86400)).sqrt()
    previous = 0
    durations = [b - a for a, b in zip(ticks, ticks[1:])]
    for day, end in zip(days, ends):
        stop = ticks.index(end)
        blocks.append(distribution(durations[previous:stop], spacing))
        weights = distribution(durations[:stop], spacing) if previous else blocks[-1]
        sigma = D(day['Volatility'])
        growth = {j: (sigma * h * j).exp() for j, _ in weights}
        # Compute E[exp(sigma W)] from the full marginal law, independently of stepwise normalization.
        mean = sum(p * growth[j] for j, p in weights)
        money = D('1e-18')
        amount, fixed = (D(day[k]).quantize(money) for k in ('Uncertain', 'Certain'))
        payoffs.append({j: amount * factor / mean + fixed for j, factor in growth.items()})
        previous = stop

    @cache
    def decision(d, node, used):
        values = []
        for k in range(case['Low'], case['High'] + 1):
            total = used + k
            if d + 1 == len(days):
                if case['Minimum'] <= total <= case['Maximum']:
                    values.append(k * payoffs[d][node])
            else:
                outcomes = [(p, decision(d + 1, node + move, total)) for move, p in blocks[d + 1]]
                if all(v is not None for _, v in outcomes):
                    values.append(k * payoffs[d][node] + sum(p * v for p, v in outcomes))
        return max(values, default=None)

    return sum(p * decision(0, node, case['Committed']) for node, p in blocks[0])


def day(amount, fixed, vol, seconds):
    return dict(Uncertain=str(amount), Certain=str(fixed), Volatility=vol, Seconds=seconds)


def fixture(days, low=0, high=1, minimum=0, maximum=1, committed=0):
    return dict(Days=days, Low=low, High=high, Minimum=minimum, Maximum=maximum, Committed=committed)


def analytic(forward, strike):
    vol_time = 0.5 * math.sqrt(30 / 365)
    d1 = math.log(forward / strike) / vol_time + vol_time / 2
    def normal(x):
        return math.erfc(-x / math.sqrt(2)) / 2
    return D(str(10000 * (forward * normal(d1) - strike * normal(d1 - vol_time))))
