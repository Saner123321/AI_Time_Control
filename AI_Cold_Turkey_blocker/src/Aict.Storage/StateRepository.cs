using System.Text;
using Aict.Core;
using Microsoft.Data.Sqlite;

namespace Aict.Storage;

public sealed record StoredSnapshot(long Revision,EngineSnapshot Snapshot);

// Backend-only path. A future privileged IPC endpoint must not accept a client database path.
public sealed class StateRepository {
    public string Path { get; }
    const int MaxJsonBytes=8*1024*1024;
    StateRepository(string path) { Path=System.IO.Path.GetFullPath(path); }
    SqliteConnection Connect() {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path,Mode=SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=5 }.ToString());
        try {connection.Open();using var command=connection.CreateCommand();command.CommandText="PRAGMA synchronous=FULL;";command.ExecuteNonQuery();return connection;}
        catch {connection.Dispose();throw;}
    }
    static string Encode(EngineSnapshot value) {
        Validate(value);string json=PolicyJson.Serialize(value);
        if(Encoding.UTF8.GetByteCount(json)>MaxJsonBytes) throw new ArgumentException("State exceeds the storage bound");return json;
    }
    static void Validate(EngineSnapshot value) {
        if(value.SchemaVersion!=1 || value.Policy is null || value.Usage is null || value.Usage.Accounts is null || value.Clock is null) throw new ArgumentException("Unsupported or incomplete state");
        PolicyValidation.Validate(value.Policy);
        if(value.Applications.IsDefault || value.Applications.Length>64 || value.Applications.Any(a=>a is null) || value.Applications.Select(a=>a.Id).Distinct(StringComparer.Ordinal).Count()!=value.Applications.Length) throw new ArgumentException("Invalid application catalog");
        foreach(var app in value.Applications) app.Validate();
        if(value.ServiceAnchor is {} anchor && (string.IsNullOrWhiteSpace(anchor.BootId) || anchor.BootId.Length>100 || anchor.UptimeMilliseconds<0)) throw new ArgumentException("Invalid service time anchor");
        foreach(var pair in value.Usage.Accounts) {
            var parts=pair.Key.Split('/');
            if(parts.Length is not (2 or 3) || !Guid.TryParseExact(parts[0],"N",out _) || !DateOnly.TryParseExact(parts[1],"yyyy-MM-dd",out _) || parts.Length==3 && (!Guid.TryParseExact(parts[2],"N",out var slot) || slot==Guid.Empty) || pair.Value is null || pair.Value.KnownTicks<0) throw new ArgumentException("Invalid usage account");
        }
        if(value.Pending is {} p) {
            if(p.Id==Guid.Empty || !Enum.IsDefined(p.Purpose) || p.SubmittedUtc>p.NotBeforeUtc || string.IsNullOrEmpty(p.TargetJson) || p.TargetSha256!=PolicyJson.Sha256(p.TargetJson)) throw new ArgumentException("Invalid frozen pending change");
            PolicyValidation.Validate(PolicyJson.Parse<PolicyConfiguration>(p.TargetJson));
        }
    }
    static void CheckSchema(SqliteConnection connection,SqliteTransaction? transaction=null) {
        using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="PRAGMA user_version;";
        if(Convert.ToInt64(command.ExecuteScalar())!=1) throw new InvalidDataException("Unsupported state database schema; source left unchanged");
    }
    public static StateRepository CreateNew(string path,EngineSnapshot initial) {
        string json=Encode(initial);var repository=new StateRepository(path);
        // CreateNew prevents reinitializing a corrupt/existing database as an empty allowance.
        using(var reservation=new FileStream(repository.Path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { }
        using var connection=repository.Connect();
        using(var wal=connection.CreateCommand()) { wal.CommandText="PRAGMA journal_mode=WAL;";wal.ExecuteScalar(); }
        using var transaction=connection.BeginTransaction(deferred:false);
        using var schema=connection.CreateCommand();schema.Transaction=transaction;
        schema.CommandText="CREATE TABLE engine_state(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL CHECK(revision>=0), json TEXT NOT NULL, sha256 TEXT NOT NULL); PRAGMA user_version=1;";schema.ExecuteNonQuery();
        using var insert=connection.CreateCommand();insert.Transaction=transaction;
        insert.CommandText="INSERT INTO engine_state(id,revision,json,sha256) VALUES(1,0,$json,$sha);";
        insert.Parameters.AddWithValue("$json",json);insert.Parameters.AddWithValue("$sha",PolicyJson.Sha256(json));insert.ExecuteNonQuery();transaction.Commit();return repository;
    }
    public static StateRepository OpenExisting(string path) {
        if(!File.Exists(path)) throw new FileNotFoundException("Existing state database required",path);
        var repository=new StateRepository(path);_ = repository.Read();return repository;
    }
    static StoredSnapshot ReadStored(SqliteConnection connection,SqliteTransaction? transaction=null) {
        CheckSchema(connection,transaction);
        using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT revision,json,sha256 FROM engine_state WHERE id=1;";
        using var reader=command.ExecuteReader();
        if(!reader.Read()) throw new InvalidDataException("Initialized state row missing; refusing quota reset");
        long revision=reader.GetInt64(0);string json=reader.GetString(1),digest=reader.GetString(2);
        if(revision<0 || Encoding.UTF8.GetByteCount(json)>MaxJsonBytes || PolicyJson.Sha256(json)!=digest) throw new InvalidDataException("State checksum/size/revision invalid");
        EngineSnapshot value;
        try { value=PolicyJson.Parse<EngineSnapshot>(json);Validate(value); }
        catch(Exception e) when(e is ArgumentException or System.Text.Json.JsonException) {throw new InvalidDataException("Stored state validation failed; source preserved",e);}
        return new(revision,value);
    }
    public StoredSnapshot Read() {
        using var connection=Connect();var stored=ReadStored(connection);
        // A persisted 'trusted' flag is not authority for a new runtime's wall clock.
        return stored with {Snapshot=stored.Snapshot with {Clock=stored.Snapshot.Clock.AfterRestartWithoutTrustedAnchor()}};
    }
    public bool TryCommit(long expectedRevision,EngineSnapshot value) {
        if(expectedRevision<0 || expectedRevision==long.MaxValue) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        string json=Encode(value);using var connection=Connect();using var transaction=connection.BeginTransaction(deferred:false);CheckSchema(connection,transaction);
        // A cached snapshot must not silently repair/overwrite newly corrupted source state.
        if(ReadStored(connection,transaction).Revision!=expectedRevision) {transaction.Rollback();return false;}
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="UPDATE engine_state SET revision=revision+1,json=$json,sha256=$sha WHERE id=1 AND revision=$expected;";
        command.Parameters.AddWithValue("$json",json);command.Parameters.AddWithValue("$sha",PolicyJson.Sha256(json));command.Parameters.AddWithValue("$expected",expectedRevision);
        bool committed=command.ExecuteNonQuery()==1;if(committed)transaction.Commit();else transaction.Rollback();return committed;
    }
}
