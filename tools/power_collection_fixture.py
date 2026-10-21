"""Synthetic collected interval prices published through the collection worker's own entry points."""
import json
from datetime import timedelta
from uuid import uuid4


class Collector:
    """One synthetic collector login with one job per source, in the launcher's owned database."""

    def __init__(self, query, manifest, sources):
        """sources maps a key to {'provider', 'sourceId', 'clock'}."""
        self.query, self.sources = query, sources
        c, self.reference, self.revision, tenant, scope = [manifest[k] for k in
            ('catalogId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
        self.catalog = c
        actor, self.login = str(uuid4()), 'synthetic_actual_' + uuid4().hex[:12]
        query(f"""CREATE ROLE {self.login} LOGIN IN ROLE openetrm_collector;
          INSERT INTO security.principals VALUES('{actor}','service',true);
          INSERT INTO security.workspace_memberships VALUES('{tenant}','{scope}','{actor}',
            '{self.login}','Synthetic interval settlement collector',true,1);
          INSERT INTO security.role_grants SELECT gen_random_uuid(),'{actor}',id,'{tenant}','{scope}',
            clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='market_data_editor';
          INSERT INTO power_data.collectors VALUES('{self.login}','{actor}','{tenant}','{scope}','{c}');""")
        self.jobs = {}
        for key, source in sources.items():
            self.jobs[key] = str(uuid4())
            query(f"""INSERT INTO power_data.jobs VALUES('{self.jobs[key]}',gen_random_uuid(),
              'Synthetic {source['provider']} interval settlement','{self.login}','{c}','{source['provider']}',true,false,
              '0 8 * * *','UTC',-7,-1);
              INSERT INTO power_data.job_sources VALUES('{self.jobs[key]}','{source['sourceId']}','{c}');""")

    def publish(self, key, day, start, count, minutes, prices, revision):
        """Publish and certify one batch of consecutive intervals; returns its batch id and knowledge time."""
        source = self.sources[key]
        end = start + timedelta(minutes=count * minutes)
        self.query(f"""INSERT INTO power_data.operating_days VALUES('{self.reference}','{self.revision}','{source['clock']}',
          '{day}','{start.isoformat()}','{end.isoformat()}','{'a' * 64}') ON CONFLICT DO NOTHING;""")
        payload = json.dumps([{'sourceId': source['sourceId'], 'rows': [
            {'start': (start + timedelta(minutes=n * minutes)).isoformat(),
             'end': (start + timedelta(minutes=(n + 1) * minutes)).isoformat(),
             'price': prices[n], 'revision': revision} for n in range(count)]}])
        self.query(f"""SET SESSION AUTHORIZATION {self.login};SELECT power_data.worker_lock();
          DO $$DECLARE attempt uuid;BEGIN
            attempt=power_data.begin_attempt('{self.jobs[key]}','{day}','{day}');
            PERFORM power_data.finish_attempt(attempt,'{payload}'::jsonb,NULL);
          END$$;""")
        self.query(f"SELECT power_data.certify('{self.catalog}');")
        return json.loads(self.query(f"""SELECT json_build_object('batchId',b.id,'knownAt',
          to_char(k.known_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'))::text
          FROM power_data.batches b JOIN power_data.receipts k USING(sequence)
          JOIN power_data.prices p ON p.batch_id=b.id WHERE p.source_id='{source['sourceId']}'
          ORDER BY b.sequence DESC LIMIT 1;"""))
