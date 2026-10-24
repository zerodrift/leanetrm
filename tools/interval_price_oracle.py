"""Independent exact oracle for synthetic hourly curve HTTP responses.

Uses Python's civil-time database and Fraction; consumes fixture definitions and
input observations, never server prices, block assignments or calibration means.
"""
from datetime import datetime, timedelta, timezone
from fractions import Fraction
from zoneinfo import ZoneInfo


def hours(first, last, zone='America/New_York'):
    clock = ZoneInfo(zone)
    start = datetime.combine(first, datetime.min.time(), clock).astimezone(timezone.utc)
    end = datetime.combine(last, datetime.min.time(), clock).astimezone(timezone.utc)
    while start < end:
        yield start, start.astimezone(clock)
        start += timedelta(hours=1)


def shaped_prices(first, last, marks, method, shape, holidays=(), bump=None):
    """Each exact hour from whole-period peak/off-peak calibration.

    Synthetic peak is weekdays 07:00–23:00, excluding explicitly listed holidays.
    marks and optional bump are keyed 'peak'/'off'; shape accepts the UTC instant
    and civil hour, independently of the API's returned point memberships.
    """
    members = {'peak': [], 'off': []}
    for utc, civil in hours(first, last):
        peak = civil.weekday() < 5 and 7 <= civil.hour < 23 and civil.date() not in holidays
        members['peak' if peak else 'off'].append((utc, Fraction(shape(utc, civil.hour))))
    result = {}
    for membership, cells in members.items():
        mean = sum((value for _, value in cells), Fraction()) / len(cells)
        mark = Fraction(marks[membership]) + Fraction((bump or {}).get(membership, 0))
        for utc, value in cells:
            result[utc] = mark + value - mean if method == 'additive' else mark * value / mean
    return result


def compare(points, expected):
    """Compare every point's exact numerator and denominator, including coverage."""
    actual = {datetime.fromisoformat(p['interval']['start'].replace('Z', '+00:00')):
              Fraction(int(p['price']['numerator']), int(p['price']['denominator'])) for p in points}
    assert len(points) == len(expected) == len(actual), "Oracle interval coverage contains missing or duplicate cells"
    assert actual == expected, next(((str(at), actual.get(at), value) for at, value in expected.items()
                                    if actual.get(at) != value), 'Oracle coverage differs')
