using System.Security.Cryptography;
using Aict.Core;
using Aict.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aict.Core.Tests;

[TestClass]
public sealed class StorageTests {
    static string NewPath() {
        string root=Environment.GetEnvironmentVariable("AICT_TEST_ROOT") ?? throw new InvalidOperationException("Run through the isolated project test script");
        string directory=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);return Path.Combine(directory,"state.sqlite");
    }
    static EngineSnapshot Example() {
        var policy=Samples.Policy() with {ChangeDelay=TimeSpan.FromHours(24)};
        var snapshot=Samples.Snapshot(policy) with {Usage=new UsageLedger().Put(Samples.GroupId,new(2026,11,2),new(TimeSpan.FromSeconds(42).Ticks))};
        return ChangeQueue.Submit(snapshot,policy with {Groups=[policy.Groups[0] with {Enabled=false}]},new(Samples.At(2,10)));
    }
    [TestMethod]
    public void ReopenKeepsQuotaPendingAndDelayButDoesNotTrustPersistedWallClock() {
        string path=NewPath();var before=Example();_ = StateRepository.CreateNew(path,before);
        var read=StateRepository.OpenExisting(path).Read();Assert.AreEqual(0L,read.Revision);
        Assert.AreEqual(before.Pending,read.Snapshot.Pending);Assert.AreEqual(TimeSpan.FromSeconds(42).Ticks,read.Snapshot.Usage.Get(Samples.GroupId,new(2026,11,2)).KnownTicks);
        Assert.IsFalse(read.Snapshot.Clock.Trusted);
        Assert.AreEqual(ApplyStatus.Locked,ChangeQueue.TryApply(read.Snapshot,read.Snapshot.Clock.Reading).Status);
    }
    [TestMethod]
    public void CommitIsVersionedAndRejectsStaleWriter() {
        var repo=StateRepository.CreateNew(NewPath(),Example());var read=repo.Read();var next=read.Snapshot with {Pending=null};
        Assert.IsTrue(repo.TryCommit(0,next));Assert.IsFalse(repo.TryCommit(0,read.Snapshot));
        var current=repo.Read();Assert.AreEqual(1L,current.Revision);Assert.IsNull(current.Snapshot.Pending);
    }
    [TestMethod]
    public async Task ConcurrentWritersCannotBothCommitTheSameRevision() {
        var repo=StateRepository.CreateNew(NewPath(),Example());var snapshot=repo.Read().Snapshot;
        var results=await Task.WhenAll(Task.Run(()=>repo.TryCommit(0,snapshot)),Task.Run(()=>repo.TryCommit(0,snapshot with {Pending=null})));
        Assert.AreEqual(1,results.Count(value=>value));Assert.AreEqual(1L,repo.Read().Revision);
    }
    [TestMethod]
    public void AnUncommittedTransactionCannotReplaceState() {
        string path=NewPath();var repo=StateRepository.CreateNew(path,Example());var original=repo.Read();
        using(var connection=new SqliteConnection($"Data Source={path};Pooling=False")) {
            connection.Open();using var tx=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=tx;
            command.CommandText="UPDATE engine_state SET revision=999,json='uncommitted';";command.ExecuteNonQuery();
            // Disposal without commit must roll back. This is not a physical power-loss test.
        }
        Assert.AreEqual(original.Revision,repo.Read().Revision);Assert.AreEqual(original.Snapshot.Pending,repo.Read().Snapshot.Pending);
    }
    [TestMethod]
    public void CorruptPayloadDoesNotBecomeZeroUsage() {
        string path=NewPath();var repo=StateRepository.CreateNew(path,Example());
        var cached=repo.Read();
        using(var connection=new SqliteConnection($"Data Source={path};Pooling=False")) {
            connection.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE engine_state SET json='{}';";command.ExecuteNonQuery();
        }
        Assert.ThrowsExactly<InvalidDataException>(()=>repo.Read());
        Assert.ThrowsExactly<InvalidDataException>(()=>repo.TryCommit(cached.Revision,cached.Snapshot));
        Assert.ThrowsExactly<IOException>(()=>StateRepository.CreateNew(path,Example()));
    }
    [TestMethod]
    public void MissingRowAndUnknownSchemaAreRejected() {
        string path=NewPath();var repo=StateRepository.CreateNew(path,Example());
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False")) {
            c.Open();using var command=c.CreateCommand();command.CommandText="DELETE FROM engine_state;";command.ExecuteNonQuery();
        }
        Assert.ThrowsExactly<InvalidDataException>(()=>repo.Read());
        string other=NewPath();var second=StateRepository.CreateNew(other,Example());
        using(var c=new SqliteConnection($"Data Source={other};Pooling=False")) {
            c.Open();using var command=c.CreateCommand();command.CommandText="PRAGMA user_version=999;";command.ExecuteNonQuery();
        }
        Assert.ThrowsExactly<InvalidDataException>(()=>second.Read());
    }
    [TestMethod]
    public void ExistingFileIsNeverOverwrittenAndMissingFileNeverCreatedByOpen() {
        string path=NewPath();File.WriteAllText(path,"preserve original bytes");string before=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.ThrowsExactly<IOException>(()=>StateRepository.CreateNew(path,Example()));
        Assert.AreEqual(before,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        string absent=NewPath();Assert.ThrowsExactly<FileNotFoundException>(()=>StateRepository.OpenExisting(absent));Assert.IsFalse(File.Exists(absent));
    }
    [TestMethod]
    public void NegativeUsageCannotBeCommitted() {
        var repo=StateRepository.CreateNew(NewPath(),Example());var state=repo.Read().Snapshot;
        var corrupt=state with {Usage=state.Usage.Put(Samples.GroupId,new(2026,11,2),new(-1))};
        Assert.ThrowsExactly<ArgumentException>(()=>repo.TryCommit(0,corrupt));Assert.AreEqual(0L,repo.Read().Revision);
    }
}
