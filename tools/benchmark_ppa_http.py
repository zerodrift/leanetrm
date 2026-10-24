"""Full authorized PPA HTTP workloads; independent civil-time/Fraction oracle.

One unique five-side instrument, one durable binding, one book, base only. The
20-second browser/client contract remains active. A timeout or structured refusal
is a reported qualification miss, never a successful timing or an invitation to
change financial input policy.
"""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from fractions import Fraction
import hashlib
import json
from pathlib import Path
import shlex
import subprocess
import threading
import time
from uuid import uuid4
from zoneinfo import ZoneInfo

from exact_values import fraction
from preview_client import PreviewClient
from preview_fixture import trade_booking


class ProcessRssSampler:
    """Sample only the launcher's owned process; retain errors as missing evidence.

    RSS samples are observations, not an OS high-water mark. The sampler thread
    belongs to the Python client and never changes the API process or its GC.
    """
    def __init__(self, pid, interval_seconds=0.1, read=None):
        from benchmark_interval_series import api_resident_bytes
        self.pid, self.interval = pid, interval_seconds
        self.read = read or api_resident_bytes
        self.samples, self.errors = [], []
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self._run, name='ppa-owned-api-rss', daemon=True)

    def _sample(self):
        try:
            self.samples.append((time.perf_counter(), self.read(self.pid)))
        except Exception as error:
            self.errors.append(f'{type(error).__name__}: {error}')

    def _run(self):
        while not self.stop.wait(self.interval):
            self._sample()

    def __enter__(self):
        self.started = time.perf_counter()
        self._sample()
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.stop.set()
        # The production reader itself has a ten-second subprocess deadline.
        # A stuck sampler must not prevent the measured failure being reported.
        self.thread.join(timeout=11)
        if self.thread.is_alive():
            self.errors.append('RSS sampler did not stop within its reader deadline')
        else:
            self._sample()
        self.ended = time.perf_counter()

    def report(self):
        gaps = [b[0] - a[0] for a, b in zip(self.samples, self.samples[1:])]
        return {'status':'sampled' if self.samples and not self.errors else 'incomplete',
                'intervalMilliseconds':self.interval * 1000,
                'targetFrequencyHz':1 / self.interval,
                'sampleCount':len(self.samples),
                'observationSeconds':self.ended - self.started,
                'maximumSampleGapSeconds':max(gaps) if gaps else None,
                'sampledPeakResidentBytes':max(value for _, value in self.samples) if self.samples else None,
                'completionResidentBytes':self.samples[-1][1] if self.samples and not self.errors and not self.thread.is_alive() else None,
                'errors':self.errors}


def api_artifacts(pid):
    """Hash the private DLLs named by the owned API command, not mutable build output."""
    command = subprocess.run(['ps','-ww','-o','command=','-p',str(pid)], capture_output=True,
                             text=True, check=True, timeout=10).stdout.strip()
    dlls = [Path(arg) for arg in shlex.split(command) if arg.endswith('/OpenEtrm.Api.dll')]
    if len(dlls) != 1:
        raise RuntimeError('The owned API command must name exactly one OpenEtrm.Api.dll')
    api = dlls[0].resolve()
    if api.parent.name != 'api' or not (api.parent.parent/'.openetrm-preview').is_file():
        raise RuntimeError('The API artifact must be inside the marked disposable preview')
    result = {}
    for name in ('OpenEtrm.Api.dll','OpenEtrm.MarketData.dll'):
        path = api.parent/name
        result[name] = {'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),
                        'lastModifiedUtc':datetime.fromtimestamp(path.stat().st_mtime,timezone.utc).isoformat()}
    return result


def instant(value):
    return value.astimezone(timezone.utc).isoformat().replace('+00:00', 'Z')


def midnight(day, zone):
    return datetime.fromisoformat(day).replace(tzinfo=ZoneInfo(zone)).astimezone(timezone.utc)


def expected_value(first, last, zone, minutes=60, peak_start=7, contract_mw=80):
    """Integrate independent weekday civil-time membership; no HTTP output inputs."""
    start, end = midnight(first, zone), midnight(last, zone)
    at = start; step = timedelta(minutes=minutes); peak = 0; cells = 0
    while at < end:
        local = at.astimezone(ZoneInfo(zone))
        peak += local.weekday() < 5 and peak_start <= local.hour < peak_start + 16
        cells += 1; at += step
    hours = Fraction(cells * minutes, 60); peak_hours = Fraction(peak * minutes, 60)
    return contract_mw * (hours + 10 * peak_hours), cells, hours, peak_hours


def terms(manifest, minutes=60, first='2031-11-01', last='2031-12-01', five=True):
    from check_ppa_bookings_http import profile_terms
    result = profile_terms(manifest)
    fixture = manifest['ppaBookings']['workloads'][str(minutes)]
    result['delivery'].update(clock=fixture['clock'], intervalMinutes=minutes, **{'from':first, 'to':last})
    result['normalizationAssets'] = [fixture['asset']]
    physical = result['memberships'][0]['side']
    physical.update(location=fixture['location'], quantity={'kind':'volume','sources':[fixture['asset']],
        'stage':'generated','capacity':None,'energy':None,'hours':None,'slots':[],'assets':[]})
    if five:
        charge = deepcopy(result['memberships'][1]);charge['role']='fixed_charge'
        charge['side']['fixedPrice']['price']='2'
        certificate = deepcopy(result['memberships'][0]);certificate['role']='certificate_delivery'
        certificate['side'].update(kind='certificate',location=None,attribute={'quantity':'test_rec_quantity',
            'conversion':'1','eligibility':'asset_generation_at_stage_v1','holding':'held_at_current_vintage_mark_v1',
            'disposition':'none_recorded_v1','rule':None})
        payment = deepcopy(result['memberships'][1]);payment['role']='certificate_payment'
        payment['side']['quantity']['slots']=[{'role':'certificate_delivery','ordinal':0}]
        payment['side']['fixedPrice'].update(quantity='test_rec_quantity',price='1')
        result['memberships'].extend([charge,certificate,payment])
    return result


class HttpFixture:
    def __init__(self, url, manifest, frame):
        self.manifest, self.frame, self.heads = manifest, frame, {}
        self.client=PreviewClient(url);self.client.bootstrap();self.client.call('profile',{'profile':'trader'});self.client.bootstrap()
        self.frame=next(f for f in self.client.call('workspace')['frames'] if not f.get('ppaBundle'))['id']
        self.editor=PreviewClient(url);self.editor.bootstrap();self.editor.call('profile',{'profile':'editor'});self.editor.bootstrap()

    def cutoff(self):
        return self.client.call('interval-series')['knownAt']

    def revise(self, series, first, values):
        body={'requestId':str(uuid4()),'seriesId':series,'expectedRevision':self.heads.get(series,0),
              'reason':'Synthetic durable PPA qualification inputs','segments':[{'start':first,'values':values}]}
        result=self.editor.call('interval-series/revise',body)
        assert result['intervals']==len(values) and result['revision']==body['expectedRevision']+1
        self.heads[series]=result['revision']
        return result

    def load(self, minutes, first, last):
        fixture=self.manifest['ppaBookings']['workloads'][str(minutes)]
        start,end=midnight(first,fixture['zone']),midnight(last,fixture['zone']);step=timedelta(minutes=minutes)
        count=(end-start)//step
        for series,value in [(fixture['shape'],'0'),(fixture['forecast'],'0.5')]:
            for offset in range(0,count,35_136):
                self.revise(series,instant(start+step*offset),[value]*min(35_136,count-offset))
        return count

    def preview(self, selected_terms, first=None, last=None, quantity='80', scope='draft', ids=None,
                known=None, frame=None, detail=None, expected=200, **changes):
        m=self.manifest
        body={'snapshotId':frame or self.frame,'definitionRevisionId':selected_terms['definitionRevisionId'],
              'referenceCatalogId':selected_terms['referenceCatalogId'],'referenceRevisionId':selected_terms['referenceRevisionId'],
              'scope':scope,'selectedIds':ids or [],'explicitSubset':scope=='trades',
              'terms':selected_terms if scope=='draft' else None,'quantity':quantity if scope=='draft' else None,
              'coverageFrom':first if scope=='draft' else None,'coverageTo':last if scope=='draft' else None,
              'knownAt':known or self.cutoff(),'pendingActuals':'require_actuals_v1','detailMonths':detail or []}
        body.update(changes)
        return self.client.call('ppa/preview',body,expected=expected)

    def book(self, selected_terms, first, last, book=None, quantity='80'):
        if book is None:
            book='d1000000-0000-0000-0000-000000000002'
        body={'operationId':str(uuid4()),'snapshotId':self.frame,'tradeId':None,'expectedRevision':None,
              'bookId':book,'boundaryAt':None,'coverageFrom':first,'coverageTo':last,'quantity':quantity,
              'reason':'Synthetic durable PPA HTTP qualification','memo':'','booking':trade_booking(),'terms':selected_terms}
        return self.client.call('ppa/book',body), body


def check_http_reuse(fixture):
    """Two durable trade bindings share one covered unit calculation in one request."""
    report={'instruments':1,'bindings':2,'sides':5,'scenarios':1,'scope':'explicit_trade_subset'}
    try:
        fixture.load(60,'2031-11-01','2031-12-01')
        selected=terms(fixture.manifest)
        first,last='2031-11-02T04:00:00Z','2031-11-03T05:00:00Z'
        trades=[fixture.book(selected,first,last,quantity=mw)[0]['tradeId'] for mw in ('80','20')]
        result=fixture.preview(selected,scope='trades',ids=trades,known=fixture.cutoff())
        oracle,cells,_,_=expected_value('2031-11-02','2031-11-03','America/New_York',contract_mw=100)
        assert fraction(result['valueExcludingOverdue'])==oracle and fraction(result['completePv'])==oracle
        assert len(result['bindings'])==2 and len({b['instrumentId'] for b in result['bindings']})==1
        assert result['scope']['scope']=='trades' and result['scope']['explicitSubset']
        assert set(result['scope']['selectedIds'])==set(trades)
        assert result['work']['unitValuations']==1 and result['work']['reusedUnitValues']==1
        assert result['work']['uniqueSideCalculations']==5 and result['work']['rowEvaluations']==5*cells
        report.update(status='passed',work=result['work'],
                      expectedValue={'numerator':str(oracle.numerator),'denominator':str(oracle.denominator)})
    except Exception as error:
        report.update(status='qualification_miss',error=f'{type(error).__name__}: {error}')
    print('PPA_HTTP_REUSE='+json.dumps(report,sort_keys=True),flush=True)
    return report


def run_benchmark(fixture, query, api_pid, dotnet):
    from benchmark_interval_series import environment
    metadata=environment(query,dotnet)
    metadata['apiArtifacts']=api_artifacts(api_pid)
    report={'schema':'ppa_full_http_base_v1','environment':metadata,
            'measuredAtUtc':datetime.now(timezone.utc).isoformat(),
            'requestTimeoutSeconds':20,'protocol':'One measured full HTTP book preview per workload after the short qualification suite, audited input upload and durable booking. The service/JIT is therefore warm; each preview has fresh request-owned economic preparation. Includes current authorization, exact context admission, database reads, preparation, valuation, serialization, transfer and JSON parsing. No cross-request economic reuse.',
            'memory':'Server allocation delta is process-wide. A client daemon samples the owned API RSS every 100 ms during the preview, including boundary samples; its maximum is a sampled peak, not an OS high-water mark. The report includes target frequency, sample count and maximum observed gap. Completion RSS is a sample. A positive .NET peak working set is a process-lifetime high-water mark; zero is reported as unavailable.',
            'comparability':'Hourly uses the managed Eastern weekday 07-23 baseline. Quarter-hour uses explicitly pinned Central weekday 06-22 and an ERCOT 15-minute source, required by unchanged collected-source compatibility. Both are independently counted with their declared native clocks; no provider actuals are present in these future workloads.',
            'reuseCheck':check_http_reuse(fixture),'results':[]}
    for minutes,years in [(60,20),(15,10)]:
        first='2031-11-01';last=f'{2031+years}-11-01'
        config=fixture.manifest['ppaBookings']['workloads'][str(minutes)]
        oracle,cells,hours,peak=expected_value(first,last,config['zone'],minutes,config['peakStart'])
        result={'name':f'{years}y_{minutes}m','from':first,'to':last,'clock':config['clock'],'peakStartHour':config['peakStart'],
                'years':years,'months':12*years,'instruments':1,'bindings':1,'books':1,'sides':5,'scenarios':1,
                'nativeIntervals':cells,'rowEvaluationsExpected':5*cells,'uniqueEnergyBlockContracts':24*years,
                'certificateVintageContracts':12*years,'elapsedHours':str(hours),'peakHours':str(peak),
                'expectedValue':{'numerator':str(oracle.numerator),'denominator':str(oracle.denominator)}}
        try:
            result['stage']='audited_input_upload'
            uploaded=fixture.load(minutes,first,last);assert uploaded==cells
            selected=terms(fixture.manifest,minutes,first,last)
            start,end=(instant(midnight(day,config['zone'])) for day in (first,last))
            result['stage']='durable_booking'
            booked,body=fixture.book(selected,start,end,book=f'd1000000-0000-0000-0000-{3 if minutes == 60 else 4:012}')
            known=fixture.cutoff()
            result['stage']='full_http_preview'
            sampler=ProcessRssSampler(api_pid)
            try:
                with sampler:
                    started=time.perf_counter()
                    try:
                        value=fixture.preview(selected,scope='books',ids=[body['bookId']],known=known)
                    finally:
                        result['fullHttpSeconds']=time.perf_counter()-started
            finally:
                result['rssSampling']=sampler.report()
                result['sampledPeakResidentBytes']=result['rssSampling']['sampledPeakResidentBytes']
                result['completionResidentBytes']=result['rssSampling']['completionResidentBytes']
            assert fraction(value['valueExcludingOverdue'])==oracle
            assert result['fullHttpSeconds']<20, 'Full HTTP wall time exceeds the 20-second request contract'
            assert fraction(value['completePv'])==oracle
            assert len(value['bindings'])==1 and value['work']['unitValuations']==1 and value['work']['uniqueSideCalculations']==5
            assert value['work']['rowEvaluations']==5*cells
            assert all(not s['measures']['rows'] for s in value['bindings'][0]['sides'])
            assert result['rssSampling']['status']=='sampled', 'RSS observation was incomplete'
            measurement=dict(value['measurement'])
            dotnet_peak=measurement.pop('peakWorkingSetBytes')
            measurement['dotnetProcessLifetimePeakWorkingSetBytes']=dotnet_peak if dotnet_peak>0 else None
            measurement['dotnetPeakWorkingSetStatus']='available' if dotnet_peak>0 else 'unavailable'
            result.update(status='passed',stage='complete',work=value['work'],
                          measurement=measurement,receiptSnapshot=value['receipt']['snapshot'])
        except Exception as error:
            result.update(status='qualification_miss',error=f'{type(error).__name__}: {error}')
        report['results'].append(result)
        print('PPA_HTTP_WORKLOAD='+json.dumps(result,sort_keys=True),flush=True)
    print('PPA_HTTP_BENCHMARK='+json.dumps(report,sort_keys=True),flush=True)
    return report
