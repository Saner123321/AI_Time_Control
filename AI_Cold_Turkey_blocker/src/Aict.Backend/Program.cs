using System.IO.Pipes;
using Aict.Backend;
using Aict.Contracts;
using Aict.Core;
using Aict.Storage;

if(args.Length!=3 || args[0]!="--preview") {Console.Error.WriteLine("Development preview only: --preview <owned-data-directory> <pipe-name>");return 2;}
string data=Path.GetFullPath(args[1]),pipeName=args[2];
if(!pipeName.StartsWith("AICT-preview-",StringComparison.Ordinal) || pipeName.Length>100 || pipeName.Any(c=>!char.IsAsciiLetterOrDigit(c) && c!='-')) return 2;
Directory.CreateDirectory(data);
try {
    using var singleInstance=new FileStream(Path.Combine(data,"backend.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    string database=Path.Combine(data,"preview.sqlite");
    var repository=File.Exists(database)?StateRepository.OpenExisting(database):StateRepository.CreateNew(database,new EngineSnapshot {
        // Only an empty DEVELOPMENT profile bootstraps without a delay; production onboarding is separate.
        Policy=new PolicyConfiguration {ChangeDelay=TimeSpan.Zero},Clock=new(DateTimeOffset.UtcNow,true)
    });
    using var authority=new PreviewAuthority(repository);
    using var shutdown=new CancellationTokenSource();
    Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;shutdown.Cancel();};
    Task timer=RunTimer();
    var clients=new HashSet<Task>();
    try {
        while(!shutdown.IsCancellationRequested) {
            clients.RemoveWhere(t=>t.IsCompleted);
            if(clients.Count>=8) {await Task.WhenAny(clients);continue;}
            // CurrentUserOnly is suitable ONLY for this same-user preview, not a future SYSTEM service.
            var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,8,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            try {await pipe.WaitForConnectionAsync(shutdown.Token);} catch {pipe.Dispose();throw;}
            clients.Add(Serve(pipe));
        }
    } catch(OperationCanceledException) when(shutdown.IsCancellationRequested) { }
    finally {shutdown.Cancel();await Task.WhenAll(clients);await timer;}
    return 0;
    async Task RunTimer() {
        try {using var ticks=new PeriodicTimer(TimeSpan.FromSeconds(1));while(await ticks.WaitForNextTickAsync(shutdown.Token)) await authority.TickAsync(shutdown.Token);}
        catch(OperationCanceledException) when(shutdown.IsCancellationRequested) { }
    }
    async Task Serve(NamedPipeServerStream pipe) {
        using(pipe) using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token)) {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try {
                string json=await PipeFrames.ReadAsync(pipe,PipeFrames.MaxRequestBytes,timeout.Token);
                PreviewResponse response;
                try {response=await authority.HandleAsync(PolicyJson.Parse<PreviewRequest>(json),timeout.Token);}
                catch(Exception e) when(e is System.Text.Json.JsonException or ArgumentException or InvalidDataException) {response=new(false,"INVALID_REQUEST","请求格式不符合协议。没有保存修改。");}
                await PipeFrames.WriteAsync(pipe,PolicyJson.Serialize(response),PipeFrames.MaxResponseBytes,timeout.Token);
            } catch(Exception e) when(e is IOException or InvalidDataException or OperationCanceledException or System.Text.DecoderFallbackException) { /* Bounded failed client; no state mutation for an incomplete frame. */ }
        }
    }
} catch(Exception e) when(e is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) {
    Console.Error.WriteLine("Preview backend failed without resetting existing data: "+e.Message);return 1;
}
