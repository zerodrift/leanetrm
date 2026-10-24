using System.Numerics;

namespace OpenEtrm.Shared.Temporal;

// Uniform axes retain only origin, step and count. Sparse/nonuniform axes retain exact UTC
// boundaries. Equality is about intervals, never storage layout or local-clock labels.
public sealed class UtcGrid : IEquatable<UtcGrid>
{
    private readonly long origin,step;
    private readonly long[]? starts,ends;
    public int Count { get; }
    private UtcGrid(long origin,long step,int count,long[]? starts=null,long[]? ends=null)
    {this.origin=origin;this.step=step;Count=count;this.starts=starts;this.ends=ends;}
    public static UtcGrid Uniform(DateTimeOffset from,DateTimeOffset to,TimeSpan step,int maximumIntervals)
    {
        RequireUtc(from);RequireUtc(to);
        if(from>=to || step.Ticks<=0 || (to.Ticks-from.Ticks)%step.Ticks!=0)
            throw new TemporalException("grid_alignment","Supply positive, exactly aligned UTC interval bounds and step.");
        var count=(to.Ticks-from.Ticks)/step.Ticks;
        if(maximumIntervals<=0 || count>maximumIntervals || count>=int.MaxValue)
            throw new TemporalException("grid_size","The grid exceeds its explicit interval limit.");
        return new(from.Ticks,step.Ticks,(int)count);
    }
    public static UtcGrid Create(ReadOnlySpan<DateTimeOffset> boundaries)
    {
        if(boundaries.Length<2)throw new TemporalException("grid_boundaries","At least two strictly increasing UTC boundaries are required.");
        long step=boundaries[1].Ticks-boundaries[0].Ticks;bool uniform=true;
        for(int i=0;i<boundaries.Length;i++)
        {
            RequireUtc(boundaries[i]);
            if(i>0 && boundaries[i]<=boundaries[i-1])throw new TemporalException("grid_boundaries","Grid boundaries must strictly increase.");
            if(i>0 && boundaries[i].Ticks-boundaries[i-1].Ticks!=step)uniform=false;
        }
        if(uniform)return new(boundaries[0].Ticks,step,boundaries.Length-1);
        long[] starts=new long[boundaries.Length-1],ends=new long[starts.Length];
        for(int i=0;i<starts.Length;i++){starts[i]=boundaries[i].Ticks;ends[i]=boundaries[i+1].Ticks;}
        return new(starts[0],0,starts.Length,starts,ends);
    }
    // Equal-duration rows may have gaps or different UTC phases after clock transitions.
    public static UtcGrid Sparse(ReadOnlySpan<long> utcStartTicks,TimeSpan duration)
    {
        if(duration.Ticks<=0)throw new TemporalException("grid_alignment","Intervals need a positive duration.");
        bool uniform=true;
        for(int i=0;i<utcStartTicks.Length;i++)
        {
            long at=utcStartTicks[i];
            if(at<0 || at>DateTimeOffset.MaxValue.Ticks-duration.Ticks || i>0 && at-utcStartTicks[i-1]<duration.Ticks)
                throw new TemporalException("grid_boundaries","UTC intervals must be ordered, distinct and nonoverlapping.");
            if(i>0 && at-utcStartTicks[i-1]!=duration.Ticks)uniform=false;
        }
        return new(utcStartTicks.Length==0?0:utcStartTicks[0],duration.Ticks,utcStartTicks.Length,uniform?null:utcStartTicks.ToArray());
    }
    private static void RequireUtc(DateTimeOffset instant)
    {if(instant.Offset!=TimeSpan.Zero)throw new TemporalException("grid_utc","Grid boundaries must be explicit UTC instants.");}
    internal void RequireSlot(int slot)
    {if((uint)slot>=(uint)Count)throw new TemporalException("grid_slot","The interval slot is outside this exact grid.");}
    private long StartTicks(int slot)=>starts is null?origin+slot*step:starts[slot];
    private long EndTicks(int slot)=>ends is null?StartTicks(slot)+step:ends[slot];
    public DateTimeOffset Start(int slot){RequireSlot(slot);return new(StartTicks(slot),TimeSpan.Zero);}
    public DateTimeOffset End(int slot){RequireSlot(slot);return new(EndTicks(slot),TimeSpan.Zero);}
    public long DurationTicks(int slot){RequireSlot(slot);return EndTicks(slot)-StartTicks(slot);}
    public int FindStart(DateTimeOffset at)
    {
        RequireUtc(at);
        if(starts is null)
        {
            long delta=at.Ticks-origin;
            return Count>0 && delta>=0 && delta%step==0 && delta/step<Count?(int)(delta/step):-1;
        }
        int index=Array.BinarySearch(starts,at.Ticks);return index>=0?index:-1;
    }
    public int Find(DateTimeOffset from,DateTimeOffset to)
    {RequireUtc(to);int index=FindStart(from);return index>=0 && EndTicks(index)==to.Ticks?index:-1;}
    public UtcSelection Select(ReadOnlySpan<int> slots)=>new(this,slots);
    public bool Overlaps(UtcGrid other)
    {
        int a=0,b=0;
        while(a<Count && b<other.Count)
        {
            if(StartTicks(a)<other.EndTicks(b) && other.StartTicks(b)<EndTicks(a))return true;
            if(EndTicks(a)<=other.StartTicks(b))a++;else b++;
        }
        return false;
    }
    public bool Equals(UtcGrid? other)
    {
        if(ReferenceEquals(this,other))return true;
        if(other is null || Count!=other.Count)return false;
        if(starts is null && other.starts is null)return Count==0 || origin==other.origin && step==other.step;
        for(int i=0;i<Count;i++)if(StartTicks(i)!=other.StartTicks(i)||EndTicks(i)!=other.EndTicks(i))return false;
        return true;
    }
    public override bool Equals(object? obj)=>obj is UtcGrid other && Equals(other);
    public override int GetHashCode()=>Count==0?0:HashCode.Combine(Count,StartTicks(0),EndTicks(Count-1));
}

public sealed class UtcSelection : IEquatable<UtcSelection>
{
    private readonly ulong[] words;
    public UtcGrid Grid { get; }
    public int Count { get; }
    public long DurationTicks { get; }
    internal UtcSelection(UtcGrid grid,ReadOnlySpan<int> slots)
    {
        Grid=grid;words=new ulong[(int)(((long)grid.Count+63)/64)];
        foreach(int slot in slots){grid.RequireSlot(slot);words[slot>>6]|=1UL<<(slot&63);}
        foreach(int slot in this){Count++;DurationTicks=checked(DurationTicks+grid.DurationTicks(slot));}
    }
    private UtcSelection(UtcGrid grid,ulong[] owned)
    {Grid=grid;words=owned;foreach(int slot in this){Count++;DurationTicks=checked(DurationTicks+grid.DurationTicks(slot));}}
    public bool Contains(int slot){Grid.RequireSlot(slot);return (words[slot>>6]&(1UL<<(slot&63)))!=0;}
    private void Aligned(UtcSelection other)
    {if(!Grid.Equals(other.Grid))throw new TemporalException("grid_alignment","Masks must name the same exact UTC intervals.");}
    public UtcSelection Intersect(UtcSelection other)
    {Aligned(other);var result=new ulong[words.Length];for(int i=0;i<result.Length;i++)result[i]=words[i]&other.words[i];return new(Grid,result);}
    public UtcSelection Union(UtcSelection other)
    {Aligned(other);var result=new ulong[words.Length];for(int i=0;i<result.Length;i++)result[i]=words[i]|other.words[i];return new(Grid,result);}
    public bool Overlaps(UtcSelection other)
    {Aligned(other);for(int i=0;i<words.Length;i++)if((words[i]&other.words[i])!=0)return true;return false;}
    public Enumerator GetEnumerator()=>new(words);
    public struct Enumerator
    {
        private readonly ulong[] words;
        private int word;
        private ulong remaining;
        public int Current { get; private set; }
        internal Enumerator(ulong[] words){this.words=words;word=-1;remaining=0;Current=-1;}
        public bool MoveNext()
        {
            while(remaining==0){if(++word>=words.Length)return false;remaining=words[word];}
            Current=word*64+BitOperations.TrailingZeroCount(remaining);remaining&=remaining-1;return true;
        }
    }
    public bool Equals(UtcSelection? other)=>ReferenceEquals(this,other)||other is not null && Grid.Equals(other.Grid)&&words.AsSpan().SequenceEqual(other.words);
    public override bool Equals(object? obj)=>obj is UtcSelection other&&Equals(other);
    public override int GetHashCode(){var h=new HashCode();h.Add(Grid);foreach(var word in words)h.Add(word);return h.ToHashCode();}
}
