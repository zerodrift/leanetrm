"""Synthetic native point curves for the bounded gas swing route checks."""
from delivery_quantity_fixture import add_fixture as location
from preview_fixture import M,R,identity


def add_fixture(reference,market,security,manifest):
    monthly = (('2021-01-01','2021-02-01'),('2021-02-01','2021-03-01'))
    daily = (('2021-01-31','2021-02-01'),('2021-02-01','2021-02-02'),('2021-02-02','2021-02-03'))
    period_starts = [f'{2021+i//12:04d}-{i%12+1:02d}-01' for i in range(41)]
    long_months = tuple(zip(period_starts,period_starts[1:]))
    curves = [
        ('synthetic_swing_long',187000,'3.10',long_months,555),
        ('synthetic_swing',95820,'3.10',monthly,555),
        ('synthetic_intrinsic',95830,('3.20','2.80','3.40'),daily,555),
        ('synthetic_mandatory',95840,('2.90','2.80','2.70'),daily,555),
        ('synthetic_swing_gap',95850,'3.10',monthly[1:],555),
        ('synthetic_swing_overlap',95860,'3.10',(*monthly,daily[0]),555),
        ('synthetic_swing_call',95870,'3.20',monthly[:1],0)]
    for prefix,number,price,periods,minute in curves:
        reference,market,security,manifest=location(reference,market,security,manifest,price=price,prefix=prefix,number=number,
            curve_name=prefix.replace('_',' ').capitalize()+' point',start_minute=minute,months=periods)
    # The daily points share their sealed target index and its independently versioned fixing series.
    fixings = [f"""INSERT INTO market_data.index_fixing_observations VALUES({M},'test_price','{start}','{end}',{R},
        'test_gas_outright','{identity('9',95830+i)}','fixture','{start}T12:00:00Z','published_index_observations_v1');"""
        for i,(start,end) in enumerate(daily)]
    fixings.extend(f"""INSERT INTO market_data.index_fixing_observations VALUES({M},'test_price','{start}','{end}',{R},
        'test_gas_outright','{identity('9',187000+i)}','fixture','{end}T12:00:00Z','published_index_observations_v1')
        ON CONFLICT(catalog_id,revision_id,index_code,observation_from,observation_to) DO NOTHING;"""
        for i,(start,end) in enumerate(long_months))
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(fixings)+'\nUPDATE market_data.revisions SET',1)
    return reference,market,security,manifest


def add_workload(reference, market, security, manifest):
    starts = [f'2041-{month:02d}-01' for month in range(1,13)] + ['2042-01-01']
    reference,market,security,manifest=location(reference,market,security,manifest,
        price=['2.95','3.05']*6,prefix='synthetic_swing_workload',number=195000,
        curve_name='Synthetic swing monthly forwards',start_minute=0,months=tuple(zip(starts,starts[1:])))
    # The physical Side requires the exact monthly fixing-series definition, including for
    # future observations. The workload only consumes its quoted forward before delivery.
    fixings=[f"""INSERT INTO market_data.index_fixing_observations VALUES({M},'test_price','{begin}','{end}',{R},
        'test_gas_outright','{identity('9',195000+i)}','fixture','{end}T12:00:00Z','published_index_observations_v1');"""
        for i,(begin,end) in enumerate(zip(starts,starts[1:]))]
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(fixings)+'\nUPDATE market_data.revisions SET',1)
    observed=next(frame for frame in manifest['frames'] if frame['at']=='2020-12-18T16:00:00Z')
    for at in ('2040-12-31T12:00:00Z','2041-01-02T12:00:00Z'):
        manifest['frames'].append(dict(observed,at=at))
    return reference,market,security,manifest
