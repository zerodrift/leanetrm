"""Generates spread-option-oracle.json: reference values for a European call
on the spread of two futures, max(long - short - strike, 0).

`kirk` evaluates Kirk's approximation with the standard library `decimal` at
60 digits, rounded half-even to 30 fractional digits. `integral` is independent
of that approximation: it conditions on the short future, where the payoff is
a Black-76 call on the long future, and integrates over the short future's
normal driver by Simpson's rule in binary floating point. Its own error is
below 1e-9 of the long price; its distance from `kirk` is the approximation's
error at that case.

Synthetic power-like long prices (25-120 per MWh), fuel-like short prices
after the heat-rate factor, volatilities 0.2-1.5, correlations 0-0.95 and
times from one day to two years, each a whole number of seconds under act/365
fixed.

Run: python3 -I tests/fixtures/spread_option_oracle.py
"""
import json
import math
import pathlib
import sys
from decimal import Decimal, getcontext

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from futures_option_oracle import black76, ncdf, q  # noqa: E402

# long, short, strike, long volatility, short volatility, correlation, years, discount factor
CASES = [
    ("45", "28", "3", "0.6", "0.45", "0.8", "0.25", "0.99"),
    ("45", "28", "0", "0.6", "0.45", "0.8", "0.25", "0.99"),
    ("32", "35", "2.5", "0.9", "0.5", "0.7", "0.5", "0.975"),
    ("120", "31.5", "4", "1.5", "0.6", "0.5", "0.08", "0.997"),
    ("38", "36", "1", "0.55", "0.5", "0.95", "1", "0.95"),
    ("60", "24.5", "5", "0.4", "0.35", "0", "2", "0.9"),
    ("25", "40", "3", "0.7", "0.4", "0.6", "0.75", "0.96"),
    ("52.25", "49", "2.75", "1.2", "0.9", "0.85", "0.002739726027397260273972602740", "1"),
    ("41", "30", "3", "0.5", "0", "0", "0.5", "0.98"),
    ("41", "30", "3", "0.2", "0.2", "0.9", "0.01", "0.9995"),
]


def kirk(long, short, strike, long_vol, short_vol, correlation, years, discount):
    f1, f2, k, s1, s2, rho, t, df = (Decimal(v) for v in (
        long, short, strike, long_vol, short_vol, correlation, years, discount))
    floor = f2 + k
    weight = f2 / floor
    variance = s1 * s1 - 2 * rho * s1 * s2 * weight + s2 * s2 * weight * weight
    spread = (variance * t).sqrt()
    d1 = ((f1 / floor).ln() + spread * spread / 2) / spread
    return df * (f1 * ncdf(d1) - floor * ncdf(d1 - spread))


def integral(long, short, strike, long_vol, short_vol, correlation, years, discount, points=20000, width=10.0):
    f1, f2, k, s1, s2, rho, t, df = (float(v) for v in (
        long, short, strike, long_vol, short_vol, correlation, years, discount))
    root = math.sqrt(t)
    residual = s1 * root * math.sqrt(1 - rho * rho)

    def conditional(z):
        floor = f2 * math.exp(s2 * root * z - s2 * s2 * t / 2) + k
        forward = f1 * math.exp(rho * s1 * root * z - rho * rho * s1 * s1 * t / 2)
        if residual == 0:
            value = max(forward - floor, 0.0)
        else:
            d1 = (math.log(forward / floor) + residual * residual / 2) / residual
            value = (forward * (1 + math.erf(d1 / math.sqrt(2))) / 2
                     - floor * (1 + math.erf((d1 - residual) / math.sqrt(2))) / 2)
        return value * math.exp(-z * z / 2) / math.sqrt(2 * math.pi)

    step = 2 * width / points
    total = conditional(-width) + conditional(width)
    for n in range(1, points):
        total += (4 if n % 2 else 2) * conditional(-width + n * step)
    return df * total * step / 3


def row(case):
    value = {"long": case[0], "short": case[1], "strike": case[2], "long_volatility": case[3],
             "short_volatility": case[4], "correlation": case[5], "years": case[6], "discount": case[7],
             "kirk": q(kirk(*case)), "integral": format(integral(*case), ".12f")}
    if Decimal(case[4]) == 0:
        value["black76"] = q(black76(case[0], str(Decimal(case[1]) + Decimal(case[2])), case[3], case[6], case[7])[0])
    return value


def main():
    getcontext().prec = 60
    path = pathlib.Path(__file__).with_name("spread-option-oracle.json")
    path.write_text(json.dumps({"calls": [row(case) for case in CASES]}, indent=1) + "\n")


if __name__ == "__main__":
    main()
