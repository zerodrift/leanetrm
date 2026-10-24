"""Independent unit/amount oracles on the owned synthetic preview."""
from preview_client import PreviewClient


def run_checks(url, previous_known_at=None, client=None):
    c = client or PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'viewer'})
    w = c.call('workspace'); refs = w['unitCatalog']['references']
    units = {u['code']: u for u in refs['units']}
    assert {'mmbtu_it','btu_it','therm_it','dekatherm_it','joule','megajoule','gigajoule','kilowatt_hour','megawatt_hour','kilowatt','megawatt','gigawatt','cubic_metre','litre','cubic_foot','mcf','mmcf','us_gallon','petroleum_barrel','kilogram','metric_tonne','pound','short_ton','troy_ounce'} <= units.keys()
    assert units['mmbtu_it']['symbol'] == 'MMBtu (IT)'
    specs = refs['quantity_specifications']
    def spec(commodity, unit, basis=None):
        rows = [q for q in specs if q['commodity_code'] == commodity and q['unit_code'] == unit and (basis is None or q['basis_code'] == basis)]
        assert len(rows) == 1, (commodity, unit)
        return rows[0]['code']
    def convert(value, commodity, source, target, decimals=None, expected=200):
        return c.call('units/convert', {'value': value, 'from': spec(commodity, source), 'to': spec(commodity, target),
                                     'decimalPlaces': decimals, 'tieRule': None if decimals is None else 'to_even'}, expected=expected)
    for commodity, source, target, amount, expected in [
        ('test_gas','dekatherm_it','mmbtu_it','7','7'), ('test_gas','therm_it','mmbtu_it','10','1'),
        ('test_gas','mmbtu_it','gigajoule','1','1.05505585262'),
        ('test_power','megawatt_hour','kilowatt_hour','2.5','2500'),
        ('test_power','megawatt_hour','gigajoule','1','3.6'),
        ('test_gas','mcf','cubic_metre','1','28.316846592'),
        ('test_liquid','petroleum_barrel','us_gallon','2','84'),
        ('test_liquid','us_gallon','cubic_metre','1','0.003785411784'),
        ('test_bulk','short_ton','kilogram','1','907.18474'),
        ('test_bulk','troy_ounce','kilogram','1','0.0311034768')]:
        result = convert(amount,commodity,source,target)
        assert result['value'] == expected and result['exact']
        assert units[target]['symbol'] in result['formatted']
        assert convert(expected,commodity,target,source)['value'] == amount
    print('PASS unit conversion: ten independent energy, power, volume and mass oracles with exact reverse conversions', flush=True)
    assert convert('1','test_power','gigajoule','megawatt_hour',expected=422)['code'] == 'precision_loss'
    rounded = convert('1','test_power','gigajoule','megawatt_hour',decimals=6)
    assert rounded['value'] == '0.277778' and not rounded['exact'] and rounded['formatted'] == '0.277778 MWh'
    source = spec('test_gas','mmbtu_it')
    for target in [spec('test_power','megawatt_hour'),spec('test_gas','cubic_metre')]:
        assert c.call('units/convert',{'value':'1','from':source,'to':target,'decimalPlaces':None,'tieRule':None},expected=422)['code'] == 'quantity_conversion'
    assert c.call('units/convert',{'value':'1','from':spec('test_power','megawatt'),'to':spec('test_power','megawatt_hour'),'decimalPlaces':None,'tieRule':None},expected=422)['code'] == 'quantity_conversion'
    assert c.call('units/convert',{'value':'1','from':source,'to':'missing','decimalPlaces':None,'tieRule':None},expected=422)['code'] == 'missing_reference'
    print('PASS unit conversion: fractional rounding is explicit; commodity, basis, missing-reference and power/energy mismatches reject', flush=True)
    display = c.call('amounts/display',[
        {'value':'3.1','decimalPlaces':8,'tick':'0.025'}, {'value':'12345678901234567890.125','decimalPlaces':2,'tick':None},
        {'value':'-0.005','decimalPlaces':2,'tick':None}, {'value':'1/3','decimalPlaces':6,'tick':None}])
    assert display == ['3.100','12,345,678,901,234,567,890.12','0.00','0.333333']
    assert c.call('amounts/display',[{'value':'1','decimalPlaces':19,'tick':None}],expected=422)['code'] == 'display_precision'
    assert c.call('amounts/display',[None],expected=422)['code'] == 'display_precision'
    print('PASS amount display: tick precision, large exact money, negative ties and rational PV format without binary floating point', flush=True)

    if previous_known_at is not None:
        c.call('market/context',{'effectiveOn':'2020-12-18','knownAt':previous_known_at,'mode':'at','selection':'latest_correction'})
        old=c.call('workspace')
        assert old['definitions']['curves'] and old['unitCatalog'] is None and old['unitCatalogError']=='unit_catalog'
        assert c.call('units/convert',{'value':'1','from':source,'to':source,'decimalPlaces':None,'tieRule':None},expected=422)['code']=='unit_catalog'
        c.call('market/current',{})
        assert c.call('workspace')['unitCatalog'] is not None
        print('PASS unit catalog: old knowledge retains market data while later unit bundle is unavailable; working knowledge restores the exact configured catalog',flush=True)
