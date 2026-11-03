using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using Aict.Beta.Service.Wfp;
namespace Aict.Beta.Service;
internal sealed class NetworkGate:IDisposable {
    IntPtr engine;readonly Guid provider=Key("provider"),sublayer=Key("sublayer");
    readonly Dictionary<string,string> identities;
    readonly string identityFile=Path.Combine(Env.Root,"state","network-identities.json");
    static readonly Guid AppId=new("d78e1e87-8644-4ea5-9437-d809ecefc971"),UserId=new("af043a0a-b34d-4f86-979c-c90371af6e66");
    static readonly Guid[] Layers=[new("c38d57d1-05a7-4c33-904f-7fbceee60e82"),new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4")];
    static Guid Key(string name)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(Env.Name+"/"+name)).AsSpan(0,16));
    static void Check(uint code,string operation){if(code!=0)throw new InvalidOperationException(operation+" 0x"+code.ToString("X8"));}
    public NetworkGate() {
        identities=File.Exists(identityFile)?Aict.Core.PolicyJson.Parse<Dictionary<string,string>>(File.ReadAllText(identityFile)):new(StringComparer.OrdinalIgnoreCase);
        if(IntPtr.Size!=8 || Marshal.SizeOf<Filter>()!=200 || Marshal.SizeOf<Condition>()!=40)throw new InvalidOperationException("WFP ABI mismatch");
        var session=new Session {display=new Display {name=Env.Name+" network gate"}};Check(Native.FwpmEngineOpen0(IntPtr.Zero,10,IntPtr.Zero,ref session,out engine),"WFP open");
        try {EnsureObjects();}catch{Dispose();throw;}
    }
    void EnsureObjects() {
        Guid pk=provider,sk=sublayer;IntPtr p=IntPtr.Zero,s=IntPtr.Zero;uint code=Native.FwpmProviderGetByKey0(engine,ref pk,out p);
        try{if(code==0){var actual=Marshal.PtrToStructure<Provider>(p);if(actual.display.name!=Env.Name || (actual.flags&1)==0)throw new InvalidDataException("WFP provider changed");}else if(code==0x80320005){var own=new Provider {key=provider,flags=1,display=new Display {name=Env.Name}};Check(Native.FwpmProviderAdd0(engine,ref own,IntPtr.Zero),"WFP provider add");}else Check(code,"WFP provider get");}finally{if(p!=IntPtr.Zero)Native.FwpmFreeMemory0(ref p);}
        code=Native.FwpmSubLayerGetByKey0(engine,ref sk,out s);try{if(code==0){var actual=Marshal.PtrToStructure<Sublayer>(s);if(actual.display.name!=Env.Name || actual.provider==IntPtr.Zero || Marshal.PtrToStructure<Guid>(actual.provider)!=provider || (actual.flags&1)==0)throw new InvalidDataException("WFP sublayer changed");}else if(code==0x80320007){IntPtr ptr=Marshal.AllocHGlobal(16);try{Marshal.StructureToPtr(provider,ptr,false);var own=new Sublayer {key=sublayer,flags=1,provider=ptr,weight=0x7000,display=new Display{name=Env.Name}};Check(Native.FwpmSubLayerAdd0(engine,ref own,IntPtr.Zero),"WFP sublayer add");}finally{Marshal.FreeHGlobal(ptr);}}else Check(code,"WFP sublayer get");}finally{if(s!=IntPtr.Zero)Native.FwpmFreeMemory0(ref s);}
    }
    bool Exists(string path,Guid layer) {
        Guid key=Key(path.ToLowerInvariant()+"/"+layer);uint code=Native.FwpmFilterGetByKey0(engine,ref key,out IntPtr ptr);if(code==0x80320003)return false;Check(code,"WFP filter get");
        try {var f=Marshal.PtrToStructure<Filter>(ptr);if(f.display.name!=Env.Name || f.provider==IntPtr.Zero || Marshal.PtrToStructure<Guid>(f.provider)!=provider || f.sublayer!=sublayer || f.layer!=layer || f.action.type!=0x1001 || (f.flags&1)==0 || f.conditionCount!=2)throw new InvalidDataException("WFP owned filter changed");
            var conditions=Enumerable.Range(0,2).Select(i=>Marshal.PtrToStructure<Condition>(IntPtr.Add(f.conditions,i*40))).ToArray();if(conditions.Count(c=>c.field==AppId)!=1 || conditions.Count(c=>c.field==UserId)!=1)throw new InvalidDataException("WFP condition fields changed");var app=conditions.Single(c=>c.field==AppId);var user=conditions.Single(c=>c.field==UserId);if(app.value.type!=(uint)DataType.ByteBlob || user.value.type!=(uint)DataType.SecurityDescriptor || app.match!=0 || user.match!=0)throw new InvalidDataException("WFP scope changed");
            byte[] expected;if(File.Exists(path)){Check(Native.FwpmGetAppIdFromFileName0(path,out IntPtr native),"WFP expected identity");try{expected=BlobBytes(native);}finally{Native.FwpmFreeMemory0(ref native);}}else if(identities.TryGetValue(path,out var encoded))expected=Convert.FromBase64String(encoded);else throw new InvalidDataException("Removed browser identity unavailable; filter retained");if(!BlobBytes(app.value.data).SequenceEqual(expected))throw new InvalidDataException("WFP executable changed");
            var sd=new RawSecurityDescriptor(BlobBytes(user.value.data),0);if(sd.DiscretionaryAcl is not {Count:1} acl || acl[0] is not CommonAce ace || ace.AceQualifier!=AceQualifier.AccessAllowed || ace.SecurityIdentifier.Value!=Env.Config.ProbeSid || ace.AccessMask!=1)throw new InvalidDataException("WFP managed user changed");return true;
        }finally{Native.FwpmFreeMemory0(ref ptr);}
    }
    static byte[] BlobBytes(IntPtr ptr){var blob=Marshal.PtrToStructure<Blob>(ptr);if(blob.size>65536)throw new InvalidDataException("WFP blob too large");byte[] data=new byte[blob.size];Marshal.Copy(blob.data,data,0,data.Length);return data;}
    void Add(string path,Guid layer) {
        using var user=new UserScope(Env.Config.ProbeSid);
        IntPtr providerPtr=Marshal.AllocHGlobal(16),conditions=Marshal.AllocHGlobal(80),app=IntPtr.Zero;
        try{Marshal.StructureToPtr(provider,providerPtr,false);Check(Native.FwpmGetAppIdFromFileName0(path,out app),"WFP executable identity");identities[path]=Convert.ToBase64String(BlobBytes(app));Env.Save(identityFile,identities);Marshal.StructureToPtr(new Condition{field=AppId,value=new Value{type=(uint)DataType.ByteBlob,data=app}},conditions,false);Marshal.StructureToPtr(user.Condition,IntPtr.Add(conditions,40),false);
            var filter=new Filter{key=Key(path.ToLowerInvariant()+"/"+layer),display=new Display{name=Env.Name},flags=1,provider=providerPtr,layer=layer,sublayer=sublayer,conditionCount=2,conditions=conditions,action=new Wfp.Action{type=0x1001}};Check(Native.FwpmFilterAdd0(engine,ref filter,IntPtr.Zero,out _),"WFP scoped filter add");
        }finally{if(app!=IntPtr.Zero)Native.FwpmFreeMemory0(ref app);Marshal.FreeHGlobal(conditions);Marshal.FreeHGlobal(providerPtr);}
    }
    public void Set(IReadOnlyDictionary<string,bool> pathBlocked) {
        Check(Native.FwpmTransactionBegin0(engine,0),"WFP transaction");bool committed=false;
        try{foreach(var pair in pathBlocked)foreach(var layer in Layers){bool exists=Exists(pair.Key,layer);if(pair.Value && !exists)Add(pair.Key,layer);else if(!pair.Value && exists){Guid key=Key(pair.Key.ToLowerInvariant()+"/"+layer);Check(Native.FwpmFilterDeleteByKey0(engine,ref key),"WFP remove owned filter");}}Check(Native.FwpmTransactionCommit0(engine),"WFP commit");committed=true;}finally{if(!committed)Native.FwpmTransactionAbort0(engine);}
        foreach(var pair in pathBlocked)foreach(var layer in Layers)if(Exists(pair.Key,layer)!=pair.Value)throw new InvalidDataException("WFP readback mismatch");
    }
    public void RemoveObjects(IEnumerable<string> paths) {Set(paths.ToDictionary(p=>p,_=>false,StringComparer.OrdinalIgnoreCase));Guid sk=sublayer,pk=provider;Check(Native.FwpmSubLayerDeleteByKey0(engine,ref sk),"WFP sublayer cleanup");Check(Native.FwpmProviderDeleteByKey0(engine,ref pk),"WFP provider cleanup");}
    public void Dispose(){if(engine!=IntPtr.Zero){Native.FwpmEngineClose0(engine);engine=IntPtr.Zero;}}
}
