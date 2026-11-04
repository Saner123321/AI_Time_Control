using System.Threading.Channels;
namespace Aict.Beta;
// Report only queues an immutable snapshot. Serialization and I/O run on the consumer.
public sealed class DeferredJournal<T> {
    readonly Channel<T> queue;readonly Task worker;long failures;
    public long FailedWrites=>Interlocked.Read(ref failures);
    public DeferredJournal(Func<T,Task> write,int capacity=128){
        queue=Channel.CreateBounded<T>(new BoundedChannelOptions(capacity){SingleReader=true,FullMode=BoundedChannelFullMode.DropOldest});
        worker=Task.Run(async()=>{await foreach(var item in queue.Reader.ReadAllAsync())try{await write(item);}catch{Interlocked.Increment(ref failures);}});
    }
    public bool Report(T item)=>queue.Writer.TryWrite(item);
    public Task CompleteAsync(){queue.Writer.TryComplete();return worker;}
}
