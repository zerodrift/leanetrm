"""Generates futures-option-oracle.json: reference exp/ln/sqrt values,
Black-76 European option values on a future and American option values on a
strike-centred (Leisen-Reimer) tree.

Uses only the standard library `decimal` at 60 digits, rounded half-even to
30 fractional digits. Run: python3 -I tests/fixtures/futures_option_oracle.py
Importers call black76 inside a 60-digit decimal context of their own.
"""
import functools
import json
import pathlib
from decimal import Decimal, ROUND_HALF_EVEN, getcontext, localcontext

QUANTUM = Decimal(1).scaleb(-30)

POSITIVE = ["0.000000000001", "0.00000001", "0.000001", "0.001", "0.01", "0.05",
            "0.1", "0.25", "0.5", "0.75", "0.9", "0.999999", "1", "1.000001",
            "1.1", "1.5", "2", "2.5", "3", "3.141592653589793", "5", "7.5",
            "10", "12.34", "20", "25", "35", "42.5", "50"]
# 55.262 lies just below the top of exp's range: its value is close to 10^24.
EXPONENTS = POSITIVE + ["55", "55.2", "55.262"]
# Range edges: the smallest representable value and values near 10^24.
EDGES = ["0.000000000000000000000000000001", "123456789012345678901234"]
NEGATIVE = ["-0.000001", "-0.01", "-0.1", "-0.5", "-1", "-2", "-3.7", "-5", "-10",
            "-20", "-35", "-50"]


def q(value):
    return format(value.quantize(QUANTUM, rounding=ROUND_HALF_EVEN), "f")


def rows(inputs, fn):
    return [{"x": x, "y": q(fn(Decimal(x)))} for x in inputs]


# Black-76 cases: futures, strike, volatility, years, discount factor. Moneyness
# F/K spans 0.5-2, volatility 0.1-3 and years 0.01-3; each years value is a
# whole number of seconds under act/365 fixed. Synthetic natural-gas-like
# (F near 2-6) and crude-like (F near 60-150) levels.
BLACK76 = [
    ("3", "3", "0.5", "0.25", "0.99"),
    ("3.5", "3", "0.6", "0.1", "0.995"),
    ("2", "4", "1", "0.5", "0.98"),
    ("6", "3", "0.8", "1", "0.96"),
    ("4.25", "4.5", "1.5", "0.01", "0.9995"),
    ("2.75", "2.5", "0.35", "2", "0.92"),
    ("80", "80", "0.3", "0.25", "0.99"),
    ("72.5", "90", "0.4", "0.5", "0.975"),
    ("95", "60", "0.25", "1", "0.95"),
    ("60", "120", "0.6", "2", "0.9"),
    ("150", "75", "0.1", "0.05", "0.998"),
    ("85", "82.5", "1.2", "0.01", "0.9998"),
    # High corner: large F*sigma*sqrt(T), where absolute discretisation error peaks.
    ("150", "150", "1", "2", "0.9"),
    ("150", "150", "3", "0.25", "0.99"),
    ("100", "130", "1.5", "0.5", "0.97"),
    ("200", "180", "2", "1", "0.95"),
    ("120", "100", "1.2", "3", "0.88"),
]


# Leisen-Reimer cases: right, futures, strike, volatility, years, discount
# factor, steps (odd). One and three steps are small enough to follow by hand;
# the rest use the pinned 401 steps at gas-like and crude-like levels: at the
# money, a put far in the money under a heavy discount, a high volatility, a
# long maturity, a strike about forty deviations away, and a futures price
# that puts d2 within 1e-13 of zero, where the inversion is nearly one half.
LEISEN_REIMER = [
    ("call", "3", "3", "0.5", "0.25", "0.99", 1),
    ("put", "3", "3.5", "0.5", "0.25", "0.9", 1),
    ("put", "100", "100", "0.446287102628419511532590180620", "0.5", "0.81", 3),
    ("call", "6", "3", "0.8", "1", "0.96", 3),
    ("call", "3", "3", "0.5", "0.25", "0.99", 401),
    ("put", "3.5", "3", "0.6", "0.1", "0.995", 401),
    ("put", "60", "120", "0.6", "2", "0.9", 401),
    ("call", "150", "150", "3", "0.25", "0.99", 401),
    ("put", "120", "100", "1.2", "3", "0.88", 401),
    ("call", "80", "80", "0.3", "0.25", "1", 401),
    ("put", "85", "82.5", "1.2", "0.01", "0.9998", 401),
    ("call", "3", "3.3", "0.05", "0.0025", "0.9999", 401),
    ("call", "3.095230222497385393571805879512", "3", "0.5", "0.25", "1", 401),
]

# Black-76 at a unit discount for two calls two deviations out of the money, at
# a volatility times root time of one and of two: futures, strike, volatility,
# years.
BLACK76_WIDE = [
    ("60", "443.3434", "1", "1"),
    ("60", "3275.889", "2", "1"),
]


def peizer_pratt(deviations, steps):
    """The Peizer-Pratt method 2 inversion: the probability whose binomial
    distribution over an odd number of steps has, above its middle, the
    normal distribution's mass below the given number of deviations."""
    scaled = deviations / (steps + Decimal(1) / 3 + Decimal("0.1") / (steps + 1))
    half = (1 - (-scaled * scaled * (steps + Decimal(1) / 6)).exp()).sqrt() / 2
    return Decimal("0.5") + (half if deviations >= 0 else -half)


def leisen_reimer(right, futures, strike, volatility, years, discount, steps):
    """An American option on a future on the Leisen-Reimer tree, by backward
    induction with exercise at every node. The up probability is the
    inversion at d2; the up and down factors are the ratios of the inversions
    at d1 and d2 and of their complements, so the tree is a martingale and
    its two middle terminal nodes lie either side of the strike."""
    f, k, s, t, df = (Decimal(v) for v in (futures, strike, volatility, years, discount))
    spread = s * t.sqrt()
    d1 = ((f / k).ln() + spread * spread / 2) / spread
    up_probability, tilted = peizer_pratt(d1 - spread, steps), peizer_pratt(d1, steps)
    up, down = tilted / up_probability, (1 - tilted) / (1 - up_probability)
    ups, downs = [up ** n for n in range(steps + 1)], [down ** n for n in range(steps + 1)]
    step_discount = (df.ln() / steps).exp()

    def intrinsic(step, node):
        price = f * ups[step - node] * downs[node]
        return max(price - k if right == "call" else k - price, Decimal(0))

    values = [intrinsic(steps, node) for node in range(steps + 1)]
    for step in range(steps - 1, -1, -1):
        values = [max(step_discount * (up_probability * values[node] + (1 - up_probability) * values[node + 1]),
                      intrinsic(step, node)) for node in range(step + 1)]
    return values[0]


def leisen_reimer_row(right, futures, strike, volatility, years, discount, steps):
    return {"right": right, "futures": futures, "strike": strike, "volatility": volatility, "years": years,
            "discount": discount, "steps": steps,
            "value": q(leisen_reimer(right, futures, strike, volatility, years, discount, steps))}


def pi():
    """pi from Machin's formula, 16*atan(1/5) - 4*atan(1/239), at the current precision."""
    return machin_pi(getcontext().prec)


@functools.cache
def machin_pi(precision):
    def atan_inverse(n):
        total, power, k, sign = Decimal(0), Decimal(1) / n, 1, 1
        while power:
            total += sign * power / k
            power /= n * n
            k, sign = k + 2, -sign
        return total
    with localcontext() as ctx:
        ctx.prec = precision + 10
        value = 16 * atan_inverse(5) - 4 * atan_inverse(239)
    with localcontext() as ctx:
        ctx.prec = precision
        return +value


def erf(x):
    """erf by its Taylor series, 2/sqrt(pi) * sum (-1)^k x^(2k+1) / (k! (2k+1)).

    The terms grow to about exp(x^2) before they shrink, so the series runs at
    70 + x^2/ln(10) digits to keep more than 60 after cancellation, and stops
    once a term falls below 10^-(precision + 5). Beyond |x| = 10,
    1 - |erf(x)| < 3e-45, so erf saturates to +/-1 there.
    """
    if abs(x) > 10:
        return Decimal(1).copy_sign(x)
    with localcontext() as ctx:
        ctx.prec = 71 + int(x * x / Decimal(10).ln())
        square, term, total, k = x * x, x, x, 0
        while True:
            k += 1
            term = -term * square / k
            step = term / (2 * k + 1)
            if abs(step) < Decimal(10) ** -(ctx.prec + 5):
                break
            total += step
        value = 2 * total / pi().sqrt()
    return +value


def ncdf(x):
    return (1 + erf(x / Decimal(2).sqrt())) / 2


def black76(futures, strike, volatility, years, discount):
    f, k, s, t, df = (Decimal(v) for v in (futures, strike, volatility, years, discount))
    spread = s * t.sqrt()
    d1 = ((f / k).ln() + spread * spread / 2) / spread
    d2 = d1 - spread
    call = df * (f * ncdf(d1) - k * ncdf(d2))
    put = df * (k * ncdf(-d2) - f * ncdf(-d1))
    return call, put


def black76_row(futures, strike, volatility, years, discount):
    call, put = black76(futures, strike, volatility, years, discount)
    forward_call, forward_put = black76(futures, strike, volatility, years, "1")
    return {"futures": futures, "strike": strike, "volatility": volatility,
            "years": years, "discount": discount, "call": q(call), "put": q(put),
            "undiscounted_call": q(forward_call), "undiscounted_put": q(forward_put)}


def main():
    getcontext().prec = 60
    oracle = {
        "exp": rows(EXPONENTS + NEGATIVE, lambda x: x.exp()),
        "ln": rows(POSITIVE + EDGES, lambda x: x.ln()),
        "sqrt": rows(POSITIVE + EDGES, lambda x: x.sqrt()),
        "black76": [black76_row(*case) for case in BLACK76],
        "leisen_reimer": [leisen_reimer_row(*case) for case in LEISEN_REIMER],
        "black76_wide": [{"futures": f, "strike": k, "volatility": s, "years": t, "undiscounted_call": q(black76(f, k, s, t, "1")[0])}
                         for f, k, s, t in BLACK76_WIDE],
    }
    path = pathlib.Path(__file__).with_name("futures-option-oracle.json")
    path.write_text(json.dumps(oracle, indent=2) + "\n")


if __name__ == "__main__":
    main()
