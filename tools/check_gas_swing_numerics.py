#!/usr/bin/env python3
"""Independent finite swing reference using forward probability distributions between decision dates."""
import argparse
from decimal import Decimal as D, localcontext
from functools import cache
import json
import math
from pathlib import Path
import subprocess
import tempfile

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


def run(dotnet):
    pairs = [(32000, 30000), (32000, 35000), (30000, 30000)]
    cases = [fixture([day(f, -k, '0.5', 30 * 86400)]) for f, k in pairs]
    # Anchor the grid at Jan 31 03:00, split a step at Feb 1 02:00. The holder can receive or deliver.
    for sign in (1, -1):
        days = [day(sign * 3199, -sign * 3200, '0.33', 30 * 86400 + 10800),
                day(sign * 3001, -sign * 3000, '0.71', 31 * 86400 + 7200)]
        cases.append(fixture(days, 1, 3, 6, 7, 2))
    cases.append(fixture([day('1.0000000000000000015', '-1', '0.5', 7200)]))
    dll = ROOT / 'tests/OpenEtrm.MarketData.Checks/bin/Debug/net10.0/OpenEtrm.MarketData.Checks.dll'
    with tempfile.TemporaryDirectory(prefix='openetrm-swing-') as tmp:
        inputs, outputs = (Path(tmp) / name for name in ('cases.json', 'results.json'))
        inputs.write_text(json.dumps(cases))
        subprocess.run([str(dotnet), str(dll), '--gas-swing-oracles', str(inputs), str(outputs)],
                       cwd=ROOT, check=True, timeout=90)
        observed = list(map(D, json.loads(outputs.read_text())))
    with localcontext() as ctx:
        ctx.prec = 80
        for case, actual in zip(cases, observed, strict=True):
            expected = reference(case, 21600)
            assert abs(actual - expected) <= TOLERANCE, (case, actual, expected)
        for case, (f, k), actual in zip(cases[:3], pairs, observed[:3], strict=True):
            exact = analytic(f / 10000, k / 10000)
            fine = reference(case, 2700)
            assert abs(actual - exact) <= 2 and abs(fine - exact) <= D('0.25'), (actual, fine, exact)
            print(f'PASS swing F={f / 10000:.2f} K={k / 10000:.2f}: six-hour {actual:.9f}, '
                  f'45-minute {fine:.9f}, analytic {exact:.9f}')
    print(f'PASS {len(cases)} swing cases against the 80-digit finite-grid oracle within {TOLERANCE:f}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', type=Path, required=True)
    run(parser.parse_args().dotnet.resolve())
