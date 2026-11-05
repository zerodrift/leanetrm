using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

// D318: a curve definition is validated against its reference terms, and its contracts are the first positions of a date mapping.
internal static class CurveDefinitionChecks
{
    public static void Run()
    {
        var passed=0;
        void Check(string name,Action work){work();passed++;Console.WriteLine($"PASS {name}");}
        void Reject(string code,Action work){try{work();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}");}
        var one=ExactDecimal.Parse("1");
        var terms=new CurveReferenceTerms(
            [new("natural_gas","hub_a"),new("natural_gas","hub_b")],
            ImmutableDictionary<string,CurveQuoteTerms>.Empty.Add("usd_mmbtu",new("natural_gas","usd","mmbtu",one)).Add("eur_mmbtu",new("natural_gas","eur","mmbtu",one)),
            ImmutableDictionary<string,CurveProfileTerms>.Empty.Add("gas_day",new("utc","UTC",1440)),
            ImmutableDictionary<string,CurveTargetTerms>.Empty.Add("hub_a_index",new("index","hub_a_monthly",null))
                .Add("exchange_settlement",new("future_series",null,null)).Add("hub_spread",new("index_difference","hub_a_monthly","hub_b_monthly")),
            ImmutableDictionary<string,CurveIndexTerms>.Empty.Add("hub_a_monthly",new("natural_gas","hub_a","usd","mmbtu",one))
                .Add("hub_b_monthly",new("natural_gas","hub_b","usd","mmbtu",one)),
            ImmutableDictionary<string,CurveFuturesProductTerms>.Empty.Add("syn_gas",new("synthetic","natural_gas","hub_a","usd","mmbtu",one))
                .Add("syn_placeholder",new("placeholder","natural_gas","hub_a","usd","mmbtu",one)),
            [new("syn_gas_2021_01","syn_gas",new(2021,1,1),new(2021,2,1),new(2020,12,29,15,30,0,TimeSpan.Zero)),
                new("syn_gas_2021_02","syn_gas",new(2021,2,1),new(2021,3,1),new(2021,1,29,15,30,0,TimeSpan.Zero)),new("syn_gas_placeholder","syn_gas",null,null,null)],
            ["synthetic_marks"]);
        var forward=new CurveDefinitionDraft("hub_a_fwd","Hub A forward","natural_gas","hub_a","usd_mmbtu","gas_day","forward",null,
            new(Guid.NewGuid(),1,2),"hub_a_index","direct","trader_mark","synthetic_marks","reject","none");
        var future=forward with{Code="syn_gas_fut",ContractKind="future",FuturesProduct="syn_gas",SettlementTarget="exchange_settlement",MarkRole="daily_settlement"};
        BoundContractDefinition Mapping(params (string Label,DateOnly Start,DateOnly End)[] rows)=>new("gas_months","Gas months",new("2020-12-15",1),
            [..rows.Select(r=>new DatedContractDefinition(r.Label,r.Start,r.End,new(r.Start.AddDays(-3).ToDateTime(new(15,30)),TimeSpan.Zero),
                Receipt(r.Start),Receipt(r.End),Receipt(r.Start.AddDays(-3)),"15:30"))]);
        static DateReceipt Receipt(DateOnly date)=>new("oe.ng[0]",date,"oe.ng",0,null,null,"synthetic");
        var months=Mapping(("Jan 21",new(2021,1,1),new(2021,2,1)),("Feb 21",new(2021,2,1),new(2021,3,1)),("Mar 21",new(2021,3,1),new(2021,4,1)));

        Check("a forward curve carries the first mapping positions with its declared terms",()=>{
            var preview=CurveDefinitions.Preview(forward,terms,months);
            if(preview.Contracts.Length!=2||preview.Contracts[0] is not {Position:1,Label:"Jan 21",FuturesSeries:null}||preview.Contracts[1].Start!=new DateOnly(2021,2,1))
                throw new Exception("Wrong contract window");
            if(preview.Terms is not {Currency:"usd",Unit:"mmbtu",TimeZone:"UTC",SettlementKind:"index",SettlementIndex:"hub_a_monthly"})throw new Exception("Wrong curve terms");
            if(preview.Contracts[0].LastTradingAt!=new DateTimeOffset(2020,12,29,15,30,0,TimeSpan.Zero)||preview.Contracts[0].StartReference.Expression!="oe.ng[0]")
                throw new Exception("Contract dates or receipts lost");
        });
        Check("a later context's mapping gives the later contracts",()=>{
            var later=CurveDefinitions.Preview(forward,terms,Mapping(("Feb 21",new(2021,2,1),new(2021,3,1)),("Mar 21",new(2021,3,1),new(2021,4,1))));
            if(later.Contracts.Select(c=>c.Label).SequenceEqual(["Feb 21","Mar 21"])is false)throw new Exception("The window did not roll");
        });
        Check("the contract count must fit within the mapping",()=>{
            Reject("curve_contract_count",()=>CurveDefinitions.Preview(forward with{Contracts=forward.Contracts! with{Count=4}},terms,months));
            Reject("curve_contract_count",()=>CurveDefinitions.Preview(forward with{Contracts=forward.Contracts! with{Count=0}},terms,months));
        });
        Check("every field is explicit and enumerations are exact",()=>{
            Reject("curve_definition",()=>CurveDefinitions.Preview(forward with{Quote=null},terms,months));
            Reject("curve_definition",()=>CurveDefinitions.Preview(forward with{Contracts=null},terms,months));
            Reject("curve_definition",()=>CurveDefinitions.Preview(forward with{MissingPrice="zero"},terms,months));
            Reject("curve_definition",()=>CurveDefinitions.Preview(forward with{Code="Hub A"},terms,months));
            Reject("curve_definition",()=>CurveDefinitions.Preview(forward with{FuturesProduct="syn_gas"},terms,months));
            Reject("curve_definition",()=>CurveDefinitions.Preview(future with{FuturesProduct=null},terms,months));
        });
        Check("references must exist in the pinned reference revision",()=>{
            Reject("curve_reference",()=>CurveDefinitions.Preview(forward with{Location="hub_c"},terms,months));
            Reject("curve_reference",()=>CurveDefinitions.Preview(forward with{Profile="power_hour"},terms,months));
            Reject("curve_reference",()=>CurveDefinitions.Preview(forward with{MarkSource="other"},terms,months));
            Reject("curve_reference",()=>CurveDefinitions.Preview(future with{FuturesProduct="other"},terms,months));
            Reject("curve_reference",()=>CurveDefinitions.Preview(future with{FuturesProduct="syn_placeholder"},terms,months));
        });
        Check("shared terms must agree with the settlement index and futures product",()=>{
            Reject("curve_terms_mismatch",()=>CurveDefinitions.Preview(forward with{Quote="eur_mmbtu"},terms,months));
            Reject("curve_terms_mismatch",()=>CurveDefinitions.Preview(forward with{Location="hub_b"},terms,months));
            Reject("curve_terms_mismatch",()=>CurveDefinitions.Preview(future with{Quote="eur_mmbtu"},terms,months));
            Reject("curve_terms_mismatch",()=>CurveDefinitions.Preview(future with{SettlementTarget="hub_a_index"},terms,months));
        });
        Check("Coming soon cases are refused rather than replaced",()=>{
            Reject("curve_unsupported",()=>CurveDefinitions.Preview(forward with{PriceRule="formula"},terms,months));
            Reject("curve_unsupported",()=>CurveDefinitions.Preview(forward with{MissingPrice="carry_forward"},terms,months));
            Reject("curve_unsupported",()=>CurveDefinitions.Preview(forward with{Interpolation="linear"},terms,months));
            Reject("curve_unsupported",()=>CurveDefinitions.Preview(forward with{SettlementTarget="hub_spread"},terms,months));
            Reject("curve_unsupported",()=>CurveDefinitions.Preview(forward with{SettlementTarget="exchange_settlement"},terms,months));
            if(CurveDefinitions.Choices.Where(c=>!c.Supported).Select(c=>c.Field+":"+c.Code).Order().SequenceEqual(
                ["interpolation:linear","missingPrice:carry_forward","missingPrice:interpolate","priceRule:formula",
                    "settlement:forward_future_series","settlement:hourly_physical","settlement:index_difference"]) is false)
                throw new Exception("The served Coming soon choices changed");
        });
        Check("each futures position matches exactly one series of its product by delivery period and last trading",()=>{
            var preview=CurveDefinitions.Preview(future,terms,months);
            if(preview.Contracts.Select(c=>c.FuturesSeries).SequenceEqual(["syn_gas_2021_01","syn_gas_2021_02"]) is false)throw new Exception("Wrong series");
            Reject("curve_contract_series",()=>CurveDefinitions.Preview(future with{Contracts=future.Contracts! with{Count=3}},terms,months));
            var duplicated=terms with{Series=terms.Series.Add(new("syn_gas_2021_01_copy","syn_gas",new(2021,1,1),new(2021,2,1),null))};
            Reject("curve_contract_series",()=>CurveDefinitions.Preview(future,duplicated,months));
            var moved=terms with{Series=[..terms.Series.Select(s=>s.Code=="syn_gas_2021_02"?s with{LastTradingAt=s.LastTradingAt!.Value.AddDays(-1)}:s)]};
            Reject("curve_terms_mismatch",()=>CurveDefinitions.Preview(future,moved,months));
            var unknown=terms with{Series=[..terms.Series.Select(s=>s.Code=="syn_gas_2021_01"?s with{LastTradingAt=null}:s)]};
            Reject("curve_terms_mismatch",()=>CurveDefinitions.Preview(future,unknown,months));
        });
        Check("positions are in increasing, non-overlapping delivery order",()=>{
            Reject("curve_contract_order",()=>CurveDefinitions.Preview(forward,terms,Mapping(("Feb 21",new(2021,2,1),new(2021,3,1)),("Jan 21",new(2021,1,1),new(2021,2,1)))));
            Reject("curve_contract_order",()=>CurveDefinitions.Preview(forward,terms,Mapping(("Q1 21",new(2021,1,1),new(2021,4,1)),("Feb 21",new(2021,2,1),new(2021,3,1)))));
        });
        Console.WriteLine($"PASS {passed} curve definition checks");
    }
}
