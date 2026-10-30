"""Live header contexts over committed synthetic inputs in an owned preview."""
import http.client
import json
import queue
import socket
import threading
from urllib.parse import urlsplit
from preview_client import PreviewClient, initial_frame

FUTURES, SWAPS = 'contract_lot_execution_pnl_v2', 'independent_side_financial_swap_v2'
# A view that is not working data cannot book or amend, and its reasons say which view it is, so that the way back is the right one.
LIVE_BOOKING = 'Live view is read-only. Return to working data, then select an As of date.'
LIVE_AMEND = 'Live view is read-only. Return to working data to amend.'
HISTORICAL_BOOKING = 'Historical view is read-only. Return to working data, then select an As of date.'


class Events:
    def __init__(self, client, selection):
        address=urlsplit(client.url)
        self.connection=http.client.HTTPConnection(address.hostname,address.port,timeout=20)
        self.connection.request('GET',client.api_prefix+'market/live/events?selection='+selection,headers={
            'Origin':client.url,'Sec-Fetch-Site':'same-origin','Sec-Fetch-Mode':'cors',
            'Cookie':'; '.join(c.name+'='+c.value for c in client.jar)})
        self.response=self.connection.getresponse()
        assert self.response.status==200,self.response.status
        self.events=queue.Queue()
        self.thread=threading.Thread(target=self.read,daemon=True)
        self.thread.start()

    def read(self):
        try:
            event={}
            while line:=self.response.readline():
                text=line.decode().strip()
                if not text:
                    if event:self.events.put(event)
                    event={}
                elif text.startswith('data:'):event['data']=json.loads(text[5:])
                elif text.startswith('event:'):event['event']=text[6:].strip()
                # SSE comments keep the connection alive; they are not events.
                elif text.startswith(':'):continue
        except (OSError,ValueError):
            pass

    def next(self, timeout=5):
        return self.events.get(timeout=timeout)

    def quiet(self):
        try:unexpected=self.next(.7)
        except queue.Empty:return
        raise AssertionError('Unrelated or rolled back work produced stream output: '+repr(unexpected))

    def close(self):
        if self.connection.sock:
            try:self.connection.sock.shutdown(socket.SHUT_RDWR)
            except OSError:pass
        self.response.close()
        self.connection.close()
        self.thread.join(timeout=2)


def run_checks(url, manifest, query):
    live=PreviewClient(url);live.bootstrap();live.call('profile',{'profile':'viewer'})
    working=live.call('workspace');seed=initial_frame(working,'2020-12-18')
    live.call('market/live',{'snapshotId':seed['id'],'policy':'latest'},expected=422)
    selected=live.call('market/live',{'snapshotId':seed['id'],'policy':'selected_facts_latest_committed_corrections_v1'})
    stream=Events(live,selected['live']['id'])
    try:
        first=stream.next()['data'];assert first['live']==selected['live']
        original=live.call('evaluate',{'snapshotId':first['frame']['id'],'curve':'local_power'})
        historical=PreviewClient(url);historical.bootstrap();historical.call('profile',{'profile':'viewer'})
        historical.call('market/context',{'effectiveOn':'2020-12-18','knownAt':first['context']['knownAt'],'mode':'at','selection':'latest_correction'})
        pinned_frame=initial_frame(historical.call('workspace/navigation'),'2020-12-18')
        pinned=historical.call('evaluate',{'snapshotId':pinned_frame['id'],'curve':'local_power'})
        live.call('market/live/events?selection='+selected['live']['id'],expected=403,
                  headers={'Sec-Fetch-Site':'same-origin','Sec-Fetch-Mode':'cors'})
        query("BEGIN;NOTIFY openetrm_calculation_changes;ROLLBACK;")
        stream.quiet()
        query("NOTIFY openetrm_calculation_changes;")
        stream.quiet()
        # A market-only reader must learn nothing about private trade activity,
        # including notification timing. This isolated publication has no holdings.
        catalog=manifest['catalogId']
        query(f"BEGIN;SELECT pg_advisory_xact_lock(730849102);INSERT INTO trading.publications SELECT '{catalog}',coalesce(max(publication_order),0)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';COMMIT;")
        stream.quiet()
        editor=PreviewClient(url);editor.bootstrap();editor.call('profile',{'profile':'editor'})
        actor=initial_frame(editor.call('workspace/navigation'),'2020-12-18')
        observation='70000000-0000-0000-0000-000000000001'
        correction=editor.call('corrections',{'snapshotId':actor['id'],'observationId':observation,'value':'3.1'})
        second=stream.next()['data']
        assert second['live']['generation']==first['live']['generation']+1
        assert second['context']['contextId']!=first['context']['contextId']
        assert second['context']['effectiveOn']==first['context']['effectiveOn']
        assert second['frame']['at']==first['frame']['at']
        changed=live.call('evaluate',{'snapshotId':second['frame']['id'],'curve':'local_power'})
        assert changed['points'][0]['value']=='29.8',changed['points'][0]
        assert changed['points'][1:]==original['points'][1:]
        assert live.call('evaluate',{'snapshotId':first['frame']['id'],'curve':'local_power'})['points']==original['points']
        combined=live.call('evaluate',{'snapshotId':first['frame']['id'],'curve':'local_power','includeWorkspace':True})
        assert combined['context']==combined['workspace']['context']==first['context']
        assert [f['id'] for f in combined['workspace']['frames']]==[first['frame']['id']]
        assert historical.call('evaluate',{'snapshotId':pinned_frame['id'],'curve':'local_power'})['points']==pinned['points']
        assert live.call('workspace/navigation')['live']['generation']==second['live']['generation']
        live.call('market/current',{})
        assert live.call('workspace/navigation')['live'] is None
    finally:
        stream.close()
    # Reconnect always returns an authorized full latest snapshot, not an old
    # result replay; retired selections cannot reopen.
    live.call('market/live/events?selection='+selected['live']['id'],expected=403,
              headers={'Sec-Fetch-Site':'same-origin','Sec-Fetch-Mode':'cors'})
    risk=PreviewClient(url);risk.bootstrap();risk.call('profile',{'profile':'risk'})
    risk_seed=initial_frame(risk.call('workspace/navigation'),'2020-12-18')
    risk_live=risk.call('market/live',{'snapshotId':risk_seed['id'],'policy':'selected_facts_latest_committed_corrections_v1'})
    risk_stream=Events(risk,risk_live['live']['id'])
    try:
        view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
        def context(update,convention='trading_workspace_v1'):
            return {'snapshotId':update['frame']['id'],'knowledgeMode':'retained','contextId':update['context']['contextId'],'convention':convention}
        first=risk_stream.next()['data']
        before=risk.call('trading/blotter',{'context':context(first),'view':view})
        assert before['hasSwaps'] and before['holdings']
        previews={h['tradeId']:risk.call('swaps/editor',{'context':context(first,'independent_side_financial_swap_v2'),'tradeId':h['tradeId'],'revision':int(h['revision'])})
                  for h in before['holdings'] if h['instrumentType']!='future'}
        editor.call('corrections',{'snapshotId':correction['frame']['id'],'observationId':correction['observationId'],'value':'3.2'})
        second=risk_stream.next()['data']
        after=risk.call('trading/blotter',{'context':context(second),'view':view})
        prior={h['tradeId']:h for h in before['holdings']}
        changed=[h for h in after['holdings'] if h['tradeId'] in previews and h['presentValue']!=prior[h['tradeId']]['presentValue']]
        assert changed,'A shared quote correction did not reach any booked swap.'
        holding=changed[0]
        detail=risk.call('swaps/editor',{'context':context(second,'independent_side_financial_swap_v2'),'tradeId':holding['tradeId'],'revision':int(holding['revision'])})
        assert detail['context']==second['context'] and len(detail['valuation']['sides'])==2
        assert detail['valuation']['presentValue']!=previews[holding['tradeId']]['valuation']['presentValue']
        assert detail['valuation']['sides']!=previews[holding['tradeId']]['valuation']['sides']
        assert risk.call('swaps/editor',{'context':context(first,'independent_side_financial_swap_v2'),'tradeId':holding['tradeId'],'revision':int(holding['revision'])})['valuation']==previews[holding['tradeId']]['valuation']
        # A live view cannot book or amend, and says that it is live: not that it is historical.
        retained={'snapshotId':second['frame']['id'],'knowledgeMode':'retained','contextId':second['context']['contextId']}
        for path,convention in (('futures/catalog',FUTURES),('swaps/catalog',SWAPS)):
            offer=risk.call(path,{**retained,'convention':convention})
            assert offer['canBook'] is False and offer['bookingUnavailableReason']==LIVE_BOOKING,(path,offer['canBook'],offer['bookingUnavailableReason'])
        assert detail['canAmend'] is False and detail['amendUnavailableReason']==LIVE_AMEND,(detail['canAmend'],detail['amendUnavailableReason'])
        risk.call('market/live/pause',{'selection':risk_live['live']['id'],'snapshotId':first['frame']['id']})
        paused=risk.call('workspace/navigation')
        assert paused['live'] is None and paused['context']==first['context']
        # The context a pause keeps is a historical view, and is called one.
        kept=risk.call('futures/catalog',{'snapshotId':paused['frames'][0]['id'],'convention':FUTURES,'knowledgeMode':'retained','contextId':paused['context']['contextId']})
        assert kept['canBook'] is False and kept['bookingUnavailableReason']==HISTORICAL_BOOKING,(kept['canBook'],kept['bookingUnavailableReason'])
    finally:
        risk_stream.close()
    print('PASS live calculation: common booked PV, independent side schedules, exact old-frame repricing and displayed-context pause',flush=True)
    print('PASS live calculation: committed corrections, scoped quiet wakeups, pinned generations and stream/session boundaries',flush=True)
    print('PASS live calculation: a live view says it is live, not historical, in why it cannot book or amend',flush=True)
