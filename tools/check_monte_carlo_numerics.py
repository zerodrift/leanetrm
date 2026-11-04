#!/usr/bin/env python3
"""Independent high-precision polar/LDL and binomial-rank oracles, without financial dependencies."""
import argparse
from decimal import Decimal,localcontext
from fractions import Fraction
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
import tempfile

ROOT=Path(__file__).resolve().parents[1]


def run_checks(dotnet):
    with localcontext() as context:
        context.prec=90
        D=Decimal
        extra=[]
        for n in (8,16,128,2048,4096,16384):
            for remaining in (0,1,2,5,10):
                def upper(p):
                    return sum(D(math.comb(n,k))*p**(n-k)*(1-p)**k for k in range(min(remaining,n)+1))
                if upper(D('.5'))>=D('.025') or upper(D('.9999'))<D('.025'):continue
                lo,hi=D('.5'),D('.9999')
                for _ in range(160):
                    mid=(lo+hi)/2
                    if upper(mid)<D('.025'):lo=mid
                    else:hi=mid
                center=((lo+hi)/2).quantize(D('1e-18'))
                for offset in ('-1e-12','-1e-15','-1e-18','0','1e-18','1e-15','1e-12'):
                    value=center+D(offset)
                    if D('.5000000000000001')<value<=D('.9999'):extra.append({'n':n,'confidence':format(value,'f')})
        with tempfile.TemporaryDirectory(prefix='openetrm-mc-numerics-') as owned:
            result=Path(owned)/'oracles.json';inputs=Path(owned)/'cases.json';inputs.write_text(json.dumps(extra))
            subprocess.run([str(dotnet),str(ROOT/'tests/OpenEtrm.MarketData.Checks/bin/Debug/net10.0/OpenEtrm.MarketData.Checks.dll'),'--monte-carlo-oracles',str(result),str(inputs)],cwd=ROOT,check=True)
            data=json.loads(result.read_text())
        def normal(index,pair,seed=20261104):
            for attempt in range(128):
                digest=hashlib.sha256(b'OpenETRM MC polar v1'+struct.pack('<QIIII',seed,index,pair,attempt,0)).digest()
                x=2*(D(int.from_bytes(digest[:8],'little')>>12)+D('.5'))/(2**52)-1
                y=2*(D(int.from_bytes(digest[8:16],'little')>>12)+D('.5'))/(2**52)-1
                radius=x*x+y*y
                if 0<radius<1:
                    scale=(-2*radius.ln()/radius).sqrt()
                    return x*scale,y*scale,attempt,digest.hex()
            raise AssertionError('Independent polar generator exhausted attempts')
        x,y,attempt,digest=normal(0,0)
        assert digest=='5cdac114041a56ba77b1905019c91f560525ee5d9eb46209608b588bd936f4ab' and attempt==0
        assert normal(19,0)[2:]==(1,'055fefa081c00b884284a19dfc0af5375e18eb2c6eceea1f3e5791a863b35d5a')
        assert data['lower']==[['1','0'],['1.25','1']] and data['diagonal']==['4','6.75']
        for sample in data['samples']:
            a,b,_,_=normal(sample['index'],0)
            # Independent high-precision sqrt/log, followed by analytic square root
            # of the hand-computed two-factor covariance [[4,5],[5,13]].
            expected=(2*a,D('2.5')*a+D('6.75').sqrt()*b)
            assert all(abs(D(observed)-ideal)<D('0.00000000000005') for observed,ideal in zip(sample['changes'],expected)),sample
        for boundary in data['boundaries']:
            for sample in boundary['samples']:
                a,b,_,_=normal(sample['index'],0,int(boundary['seed']))
                expected=(2*a,D('2.5')*a+D('6.75').sqrt()*b)
                assert all(abs(D(observed)-ideal)<D('0.00000000000005') for observed,ideal in zip(sample['changes'],expected))
        print('PASS Monte Carlo independent numerics: fixed SHA/polar vectors, rejection, exact LDL and 168 high-precision native shock pairs including seed boundaries',flush=True)
        for case in data['cases']:
            n=case['n'];p=D(case['confidence']);q=1-p
            # Forward arbitrary-exponent Decimal probabilities start at P(B=0),
            # independently of the production mode-centered normalization.
            weights=[q**n]
            for k in range(n):weights.append(weights[-1]*(n-k)/(k+1)*p/q)
            total=sum(weights);assert abs(total-1)<D('1e-70')
            if n<=16:
                exact=[Fraction(math.comb(n,k))*Fraction(p)**k*Fraction(q)**(n-k) for k in range(n+1)]
                assert sum(exact)==1
                assert all(abs(D(v.numerator)/D(v.denominator)-w)<D('1e-75') for v,w in zip(exact,weights))
            lower,upper=0,n+1;tail=D(0)
            for k,w in enumerate(weights):
                tail+=w
                if tail<=D('.025'):lower=k+1
                else:break
            tail=D(0)
            for k in range(n,-1,-1):
                tail+=weights[k]
                if tail<=D('.025'):upper=k
                else:break
            actual=case['interval'];lo=actual['lowerRank'];hi=actual['upperRank']
            assert lo<=lower and hi>=upper and lower-lo<=1 and hi-upper<=1,case
            assert sum(weights[:lo])<=D('.025') and sum(weights[hi:])<=D('.025'),case
            assert actual['lower']==(None if lo==0 else str(lo)) and actual['upper']==(None if hi==n+1 else str(hi)),case
        print(f'PASS Monte Carlo independent quantile intervals: exact small-binomial enumeration and 90-digit tails across {len(data["cases"])} count/confidence cases including cutoff neighbors',flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--dotnet',type=Path,required=True)
    run_checks(parser.parse_args().dotnet.resolve())
