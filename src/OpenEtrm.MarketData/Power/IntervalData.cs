using System.Collections;
using System.Numerics;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Power;

// Request-owned columns, independent of TOU, instrument and units. The authorized reader keeps
// the series' exact specification and knowledge context. Absent rows are gaps, never zeros.
// Fixed-size chunks bound builder growth; only unusually wide exact values need a heap integer.
public sealed class IntervalData : IReadOnlyList<SeriesCell>
{
    private const int ChunkSize=4096;
    private static readonly BigInteger CompactMinimum=(BigInteger)Int128.MinValue;
    private static readonly BigInteger CompactMaximum=(BigInteger)Int128.MaxValue;
    private sealed class Chunk
    {
        public long[]? Starts=new long[ChunkSize];
        public readonly Int128[] Values=new Int128[ChunkSize];
        public readonly int[] Revisions=new int[ChunkSize];
        public Dictionary<int,BigInteger>? Wide;
    }
    private readonly Chunk[] chunks;
    private readonly long valueDivisor;
    public UtcGrid Grid { get; }
    public int Count { get; }
    public int IntervalMinutes { get; }
    private IntervalData(Chunk[] chunks,int count,int minutes,UtcGrid grid,long valueDivisor)
    {this.chunks=chunks;Count=count;IntervalMinutes=minutes;Grid=grid;this.valueDivisor=valueDivisor;}
    public SeriesCell this[int index]
    {
        get
        {
            CheckIndex(index);var chunk=chunks[index/ChunkSize];var offset=index%ChunkSize;
            return new(Grid.Start(index),Grid.End(index),ExactDecimal.FromScaled(Scaled(index)),chunk.Revisions[offset]);
        }
    }
    internal BigInteger Scaled(int index)
    {
        CheckIndex(index);var c=chunks[index/ChunkSize];var offset=index%ChunkSize;
        return c.Wide is not null && c.Wide.TryGetValue(offset,out var wide)?wide:(BigInteger)c.Values[offset];
    }
    internal int Revision(int index){CheckIndex(index);return chunks[index/ChunkSize].Revisions[index%ChunkSize];}
    public int Find(DateTimeOffset start,DateTimeOffset end)
    {
        if(start.Offset!=TimeSpan.Zero || end.Offset!=TimeSpan.Zero)throw Coverage();
        int index=Grid.Find(start,end);return index>=0?index:throw Coverage();
    }

    // Selection is supplied by the schedule/TOU compiler. No normalization or zero filling.
    public BigInteger SumScaled(ReadOnlySpan<int> selected)
    {
        Int128 compact=0;BigInteger wide=0;
        foreach(int index in selected)
        {
            CheckIndex(index);var c=chunks[index/ChunkSize];int offset=index%ChunkSize;
            if(c.Wide is not null && c.Wide.TryGetValue(offset,out var value)){wide+=value;continue;}
            try{compact=checked(compact+c.Values[offset]);}
            catch(OverflowException){wide+=(BigInteger)compact;compact=c.Values[offset];}
        }
        return wide+(BigInteger)compact;
    }
    private bool Compact(int index,out Int128 value)
    {
        var c=chunks[index/ChunkSize];int offset=index%ChunkSize;value=c.Values[offset];
        return c.Wide is null || !c.Wide.ContainsKey(offset);
    }
    private void Aligned(IntervalData other,UtcSelection mask)
    {
        if(!Grid.Equals(other.Grid) || !Grid.Equals(mask.Grid))
            throw new MarketDataException("interval_alignment","Arithmetic requires the same exact UTC intervals and mask; align inputs explicitly first.");
    }
    private static bool TryProduct(Int128 a,Int128 b,out Int128 product)
    {
        bool negative=(a<0)!=(b<0);
        UInt128 left=a<0?(UInt128)(-(a+1))+1:(UInt128)a,right=b<0?(UInt128)(-(b+1))+1:(UInt128)b;
        UInt128 limit=negative?(UInt128)1<<127:((UInt128)1<<127)-1;
        if(right!=0 && left>limit/right){product=0;return false;}
        UInt128 magnitude=left*right;
        product=negative?(magnitude==((UInt128)1<<127)?Int128.MinValue:-(Int128)magnitude):(Int128)magnitude;
        return true;
    }
    // Exact Σ(aᵢ bᵢ) over selected intervals. Units and whether duration belongs in the
    // expression remain the caller's explicit contract; this kernel never normalizes.
    public (BigInteger Numerator,BigInteger Denominator) Dot(IntervalData other,UtcSelection mask)
    {
        Aligned(other,mask);Int128 compact=0;BigInteger wide=0;
        foreach(int index in mask)
        {
            if(Compact(index,out var a) && other.Compact(index,out var b))
            {
                a/=valueDivisor;b/=other.valueDivisor;
                if(TryProduct(a,b,out var product))
                {
                    if(product>0 && compact>Int128.MaxValue-product || product<0 && compact<Int128.MinValue-product)
                    {wide+=(BigInteger)compact;compact=product;}
                    else compact+=product;
                    continue;
                }
            }
            wide+=(Scaled(index)/valueDivisor)*(other.Scaled(index)/other.valueDivisor);
        }
        var numerator=wide+(BigInteger)compact;var denominator=(ExactDecimal.WorkingScale/valueDivisor)*(ExactDecimal.WorkingScale/other.valueDivisor);
        var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator),denominator);
        return(numerator/gcd,denominator/gcd);
    }
    // Writes only the selected products into caller-owned memory, in interval order.
    // Numerators share the exact denominator 10^36 (two 18-place input columns).
    public void MultiplyInto(IntervalData other,UtcSelection mask,Span<BigInteger> numerators)
    {
        Aligned(other,mask);
        if(numerators.Length!=mask.Count)throw new ArgumentException("Destination must match the selected row count.",nameof(numerators));
        int output=0;foreach(int index in mask)numerators[output++]=Scaled(index)*other.Scaled(index);
    }
    // Column-oriented XᵀY: e.g. many meter/forecast profiles against many price or
    // weight scenarios. Time axes must match exactly; matrix position never guesses a join.
    public static (BigInteger Numerator,BigInteger Denominator)[,] CrossProducts(
        IReadOnlyList<IntervalData> left,IReadOnlyList<IntervalData> right,UtcSelection mask)
    {
        var result=new (BigInteger Numerator,BigInteger Denominator)[left.Count,right.Count];
        foreach(var column in left.Concat(right))
            if(!column.Grid.Equals(mask.Grid))throw new MarketDataException("interval_alignment","Every matrix column must use the selected exact UTC axis.");
        for(int i=0;i<left.Count;i++)for(int j=0;j<right.Count;j++)result[i,j]=left[i].Dot(right[j],mask);
        return result;
    }
    public IEnumerator<SeriesCell> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    private void CheckIndex(int index){if((uint)index>=(uint)Count)throw new ArgumentOutOfRangeException(nameof(index));}
    private static MarketDataException Coverage()=>new("interval_shape_coverage","Every selected interval needs a value with its exact UTC boundaries.");

    public static IntervalData From(IReadOnlyList<SeriesCell> rows,int minutes)
    {
        if(rows is IntervalData data)
        {
            if(data.IntervalMinutes!=minutes)throw Coverage();
            return data;
        }
        var builder=new Builder(minutes);
        foreach(var row in rows.OrderBy(r=>r.Start))builder.Add(row.Start,row.End,row.Value,row.Revision);
        return builder.Build();
    }
    public sealed class Builder
    {
        private readonly List<Chunk> chunks=[];
        private readonly int minutes;
        private readonly long step;
        private long? first,last;
        private int count;
        private bool built;
        private long divisor=1_000_000_000_000_000_000;
        public Builder(int intervalMinutes)
        {
            if(!IntervalSeriesGrid.Resolutions.Contains(intervalMinutes))throw new MarketDataException("interval_series_resolution","Choose a supported interval resolution.");
            minutes=intervalMinutes;step=TimeSpan.FromMinutes(minutes).Ticks;
        }
        // Readers append ordered, already-authorized rows. Gaps are retained by their starts.
        public void Add(DateTimeOffset start,DateTimeOffset end,ExactDecimal value,int revision)
        {
            if(built)throw new MarketDataException("interval_data_sealed","The interval table is immutable after Build.");
            if(start.Offset!=TimeSpan.Zero || end.Offset!=TimeSpan.Zero || end.Ticks-start.Ticks!=step || revision<1 ||
                last is {} previous && start.Ticks-previous<step)throw Coverage();
            if(count%ChunkSize==0)chunks.Add(new());
            var c=chunks[^1];int offset=count%ChunkSize;var scaled=value.ScaledValue;
            c.Starts![offset]=start.Ticks;c.Revisions[offset]=revision;
            if(scaled>=CompactMinimum && scaled<=CompactMaximum)
            {c.Values[offset]=(Int128)scaled;if(divisor>1)divisor=Gcd(divisor,(long)(c.Values[offset]%divisor));}
            else { (c.Wide??=[]).Add(offset,scaled);if(divisor>1)divisor=Gcd(divisor,(long)(scaled%divisor)); }
            first??=start.Ticks;last=start.Ticks;count++;
        }
        private static long Gcd(long a,long b){b=Math.Abs(b);while(b!=0){(a,b)=(b,a%b);}return a;}
        public IntervalData Build()
        {
            if(built)throw new MarketDataException("interval_data_sealed","The interval table is immutable after Build.");
            built=true;
            UtcGrid grid;
            if(count>0 && last!.Value-first!.Value==(long)(count-1)*step)
                grid=UtcGrid.Uniform(new(first.Value,TimeSpan.Zero),new(last.Value+step,TimeSpan.Zero),TimeSpan.FromTicks(step),count);
            else
            {
                var starts=new long[count];
                for(int i=0;i<count;i++)starts[i]=chunks[i/ChunkSize].Starts![i%ChunkSize];
                grid=UtcGrid.Sparse(starts,TimeSpan.FromTicks(step));
            }
            foreach(var chunk in chunks)chunk.Starts=null;
            return new([..chunks],count,minutes,grid,divisor);
        }
    }
}

// A reusable TOU/schedule selection references columns; it never copies their values.
internal sealed class IntervalSelection : IReadOnlyList<SeriesCell>
{
    private readonly IntervalData data;
    private readonly int[] indices;
    private readonly BigInteger sum;
    public int Count=>indices.Length;
    public SeriesCell this[int index]=>data[indices[index]];
    public IntervalSelection(IntervalData data,IReadOnlyList<PowerInterval> periods)
    {
        this.data=data;indices=new int[periods.Count];
        for(int i=0;i<periods.Count;i++)indices[i]=data.Find(periods[i].Start,periods[i].End);
        sum=data.SumScaled(indices.AsSpan());
    }
    public BigInteger SumScaled()=>sum;
    public BigInteger Scaled(int index)=>data.Scaled(indices[index]);
    public int Revision(int index)=>data.Revision(indices[index]);
    public bool SameValues(IntervalSelection other)=>ReferenceEquals(data,other.data)
        ?indices.AsSpan().SequenceEqual(other.indices):this.SequenceEqual(other);
    public IEnumerator<SeriesCell> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}
