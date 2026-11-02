"""Generates futures-option-oracle.json: reference exp/ln/sqrt values and
Black-76 European option values on a future.

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
    }
    path = pathlib.Path(__file__).with_name("futures-option-oracle.json")
    path.write_text(json.dumps(oracle, indent=2) + "\n")


if __name__ == "__main__":
    main()
