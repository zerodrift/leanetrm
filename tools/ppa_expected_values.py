"""Independent exact arithmetic and membership oracle for synthetic PPA cases.

No managed code, output rows, resolver classifications or cache keys are inputs.
Decimal inputs are strings and all arithmetic uses Fraction. Civil-time checks
use Python's zoneinfo. Refusal expectations are explicit policy assertions, not
claims that an arithmetic oracle proves authorization or production validation.

Usage: python3 tools/ppa_expected_values.py [--output expected.json]
       python3 tools/ppa_expected_values.py --compare managed.json

The comparison input is a flat object of exported oracle paths. It must contain
at least one path; every supplied path must exist and match exactly. The report
names tested paths, so a subset cannot be mistaken for complete case coverage.
Use --require-all for a complete arithmetic export. Exact fractions are objects
with decimal-string numerator/denominator, including integers.
"""
import argparse
import calendar
import json
from datetime import date, datetime, timedelta
from fractions import Fraction
from pathlib import Path
from zoneinfo import ZoneInfo


ROOT = Path(__file__).resolve().parents[1]


def instant(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def exact(value):
    value = Fraction(value)
    return {"numerator": str(value.numerator), "denominator": str(value.denominator)}


def expected(fixture):
    if fixture["schema"] != "synthetic_ppa_oracle_v1":
        raise ValueError("Unsupported fixture schema")
    result = {}
    cases = fixture["cases"]

    def put(case, name, value):
        result[f"{case}/{name}"] = exact(value)

    key = "01_segmented_payment"
    c = cases[key]
    amounts = [Fraction(row["unit_mwh"]) * Fraction(row["contract_mw"])
               * Fraction(row["strike"]) * Fraction(row["coefficient"])
               for row in c["intervals"]]
    for i, amount in enumerate(amounts):
        put(key, f"amount_{i}", amount)
        put(key, f"pv_{i}", amount * Fraction(c["discount_factor"]))
        result[f"{key}/payment_{i}"] = c["payment_date"]
    put(key, "primary", sum(amounts) * Fraction(c["discount_factor"]))
    put(key, "termination_retained_amount", amounts[0])

    key = "02_coverage_partition"
    c = cases[key]
    start, end, boundary = map(instant, (c["full_from"], c["full_to"], c["boundary"]))
    step = timedelta(minutes=c["interval_minutes"])
    intervals = []
    while start < end:
        intervals.append((start, start + step))
        start += step
    left = [row for row in intervals if row[1] <= boundary]
    right = [row for row in intervals if row[0] >= boundary]
    assert set(left).isdisjoint(right) and set(left + right) == set(intervals)
    zone = ZoneInfo(fixture["clock"])
    repeated = [row[0] for row in intervals if row[0].astimezone(zone).hour == 1]
    assert len(repeated) == 2 and repeated[0] != repeated[1]
    unit = Fraction(c["unit_mwh"]) * Fraction(c["contract_mw"]) * Fraction(c["price"]) * Fraction(c["coefficient"])
    put(key, "full_amount", len(intervals) * unit)
    put(key, "first_amount", len(left) * unit)
    put(key, "second_amount", len(right) * unit)
    result[f"{key}/row_count"] = len(intervals)
    result[f"{key}/repeated_hour_count"] = len(repeated)
    result[f"{key}/invalid_boundary"] = "ppa_segment_boundary"
    result[f"{key}/overlap"] = "ppa_segment_coverage"
    result[f"{key}/gap"] = "ppa_segment_coverage"

    key = "03_retained_certificates"
    c = cases[key]
    quantity, mark, adjustment, bump = (Fraction(c[name]) for name in ("certificates", "mark", "adjustment", "bump"))
    for name, shock in (("primary", 0), ("up", bump), ("down", -bump)):
        put(key, name, quantity * (mark + adjustment + shock))
    put(key, "overdue_known", quantity * Fraction(c["payment_price"]) * Fraction(c["payment_coefficient"]))
    result[f"{key}/complete_pv"] = None
    result[f"{key}/holding_class"] = "Held"
    result[f"{key}/holding_coverage"] = "HoldingValue"
    result[f"{key}/holding_cash_rows"] = 0

    key = "04_estimated_holdings"
    c = cases[key]
    for name, quantity in (("original", c["estimated_certificates"]), ("corrected", c["corrected_certificates"]), ("original_replay", c["estimated_certificates"])):
        put(key, name, Fraction(quantity) * Fraction(c["mark"]))
    result[f"{key}/original_class"] = "Estimated"
    result[f"{key}/deadline_missing"] = "meter_actual_missing"

    key = "05_mixed_overdue"
    c = cases[key]
    known = Fraction(c["known_mwh"]) * Fraction(c["actual_price"])
    estimated = Fraction(c["estimated_mwh"]) * Fraction(c["actual_price"])
    future = Fraction(c["future_amount"])
    future_delivered = instant(c["future_delivery_end"]) <= instant(c["valuation_at"])
    assert instant(c["future_payment_at"]) > instant(c["valuation_at"])
    holding = Fraction(c["holding_quantity"]) * Fraction(c["holding_mark"])
    primary = future * Fraction(c["discount_factor"]) + holding
    put(key, "overdue_known", known)
    put(key, "overdue_estimated", estimated)
    put(key, "primary", primary)
    put(key, "undiscounted_primary", future + holding)
    result[f"{key}/complete_pv"] = None
    result[f"{key}/canceling_overdue_complete_pv"] = None
    result[f"{key}/known_pv"] = None
    result[f"{key}/estimated_pv"] = None

    key = "06_deadlines_and_cutoffs"
    c = cases[key]
    put(key, "corrected_known", Fraction(c["corrected_mwh"]) * Fraction(c["actual_price"]))
    for scenario in ("base", "up", "down"):
        put(key, f"{scenario}_overdue_known", known)
        put(key, f"{scenario}_overdue_estimated", estimated)
    result[f"{key}/meter_deadline_missing"] = "meter_actual_missing"
    result[f"{key}/price_deadline_missing"] = "interval_actual_missing"

    key = "07_capacity_month"
    c = cases[key]
    month, change = date.fromisoformat(c["month"]), date.fromisoformat(c["change_date"])
    count = calendar.monthrange(month.year, month.month)[1]
    allocated = Fraction(c["accredited_mw"]) / Fraction(c["basis_mw"]) * Fraction(c["contract_mw"])
    after = Fraction(c["changed_accredited_mw"]) / Fraction(c["basis_mw"]) * Fraction(c["contract_mw"])
    before_days = (change - month).days
    quantity = allocated * count
    changed_quantity = allocated * before_days + after * (count - before_days)
    for name, value in (("allocated_mw", allocated), ("mw_day", quantity), ("amount", quantity * Fraction(c["price_per_mw_day"])),
                        ("changed_mw_day", changed_quantity), ("changed_amount", changed_quantity * Fraction(c["price_per_mw_day"])),
                        ("dst_day_mw_day", allocated)):
        put(key, name, value)
    result[f"{key}/monthly_rows"] = 1
    result[f"{key}/operating_days"] = count

    key = "08_capacity_components"
    c = cases[key]
    put(key, "amount", sum(Fraction(row["mw_day"]) * Fraction(row["price"]) for row in c["components"]))
    for name, code in (("interior_boundary", "ppa_segment_boundary"), ("mwh_quote", "ppa_quantity_unit"), ("mw_quote", "ppa_quantity_unit")):
        result[f"{key}/{name}"] = code

    key = "09_unit_reuse"
    c = cases[key]
    for label, scopes in (("full", c["full_scopes"]), ("segmented", c["segmented_scopes"])):
        result[f"{key}/{label}_unit_calculations"] = scopes
        result[f"{key}/{label}_unit_reuses"] = scopes * (c["bindings_per_scope"] - 1)
        result[f"{key}/{label}_side_calculations"] = scopes * c["sides_per_scope"]
    for i, mw in enumerate(c["contract_mw"]):
        put(key, f"scaled_{i}", Fraction(mw) * Fraction(c["unit_value"]))

    key = "10_shared_work"
    c = cases[key]
    for i, quantity in enumerate(c["unit_quantities"]):
        mwh = Fraction(quantity) * Fraction(c["contract_mw"])
        put(key, f"physical_{i}", mwh * Fraction(c["physical_price"]))
        for j, strike in enumerate(c["strikes"]):
            put(key, f"cash_quantity_{i}_strike_{j}", -mwh * Fraction(strike))
            put(key, f"combined_quantity_{i}_strike_{j}", mwh * (Fraction(c["physical_price"]) - Fraction(strike)))

    key = "11_independent_discounting"
    c = cases[key]
    value = sum(Fraction(amount) * Fraction(df) for amount, df in zip(c["amounts"], c["discount_factors"], strict=True))
    put(key, "primary", value)
    put(key, "reversed", -value)
    for i, mw in enumerate(c["contract_mw"]):
        put(key, f"scaled_{i}", Fraction(mw) * Fraction(c["unit_value"]))

    key = "12_measure_reconciliation"
    delivered = Fraction(cases[key]["delivered_amount"])
    put(key, "primary", primary)
    put(key, "delivered_value", delivered)
    put(key, "all_amounts", known + estimated + future + holding + delivered)
    for name, value in (("class_realised", known + delivered + (future if future_delivered else 0)), ("class_estimated", estimated), ("class_held", holding), ("class_unrealised", 0 if future_delivered else future),
                        ("coverage_overdue_known", known), ("coverage_overdue_estimated", estimated), ("coverage_discounted_amount", future),
                        ("coverage_holding", holding), ("coverage_delivered", delivered), ("lifecycle_delivered", known + estimated + (future if future_delivered else 0)), ("lifecycle_remaining", 0 if future_delivered else future)):
        put(key, name, value)
    put(key, "no_overdue_complete_pv", primary)
    put(key, "estimated_delivered_realised_value", 0)
    result[f"{key}/complete_pv"] = None

    p = fixture["plan"]
    c = p["physical"]
    physical, strike = Fraction(c["mwh"]) * Fraction(c["location_price"]), -Fraction(c["mwh"]) * Fraction(c["strike"])
    for name, value in (("physical", physical), ("strike", strike), ("primary", physical + strike)):
        put("plan_physical", name, value)
    c = p["virtual"]
    put("plan_virtual", "primary", Fraction(c["mwh"]) * (Fraction(c["hub_price"]) - Fraction(c["strike"])))
    c = p["normalization"]
    put("plan_normalization", "metered_mwh", Fraction(c["meter_mwh"]) / Fraction(c["basis_mw"]) * Fraction(c["contract_mw"]))
    put("plan_normalization", "degraded_mwh", Fraction(c["degraded_unit_mwh"]) * Fraction(c["contract_mw"]))
    c = p["escalation"]
    base = date.fromisoformat(c["base_date"])
    for i, text in enumerate(c["dates"]):
        operating = date.fromisoformat(text)
        years = operating.year - base.year - ((operating.month, operating.day) < (base.month, base.day))
        put("plan_escalation", f"price_{i}", Fraction(c["strike"]) * (1 + Fraction(c["rate"])) ** years)
    c = p["capacity_fixing"]
    quantity = Fraction(c["accredited_mw"]) / Fraction(c["basis_mw"]) * Fraction(c["contract_mw"]) * c["operating_days"]
    put("plan_capacity", "fixed_amount", quantity * Fraction(c["fixing"]))
    put("plan_capacity", "marked_amount", quantity * Fraction(c["mark"]))
    c = p["indexed"]
    amount = Fraction(c["mwh"]) * Fraction(c["index"])
    cash = Fraction(c["mwh"]) * (Fraction(c["index"]) + Fraction(c["adder"])) * Fraction(c["coefficient"])
    put("plan_indexed", "physical", amount)
    put("plan_indexed", "cash_with_adder", cash)
    put("plan_indexed", "primary_with_adder", amount + cash)
    example = c["multiplier_example"]
    put("plan_indexed", "multiplied_price", Fraction(example["index"]) * Fraction(example["multiplier"]) + Fraction(example["adder"]))
    c = p["basis_cash"]
    receive, pay = Fraction(c["mwh"]) * Fraction(c["receive_index"]), -Fraction(c["mwh"]) * Fraction(c["pay_index"])
    put("plan_basis", "receive", receive)
    put("plan_basis", "pay", pay)
    put("plan_basis", "primary", receive + pay)
    c = p["quarter_hour_covariance"]
    put("plan_covariance", "amount", sum(Fraction(q) * Fraction(price) for q, price in zip(c["mwh"], c["prices"], strict=True)))
    c = p["economic_classes"]
    put("plan_classes", "realised", Fraction(c["realised_cash"]) + Fraction(c["realised_delivery"]))
    put("plan_classes", "unrealised", Fraction(c["unrealised"]))
    return result


def compare(actual, oracle, require_all=False):
    if not isinstance(actual, dict) or not actual:
        raise ValueError("Comparison requires a nonempty object of exported oracle paths")
    if require_all and actual.keys() != oracle.keys():
        raise ValueError(f"Coverage differs: missing={sorted(oracle.keys() - actual.keys())}, unexpected={sorted(actual.keys() - oracle.keys())}")
    for path, value in actual.items():
        if path not in oracle:
            raise ValueError(f"Unknown oracle path: {path}")
        if value != oracle[path]:
            raise ValueError(f"{path}: expected {oracle[path]!r}; got {value!r}")
    return {"compared_outputs": len(actual), "available_outputs": len(oracle),
            "case_groups": sorted({path.split("/")[0] for path in actual}), "paths": sorted(actual)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inputs", type=Path, default=ROOT / "tests/fixtures/ppa-oracle-inputs.json")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--compare", type=Path)
    parser.add_argument("--require-all", action="store_true")
    args = parser.parse_args()
    result = expected(json.loads(args.inputs.read_text()))
    if args.compare:
        result = compare(json.loads(args.compare.read_text()), result, args.require_all)
    serialized = json.dumps(result, indent=2, sort_keys=True) + "\n"
    if args.output:
        args.output.write_text(serialized)
    else:
        print(serialized, end="")


if __name__ == "__main__":
    main()
