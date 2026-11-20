"""Original gas commands resolve the same accepted native financial fact."""
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from check_reference_editing import main
from gas_loan_quantity_fixture import fixture,configure
from gas_loan_reconciliation_upgrade import checks as upgrade_checks
from gas_owned_fact_acknowledgements import checks as fact_checks
from gas_owned_fact_upgrade import checks as fact_upgrade


def checks(query,manifest):fact_checks(query,manifest,configure(query,manifest))


def upgrade(query,migration):
    upgrade_checks(query,migration);fact_upgrade(query,migration)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade)
