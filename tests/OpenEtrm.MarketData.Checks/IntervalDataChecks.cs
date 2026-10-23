using System.Numerics;
using OpenEtrm.Shared.Temporal;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

internal static class IntervalDataChecks
{
    public static void Run()
    {
        var start=new DateTimeOffset(2031,11,2,5,0,0,TimeSpan.Zero);
        var builder=new IntervalData.Builder(15);
        builder.Add(start,start.AddMinutes(15),ExactDecimal.Parse("0"),1);
        builder.Add(start.AddMinutes(30),start.AddMinutes(45),ExactDecimal.Parse("2.5"),2);
        builder.Add(start.AddMinutes(60),start.AddMinutes(75),ExactDecimal.Parse("999999999999999999999999.999999999999999999"),3);
        var data=builder.Build();
        Equal(data.Count,3);Equal(data[1].Value.ToString(),"2.5");Equal(data[2].Revision,3);
        Equal(data.Find(start,start.AddMinutes(15)),0);
        Reject(()=>data.Find(start.AddMinutes(15),start.AddMinutes(30)));
        Reject(()=>data.Find(start,start.AddMinutes(30)));
        Equal(data.SumScaled([0,1]),BigInteger.Parse("2500000000000000000"));
        Equal(data.SumScaled([0,2]),BigInteger.Parse("999999999999999999999999999999999999999999"));
        Reject(()=>builder.Add(start.AddMinutes(90),start.AddMinutes(105),ExactDecimal.Parse("1"),4));
        var invalid=new IntervalData.Builder(15);
        invalid.Add(start,start.AddMinutes(15),ExactDecimal.Parse("1"),1);
        Reject(()=>invalid.Add(start,start.AddMinutes(15),ExactDecimal.Parse("2"),2));
        Reject(()=>new IntervalData.Builder(15).Add(start,start.AddMinutes(30),ExactDecimal.Parse("1"),1));
        var overflow=new IntervalData.Builder(15);
        for(int i=0;i<4;i++)overflow.Add(start.AddMinutes(15*i),start.AddMinutes(15*(i+1)),ExactDecimal.Parse("99999999999999999999"),1);
        Equal(overflow.Build().SumScaled([0,1,2,3]),BigInteger.Parse("399999999999999999996000000000000000000"));
        var many=new IntervalData.Builder(5);
        for(int i=0;i<9000;i++)many.Add(start.AddMinutes(5*i),start.AddMinutes(5*(i+1)),ExactDecimal.Parse(i%2==0?"1.25":"-0.25"),1);
        var table=many.Build();
        Equal(table.SumScaled(Enumerable.Range(0,9000).ToArray()),BigInteger.Parse("4500000000000000000000"));
        // Two equal local labels across a DST fallback remain different UTC intervals.
        Equal(data.Find(start.AddMinutes(60),start.AddMinutes(75)),2);
        var a=new IntervalData.Builder(15);var b=new IntervalData.Builder(15);
        for(int i=0;i<3;i++){a.Add(start.AddMinutes(i*15),start.AddMinutes((i+1)*15),ExactDecimal.Parse((i+1).ToString()),1);b.Add(start.AddMinutes(i*15),start.AddMinutes((i+1)*15),ExactDecimal.Parse((4+i).ToString()),1);}
        var left=a.Build();var right=b.Build();var mask=left.Grid.Select([0,2]);
        var dot=left.Dot(right,mask);Equal(dot.Numerator,new BigInteger(22));Equal(dot.Denominator,BigInteger.One);
        var products=new BigInteger[2];left.MultiplyInto(right,mask,products);
        Equal(products[0]/BigInteger.Pow(10,36),new BigInteger(4));Equal(products[1]/BigInteger.Pow(10,36),new BigInteger(18));
        var matrix=IntervalData.CrossProducts([left,right],[left,right],mask);
        Equal(matrix[0,0].Numerator,new BigInteger(10));Equal(matrix[0,1].Numerator,new BigInteger(22));Equal(matrix[1,1].Numerator,new BigInteger(52));
        Equal(mask.Intersect(left.Grid.Select([1,2])).Count,1);Equal(mask.Union(left.Grid.Select([1])).Count,3);
        Equal(mask.Overlaps(left.Grid.Select([1])),false);
        var phased=new IntervalData.Builder(60);
        phased.Add(start,start.AddHours(1),ExactDecimal.Parse("1"),1);
        phased.Add(start.AddMinutes(90),start.AddMinutes(150),ExactDecimal.Parse("2"),1);
        Equal(phased.Build().Count,2);
        var utc=PowerClock.Capture(TimeZoneInfo.Utc);
        var tou=TimeOfUse.WeekdayBlock(15,5,6,true);
        var monday=UtcGrid.Uniform(new(2031,11,3,0,0,0,TimeSpan.Zero),new(2031,11,4,0,0,0,TimeSpan.Zero),TimeSpan.FromMinutes(15),96);
        Reject(()=>tou.Select(UtcGrid.Uniform(start,start.AddMinutes(1),TimeSpan.FromSeconds(30),2),utc,[]));
        var compactQuantities=new IntervalQuantityData.Builder(15);
        compactQuantities.Add(new(start,start.AddMinutes(15),ExactDecimal.Parse("12"),1,start,Guid.NewGuid()));
        var stored=compactQuantities.Build();Equal(stored.Values[0].Value.ToString(),"12");
        var header=new QuantitySeries(Guid.NewGuid(),"metered_energy",default,15,null,[]) {Data=stored};
        Reject(()=>{_= (header with {Cells=[new(start,start.AddMinutes(15),ExactDecimal.Parse("99"),1,start,Guid.NewGuid())]}).Values;});
        Reject(()=>{_= (header with {IntervalMinutes=60}).Values;});
        var shifted=new IntervalData.Builder(15);shifted.Add(start.AddMinutes(15),start.AddMinutes(30),One(),1);
        Reject(()=>left.Dot(shifted.Build(),mask));
        Equal(tou.Select(monday,utc,[]).Count,4);Equal(tou.Select(monday,utc,[new(2031,11,3)]).Count,0);
        Equal(left.Grid.Overlaps(UtcGrid.Uniform(start.AddMinutes(45),start.AddMinutes(60),TimeSpan.FromMinutes(15),1)),false);
        // Independent scaled-integer oracle includes full precision, wide values,
        // signed products, cancellation and compact accumulator overflow.
        var random=new Random(211);var scale=BigInteger.Pow(10,18);
        BigInteger[] candidates=[0,1,-1,(BigInteger)Int128.MinValue,(BigInteger)Int128.MaxValue,scale*12,scale*50,BigInteger.Pow(10,41),-BigInteger.Pow(10,41)];
        string Decimal(BigInteger value)=>(value.Sign<0?"-":"")+(BigInteger.Abs(value)/scale)+"."+(BigInteger.Abs(value)%scale).ToString("D18");
        for(int trial=0;trial<20;trial++)
        {
            var x=new IntervalData.Builder(15);var y=new IntervalData.Builder(15);BigInteger oracle=0;
            for(int i=0;i<200;i++)
            {
                var xv=candidates[random.Next(candidates.Length)];var yv=candidates[random.Next(candidates.Length)];
                x.Add(start.AddMinutes(i*15),start.AddMinutes((i+1)*15),ExactDecimal.Parse(Decimal(xv)),1);
                y.Add(start.AddMinutes(i*15),start.AddMinutes((i+1)*15),ExactDecimal.Parse(Decimal(yv)),1);
                if(i%3!=0)oracle+=xv*yv;
            }
            var xx=x.Build();var answer=xx.Dot(y.Build(),xx.Grid.Select(Enumerable.Range(0,200).Where(i=>i%3!=0).ToArray()));
            Equal(answer.Numerator*scale*scale,oracle*answer.Denominator);
        }
        Console.WriteLine("PASS compact interval data: gaps, zeros, exact wide values, revisions, immutable ownership, chunk boundaries and UTC identity");
    }
    static ExactDecimal One()=>ExactDecimal.Parse("1");
    static void Equal<T>(T actual,T expected){if(!Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}.");}
    static void Reject(Action action){try{action();}catch(MarketDataException){return;}throw new Exception("Expected interval data rejection.");}
}
