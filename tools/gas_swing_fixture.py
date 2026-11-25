"""Synthetic native point curves for the bounded gas swing route checks."""
from delivery_quantity_fixture import add_fixture as location
from preview_fixture import M,R,identity


def add_fixture(reference,market,security,manifest):
    monthly = (('2021-01-01','2021-02-01'),('2021-02-01','2021-03-01'))
    daily = (('2021-01-31','2021-02-01'),('2021-02-01','2021-02-02'),('2021-02-02','2021-02-03'))
    curves = [
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
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(fixings)+'\nUPDATE market_data.revisions SET',1)
    return reference,market,security,manifest
