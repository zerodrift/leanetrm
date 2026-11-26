#!/usr/bin/env python3
"""Independent unit-reference swing checks using 80-place marginal distributions."""
import argparse
from datetime import date, timedelta
from decimal import Decimal as D, localcontext
import json
from pathlib import Path
import subprocess
import tempfile

from gas_swing_reference import ROOT, TOLERANCE, analytic, distribution

ATOM = D('1e-18')


def value(case, spacing):
    days = case['Days']
    stops = [row['Seconds'] for row in days]
    ticks = sorted({0, *stops, *range(stops[0] % spacing, stops[-1] + 1, spacing)})
    durations = [b - a for a, b in zip(ticks, ticks[1:])]
    h = (D(3 * spacing) / (365 * 86400)).sqrt()
    transitions, payoffs, periods = [], [], []
    elected, changed = {}, {}
    previous = 0
    for i, (row, stop) in enumerate(zip(days, stops, strict=True)):
        business_day = date(2041, 1, case.get('FirstDay', 31)) + timedelta(days=i)
        period = business_day.month if case.get('Reset') else 0
        if 'Election' in row:
            amount = D(row['Election'])
            elected[period] = elected.get(period, 0) + amount
            changed[period] = changed.get(period, 0) + int(amount != 1)
            continue
        periods.append(period)
        end = ticks.index(stop)
        transitions.append(distribution(durations[previous:end], spacing))
        law = distribution(durations[:end], spacing) if previous else transitions[-1]
        exp = {j: (h * D(row['Sigma']) * j).exp() for j, _ in law}
        mean = sum(p * exp[j] for j, p in law)
        payoffs.append({j: [(D(row['Physical']) * q).quantize(ATOM) * growth / mean
                             + (D(row['Fixed']) * q).quantize(ATOM) for q in case['Choices']]
                        for j, growth in exp.items()})
        previous = end

    # Enumerate the complete first decision at each observed history. Conditional
    # last-date choices obey the count and term cap left by that history.
    def best(day, node, total, changes):
        possible = []
        for rank, q in enumerate(case['Choices']):
            used = changes + int(q != 1)
            amount = total + q
            if used > case['Count'] or amount > case['Upper']:
                continue
            cash = payoffs[day][node][rank]
            if day + 1 < len(payoffs):
                new_period = periods[day + 1]
                next_total, next_count = ((amount, used) if new_period == periods[day] else
                                          (elected.get(new_period, 0), changed.get(new_period, 0)))
                future = [(p, best(day + 1, node + j, next_total, next_count)) for j, p in transitions[day + 1]]
                if any(v is None for _, v in future):
                    continue
                cash += sum(p * v for p, v in future)
            possible.append(cash)
        return max(possible, default=None)

    root = sum(p * best(0, node, elected.get(periods[0], 0), changed.get(periods[0], 0)) for node, p in transitions[0])
    return root.quantize(ATOM) * D(case['Held'])


def run(dotnet):
    def row(seconds, physical, fixed, sigma):
        return dict(Seconds=seconds, Physical=physical, Fixed=fixed, Sigma=sigma)
    pairs = [(3.2, 3.0), (3.2, 3.5), (3.0, 3.0)]
    cases = [dict(Days=[row(30*86400,str(f),str(-k),'0.5')], Choices=[0,1], Count=1,
                  Upper=1, Held='10000') for f,k in pairs]
    for sign in (1, -1):
        cases.append(dict(Days=[row(7200,str(sign*3),str(-sign*3),'0.33'),
                                row(90000,str(sign*4),str(-sign*4),'0.71')],
                          Choices=[0,1,2], Count=1, Upper=3, Held='1000'))
    for reset in (False, True):
        cases.append(dict(Days=[dict(row(3600,'0','0','0'), Election='1.5'),
                                row(7200,'2.5','-2.49','0.33'), row(90000,'3.5','-3.52','0.71')],
                          FirstDay=30, Reset=reset, Choices=[0,1,2], Count=2, Upper=4, Held='1000'))
    cases.append(dict(Days=[row(7200,'1.0000000000000000015','-1','0.5')],
                      Choices=[0,1,2], Count=1, Upper=2, Held='1', Exact=True))
    cases.append(dict(Days=[row(21600,'0.00000000000000000001','0.0000000000000000005','0.2')],
                      Choices=[0,1,2], Count=1, Upper=2, Held='1', Exact=True))
    dll = ROOT / 'tests/OpenEtrm.MarketData.Checks/bin/Debug/net10.0/OpenEtrm.MarketData.Checks.dll'
    with tempfile.TemporaryDirectory(prefix='openetrm-swing-unit-') as tmp:
        inputs, outputs = (Path(tmp) / name for name in ('inputs.json','outputs.json'))
        inputs.write_text(json.dumps(cases))
        subprocess.run([str(dotnet),str(dll),'--gas-swing-unit-oracles',str(inputs),str(outputs)],
                       cwd=ROOT,check=True,timeout=90)
        actuals = list(map(D,json.loads(outputs.read_text())))
    with localcontext() as context:
        context.prec = 80
        for case, actual in zip(cases,actuals,strict=True):
            expected = value(case,21600)
            if abs(actual - expected) > TOLERANCE or case.get('Exact') and actual != expected:
                raise AssertionError(f'Unit-reference swing finite value {actual} differs from {expected}')
        for case, actual, (forward,strike) in zip(cases[:3],actuals[:3],pairs,strict=True):
            fine, exact = value(case,2700), analytic(forward,strike)
            print(f'F={forward:.2f} K={strike:.2f} six-hour {actual:.9f} finer {fine:.9f} analytic {exact:.9f}')
            if abs(actual-exact)>2 or abs(fine-exact)>D('0.25'):
                raise AssertionError(f'Analytic/finer swing comparison failed: {actual}, {fine}, {exact}')
    print(f'PASS {len(cases)} unit-reference cases against 80-digit adapted finite values and analytic/finer gates')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet',type=Path,required=True)
    run(parser.parse_args().dotnet.resolve())
