"""Exact values for HTTP checks: UTC instants, exact fractions and half-even rounding."""
from datetime import datetime
from decimal import Decimal, ROUND_HALF_EVEN, localcontext
from fractions import Fraction


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def fraction(value):
    return Fraction(int(value['numerator']), int(value['denominator']))


def rounded(value, places):
    with localcontext() as context:
        context.prec = 100
        return (Decimal(value.numerator) / Decimal(value.denominator)).quantize(
            Decimal(1).scaleb(-places), rounding=ROUND_HALF_EVEN)
