"""Explicit synthetic PPA contract terms shared by demo booking and acceptance."""

def profile_terms(manifest, strike='40'):
    payment={'frequency':'monthly','rule':'day_of_following_month','days':None,'dayOfMonth':10,'dayBasis':None,'shortMonth':'reject','adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    quantity={'kind':'profile','sources':[],'stage':None,'capacity':'test_capacity_quantity','energy':'test_power_quantity','hours':{'membership':'all','touProduct':None,'touVersion':None,'calendar':'synthetic_eastern_holidays'},'slots':[],'assets':[]}
    slot={'kind':'slots','sources':[],'stage':None,'capacity':None,'energy':None,'hours':None,'slots':[{'role':'energy_delivery','ordinal':0}],'assets':[]}
    physical={'kind':'energy','quantity':quantity,'paymentRule':payment,'location':'test_hub_a','fixedPrice':None,'indexPrice':None,'attribute':None}
    cash={'kind':'fixed','quantity':slot,'paymentRule':payment,'location':None,'fixedPrice':{'kind':'flat','quantity':'test_power_quantity','multiplier':'1','price':strike,'rate':None,'baseDate':None,'basis':None,'steps':[]},'indexPrice':None,'attribute':None}
    return {'definitionRevisionId':manifest['revisionId'],'referenceCatalogId':manifest['referenceCatalogId'],'referenceRevisionId':manifest['referenceRevisionId'],
            'delivery':{'clock':'synthetic_eastern','intervalMinutes':60,'from':'2031-11-01','to':'2031-12-01'},'currency':'usd','unitQuantity':'test_capacity_quantity','discounting':False,'implementation':'ppa_instrument_v2','normalizationAssets':[],
            'memberships':[{'role':'energy_delivery','ordinal':0,'coefficient':'1','side':physical},{'role':'strike_payment','ordinal':0,'coefficient':'-1','side':cash}]}
