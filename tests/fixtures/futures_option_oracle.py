"""Generates futures-option-oracle.json: reference exp/ln/sqrt values.

Uses only the standard library `decimal` at 60 digits, rounded half-even to
30 fractional digits. Run: python3 -I tests/fixtures/futures_option_oracle.py
"""
import json
import pathlib
from decimal import Decimal, ROUND_HALF_EVEN, getcontext

getcontext().prec = 60
QUANTUM = Decimal(1).scaleb(-30)

POSITIVE = ["0.000000000001", "0.00000001", "0.000001", "0.001", "0.01", "0.05",
            "0.1", "0.25", "0.5", "0.75", "0.9", "0.999999", "1", "1.000001",
            "1.1", "1.5", "2", "2.5", "3", "3.141592653589793", "5", "7.5",
            "10", "12.34", "20", "25", "35", "42.5", "50"]
EXPONENTS = POSITIVE + ["55", "55.2"]
# Range edges: the smallest representable value and values near 10^24.
EDGES = ["0.000000000000000000000000000001", "123456789012345678901234"]
NEGATIVE = ["-0.000001", "-0.01", "-0.1", "-0.5", "-1", "-2", "-3.7", "-5", "-10",
            "-20", "-35", "-50"]


def q(value):
    return format(value.quantize(QUANTUM, rounding=ROUND_HALF_EVEN), "f")


def rows(inputs, fn):
    return [{"x": x, "y": q(fn(Decimal(x)))} for x in inputs]


oracle = {
    "exp": rows(EXPONENTS + NEGATIVE, lambda x: x.exp()),
    "ln": rows(POSITIVE + EDGES, lambda x: x.ln()),
    "sqrt": rows(POSITIVE + EDGES, lambda x: x.sqrt()),
}
path = pathlib.Path(__file__).with_name("futures-option-oracle.json")
path.write_text(json.dumps(oracle, indent=2) + "\n")
