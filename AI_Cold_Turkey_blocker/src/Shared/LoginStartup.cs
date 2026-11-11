using System;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
namespace Aict.Startup;

internal interface ILoginStartupStore {
    string? Command {get;set;}
    bool Configured {get;set;}
}
internal sealed class LoginStartup {
    internal const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    internal readonly string Id,Command;
    internal LoginStartup(string root){
        root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);string name=Path.GetFileName(root);
        if(!name.StartsWith("AICT-Beta-",StringComparison.Ordinal)||!Guid.TryParseExact(name[10..],"N",out _)||!root.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),name),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Installed product location required for startup.");
        Id=name[10..];Command="\""+Path.Combine(root,@"App\Aict.Beta.Desktop.exe")+"\" --startup";
        if(Command.Length>260)throw new InvalidDataException("Startup command is too long.");
    }
    internal bool Enabled(ILoginStartupStore store)=>string.Equals(store.Command,Command,StringComparison.Ordinal);
    internal bool Initialize(ILoginStartupStore store){if(!store.Configured)Set(store,true);return Enabled(store);}
    internal void Set(ILoginStartupStore store,bool enabled){
        string? current=store.Command;
        if(current is not null&&!string.Equals(current,Command,StringComparison.Ordinal))throw new InvalidDataException("自启动入口已被其他内容占用，未覆盖。请导出诊断。");
        store.Command=enabled?Command:null;
        if(Enabled(store)!=enabled||(!enabled&&store.Command is not null))throw new IOException("自启动设置未能保存，请重试。");
        store.Configured=true;
    }
    internal bool RemoveOwned(ILoginStartupStore store){if(!Enabled(store))return false;store.Command=null;return store.Command is null;}
    internal RegistryLoginStartupStore Open(string sid)=>new(sid,Id);
}
internal sealed class RegistryLoginStartupStore:ILoginStartupStore,IDisposable {
    readonly RegistryKey user;readonly string name,preferences;
    internal RegistryLoginStartupStore(string sid,string id){
        _=new SecurityIdentifier(sid);if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Invalid installation identity");
        user=Registry.Users.OpenSubKey(sid,true)??throw new IOException("目标用户尚未登录，无法配置自启动。");
        name="AICT-Desktop-"+id;preferences=@"Software\AICT\Desktop\"+id;
    }
    public string? Command {
        get{using var key=user.OpenSubKey(LoginStartup.RunKey);object? value=key?.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);return value is null?null:value as string??throw new InvalidDataException("Unexpected startup entry type");}
        set{if(value is null){using var key=user.OpenSubKey(LoginStartup.RunKey,true);key?.DeleteValue(name,false);}else{using var key=user.CreateSubKey(LoginStartup.RunKey,true);key.SetValue(name,value,RegistryValueKind.String);}}
    }
    public bool Configured {
        get{using var key=user.OpenSubKey(preferences);return key?.GetValue("StartupConfigured") is int value&&value==1;}
        set{using var key=user.CreateSubKey(preferences,true);key.SetValue("StartupConfigured",value?1:0,RegistryValueKind.DWord);}
    }
    public void Dispose()=>user.Dispose();
}
