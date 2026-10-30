"""Adversarial admission checks for retained dated-source risk samples."""
from copy import deepcopy
from datetime import datetime, timedelta
import json
from uuid import UUID, uuid4

from preview_fixture import TENANT, WORKSPACE


def run(query, job_id):
    job_id = str(UUID(job_id))
    job = json.loads(query(f"SELECT jsonb_build_object('catalog',catalog_id,'context',context_id,'at',valuation_at,'markets',frame->'connectedMarkets') FROM risk.jobs WHERE id='{job_id}';"))
    candidates = [(market['revisionId'], sample) for market in job['markets'] for sample in market['samples']
                  if sample['kind'] == 'contract_observation' and sample['freezeHistorical']]
    assert candidates, 'This admission oracle requires an actual historical source sample.'
    revision, sample = candidates[0]
    revision = str(UUID(revision))

    def qualify(value, *, outside_scope=False, rejected=False):
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'https://preview.invalid','risk','human_interactive','{TENANT}','{WORKSPACE}',NULL,'preview_runtime',60);").strip()
        literal = "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
        scope = f"selected_job.id='{uuid4()}';" if outside_scope else ''
        admission = f"PERFORM risk.require_contract_observation_sample(selected_job,'{revision}',{literal});"
        if rejected:
            admission = f"""BEGIN {admission}
 RAISE EXCEPTION 'Invalid retained source sample was admitted' USING ERRCODE='P0002';
 EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'risk_graph_input' THEN RAISE;END IF;END;"""
        query(f"""BEGIN;
 CREATE FUNCTION pg_temp.check_contract_sample() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $check$
 DECLARE selected_job risk.jobs;BEGIN SELECT * INTO STRICT selected_job FROM risk.jobs WHERE id='{job_id}';
 {scope}{admission} END $check$;
 SET LOCAL SESSION AUTHORIZATION preview_runtime;
 SELECT security.use_context('{token}');
 SELECT security.use_market_context('{job['catalog']}','{job['context']}');
 SELECT security.use_trading_context('{job['catalog']}','{job['at']}');
 SELECT pg_temp.check_contract_sample();ROLLBACK;""")

    qualify(sample)
    qualify(sample, outside_scope=True, rejected=True)
    qualify({**sample, 'contractId': str(uuid4())}, rejected=True)
    qualify({**sample, 'freezeObservation': True}, rejected=True)
    shifted = datetime.fromisoformat(sample['at'].replace('Z', '+00:00')) + timedelta(seconds=1)
    qualify({**sample, 'at': shifted.isoformat()}, rejected=True)
    wrong_series = deepcopy(sample)
    wrong_series['observations'][0]['seriesId'] = str(uuid4())
    qualify(wrong_series, rejected=True)
    # Retained calculation inputs never grant continuing reference authority.
    # Scope removal and its epoch change are confined to this transaction.
    query(f"""BEGIN;DO $check$ DECLARE selected_job risk.jobs;reference_catalog uuid;BEGIN
 SELECT * INTO STRICT selected_job FROM risk.jobs WHERE id='{job_id}';
 SELECT reference_catalog_id INTO STRICT reference_catalog FROM market_data.revisions
  WHERE(catalog_id,id)=(selected_job.catalog_id,'{revision}'::uuid);
 IF risk.authorized(selected_job) IS NOT TRUE THEN RAISE EXCEPTION 'Expected current job authority';END IF;
 DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=
  (reference_catalog,selected_job.tenant_id,selected_job.workspace_id);
 IF risk.authorized(selected_job) IS NOT FALSE THEN RAISE EXCEPTION 'Retained source evidence survived revoked reference scope';END IF;
 END $check$;ROLLBACK;""")
    print('PASS dated-source risk SQL: exact historical sample admitted; absent job scope, wrong contract/series/anchor and current-freeze flag refused; revoked reference scope denies retained job')
