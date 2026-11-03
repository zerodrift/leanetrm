"""Test-only observer for real publication workflows in runner-owned synthetic previews."""
from preview_client import PreviewClient


class BlotterFreshnessProbe:
    def __init__(self,url,query,manifest):
        self.client=PreviewClient(url);self.client.bootstrap();self.client.call('profile',{'profile':'trader'})
        frame=self.client.call('workspace/navigation')['frames'][0]
        self.request={'snapshotId':frame['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
        self.query,self.catalog=query,manifest['catalogId']

    def read(self):
        # The independent oracle also establishes that this publication did not touch the old market/trading streams.
        prior=self.query(f"SELECT jsonb_build_array((SELECT coalesce(max(event_order),0) FROM market_data.knowledge_events WHERE catalog_id='{self.catalog}'),(SELECT coalesce(max(publication_order),0) FROM trading.publications WHERE catalog_id='{self.catalog}'))::text;")
        return self.client.call('trading/freshness',self.request),prior

    def changed(self,before,label):
        after=self.read()
        assert after[0]!=before[0] and after[1]==before[1],label
        print('PASS Blotter freshness: '+label,flush=True)
