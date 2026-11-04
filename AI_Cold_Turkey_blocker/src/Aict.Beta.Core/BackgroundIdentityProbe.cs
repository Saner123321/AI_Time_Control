namespace Aict.Beta;
public sealed record IdentityProbeObservation(string Key,long ElapsedMilliseconds,string? ErrorType,int ErrorCode);
public sealed record IdentityProbeSnapshot(string? Id,bool Pending,string Stage,long PendingMilliseconds);
// One background lookup at a time. A slow PE hash cannot delay the caller's heartbeat.
public sealed class BackgroundIdentityProbe(Func<string,Action<string>,string?> resolve,Action<IdentityProbeObservation>? observe=null,Func<long>? ticks=null):IDisposable {
    readonly object gate=new();readonly Dictionary<string,(string? Id,long At)> cache=new(StringComparer.OrdinalIgnoreCase);
    Func<long> Clock=>ticks??(()=>Environment.TickCount64);
    bool busy,disposed;long started;string stage="idle";
    public IdentityProbeSnapshot Sample(string key){
        lock(gate){
            long now=Clock();bool found=cache.TryGetValue(key,out var entry);
            if(!disposed&&!busy&&(!found||now-entry.At>=2000)){
                busy=true;started=now;stage="queued";_ = Task.Run(()=>Run(key));
            }
            string? id=found&&now-entry.At<=5000?entry.Id:null;
            return new(id,id is null,stage,busy?Math.Max(0,now-started):0);
        }
    }
    void Run(string key){
        string? id=null,error=null;int code=0;long duration;
        try{id=resolve(key,s=>{lock(gate)stage=s;});}catch(Exception e){error=e.GetType().Name;code=e.HResult;}
        lock(gate){duration=Math.Max(0,Clock()-started);if(!disposed){cache[key]=(id,Clock());if(cache.Count>256)cache.Remove(cache.MinBy(p=>p.Value.At).Key);}busy=false;stage="idle";}
        try{observe?.Invoke(new(key,duration,error,code));}catch{/* Diagnostics must not break collection. */}
    }
    public void Dispose(){lock(gate)disposed=true;}
}
