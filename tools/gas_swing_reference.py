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


def marked_period(first_day, length, *, election=None, forward_shift=None, vol_shift=None, clock_offset=0):
    """Independent two-change stopping problem on the specified UTC fixture, per held reference unit.

    Strip the unconditional expected base parcel using the martingale property.
    Only the incremental hundred-unit choices remain. Continue between decision dates
    with the nine displacement probabilities of four trinomial transitions.
    """
    from datetime import date, timedelta
    from decimal import localcontext

    with localcontext() as ctx:
        ctx.prec = 80
        one = D(1)
        h = (one / (4 * 365) * 3).sqrt()
        six = D(6)
        start = date(2041,1,1)
        rows = []
        base = D(0)
        reserved = D('0.5') if election is not None else D(0)
        # On each of these complete months 1,005 times the actual number of days
        # admits one full net upward increment. The seasonal cap admits the same one.
        for offset in range(first_day,first_day+length):
            if offset == election:
                continue
            month = (start+timedelta(days=offset)).month
            f = D('2.95' if month%2 else '3.05')
            sigma = D('0.20' if month%2 else '0.25')
            if forward_shift and month == forward_shift[0]:
                f += D(forward_shift[1])
            if vol_shift and month == vol_shift[0]:
                sigma += D(vol_shift[1])
            rows.append((1+4*(offset-clock_offset),f,sigma))
            base += f-3

        @cache
        def kernel(ticks):
            # Integer coefficients of (1+4x+x²)^ticks, from its logarithmic derivative.
            half=[1]
            for i in range(ticks):
                previous=half[i-1] if i else 0
                half.append((4*(ticks-i)*half[i]+(2*ticks-i+1)*previous)//(i+1))
            weights=half+half[-2::-1]
            denominator=6**ticks
            assert sum(weights)==denominator
            return tuple(D(w)/denominator for w in weights)

        def expectation(values, gap):
            weights=kernel(gap)
            return [sum((w*values[i+k] for k,w in enumerate(weights)),D(0))
                    for i in range(len(values)-2*gap)]

        # With two changes allowance, the state is no change, first down, or first up.
        # Once both choices have been made all further incremental rewards vanish.
        states = (reserved,) if election is not None else (D(0),D(-1),D(1))
        following = {}
        following_tick = None
        for tick,f,sigma in reversed(rows):
            jump = (sigma*h).exp()
            growth = jump**(-tick)
            denominator = ((1/jump+4+jump)/six)**tick
            increments=[]
            for node in range(2*tick+1):
                increments.append((f*growth/denominator-3)/10)
                growth *= jump
            if following_tick is not None:
                following = {state:expectation(values,following_tick-tick) for state,values in following.items()}
            else:
                following = {state:[D(0)]*len(increments) for state in states}
            current = {}
            for state in states:
                best=following[state].copy()
                for move in (-1,1):
                    if state+move>1:
                        continue
                    future=following[D(move)] if state==0 and election is None else None
                    for i,reward in enumerate(increments):
                        candidate=(reward if move==1 else -reward)+(future[i] if future else 0)
                        if candidate>best[i]:
                            best[i]=candidate
                current[state]=best
            following,following_tick=current,tick
        if not rows:
            return base
        opening=sum((p*v for p,v in zip(kernel(following_tick),following[reserved],strict=True)),D(0))
        return +(base+opening)


@cache
def long_period(first, length, election, price, volatility, clock_offset):
    return marked_period(first,length,election=election,forward_shift=price,vol_shift=volatility,clock_offset=clock_offset)


def long_reference(length, *, price=None, volatility=None, election=None, missed=False):
    from calendar import monthrange
    from decimal import localcontext
    with localcontext() as ctx:
        ctx.prec=80
        lengths=[length] if length==90 else [monthrange(2041,m)[1] for m in range(1,13)]
        first=0
        parts=[]
        for month,n in enumerate(lengths,1):
            individual=lambda shift:shift if shift and (length==90 or shift[0]==month) else None
            if not missed or month>1:
                parts.append(long_period(first,n,election,individual(price),individual(volatility),2 if missed else 0))
            first+=n
        assert first==length
        return sum(parts),tuple(parts)


def check_long_value(length, actual, **changes):
    from decimal import localcontext
    with localcontext() as ctx:
        ctx.prec=80
        exact,parts=long_reference(length,**changes)
        steps=1+4*(length-1)
        # The fixture has E[multiplier]=1, |A|<4, |C|<=3.3, sigma<=0.26.
        # In 10^-30 units the jump error is <3 (the sqrt derivative at
        # 3/1460 is <12). Over t steps, exponent error is <3t atoms.
        # The side and middle probabilities have relative errors <=2 atoms.
        # Each prefix factor is >=1, its two exponentials <1.02, so its
        # ordered rounding and the jump/probability errors cost <10 relative
        # atoms per transition. Taking products through t<=1457 bounds path
        # and multiplier distortion by 32(t+1) atoms, including exp/division.
        # E[absolute daily reward] <=4*E[multiplier]+3.3 <8. Max and
        # expectation accumulate this bound over days. The factor 9 also
        # covers the three separately rounded branches and immediate rewards.
        finite=D(32)*9*length*(steps+1)*D('1e-30')
        limit=D('0.5e-18')+finite
        actual_unit=D(actual)/1000
        if abs(actual_unit-exact)>limit:
            raise AssertionError(f'{length} normalized root {actual_unit} outside {exact} +/- {limit}')
        rounded_parts=sum(p.quantize(D('1e-18')) for p in parts)
        if abs(actual_unit-rounded_parts)>limit+len(parts)*D('0.5e-18'):
            raise AssertionError('Monthly optima at inception exceeded the separate root-rounding envelope')
        return parts
