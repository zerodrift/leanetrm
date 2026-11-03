"""The volatility surface fit of the owned exchange example's option products, run in the managed checks against the
real example data. The managed check derives the expected observation ids from each series calibrated alone; this runner
selects, in SQL, the option settlements a derived frame leaves out."""
import json
import subprocess
from pathlib import Path
from uuid import uuid4
from preview_client import PreviewClient

PRODUCTS = ('nymex_on', 'nymex_lo')
# The fixture's deep in-the-money series that settles exactly at intrinsic (exchange_fixture.AT_INTRINSIC).
AT_INTRINSIC = 'nymex_on_20261201_c2.00'
# The product whose option settlements a derived frame leaves out, keeping its underlying futures.
UNSETTLED = 'nymex_lo'


def run_checks(url, query, manifest, dotnet, owned_root, environment):
    base = PreviewClient(url); base.bootstrap()
    client = PreviewClient(url, 'exchange'); client.opener = base.opener; client.bootstrap(); client.call('profile', {'profile': 'trader'})
    frames = client.call('workspace')['frames']
    frame = next(f for f in frames if f['at'].startswith('2026-10-16'))
    missing = next(f for f in frames if f['at'].replace('+00:00', 'Z') == manifest['missingSettlement']['at'])

    # The pinned daily settlements of the product's own option series.
    pins = ",".join(f"'{oid}'" for oid in frame['observationIds'])
    unsettled_options = set(query(
        "SELECT DISTINCT o.id FROM market_data.observations o "
        "JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        "JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id) "
        "JOIN ref_data.futures_option_series os ON(os.catalog_id,os.revision_id,os.code)=(k.reference_catalog_id,k.reference_revision_id,k.futures_option_series_code) "
        f"WHERE o.id IN ({pins}) AND o.role='daily_settlement' AND os.product_code='{UNSETTLED}';").split())
    assert unsettled_options
    unsettled = {**frame, 'id': str(uuid4()), 'observationIds': [o for o in frame['observationIds'] if o not in unsettled_options]}
    fixture = {'frame': frame, 'missingFrame': missing, 'unsettledFrame': unsettled, 'unsettledProduct': UNSETTLED,
               'products': list(PRODUCTS), 'atIntrinsic': AT_INTRINSIC, 'missingSettlement': manifest['missingSettlement']}
    path = Path(owned_root) / 'vol-surface-fit.json'
    path.write_text(json.dumps(fixture))
    # About six fits of calibration in all; a hosted runner fits each product in about 90 s.
    subprocess.run([str(dotnet), 'run', '--project', 'tests/OpenEtrm.Api.Checks', '--', '--vol-surface-fit', str(path)],
                   cwd=Path(__file__).resolve().parents[1], env=environment, check=True, timeout=900)
