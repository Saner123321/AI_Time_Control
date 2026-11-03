using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Aict.Core;
using Microsoft.Win32;

namespace Aict.Service;

// A deliberately narrow deployment gate until isolated integration/recovery has passed.
internal sealed record LabConfiguration(string RunId,string ProbeSid,string TargetSipHash,long TargetSize,bool InteractiveMode=false);
internal static class LabEnvironment {
    internal static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    internal static bool Vmware() {
        using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return Convert.ToString(bios?.GetValue("SystemManufacturer"))?.Contains("VMware",StringComparison.Ordinal)==true
            && Convert.ToString(bios?.GetValue("SystemProductName"))?.Contains("VMware",StringComparison.Ordinal)==true;
    }
    internal static LabConfiguration Guard(bool service,bool interactive=false) {
        if(!Vmware()) throw new InvalidOperationException("VMware guest required; no SCM, policy or file changes performed.");
        using var identity=WindowsIdentity.GetCurrent();
        if(service && identity.User?.Value!="S-1-5-18") throw new InvalidOperationException("The service entry requires LocalSystem");
        string folder=Path.GetFileName(Root);string prefix=interactive?"AICT-Interactive-":"AICT-ServicePolicy-";
        if(!folder.StartsWith(prefix,StringComparison.Ordinal) || !Guid.TryParseExact(folder[prefix.Length..],"N",out _)
            || !string.Equals(Root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),folder),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected protected laboratory root");
        foreach(string directory in new[]{Root,Path.Combine(Root,"Service"),Path.Combine(Root,"state")}) {
            var info=new DirectoryInfo(directory);
            if(!info.Exists || (info.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Expected ordinary protected directory");
            if(service) VerifyAcl(info.GetAccessControl());
        }
        string path=Path.Combine(Root,"config.json");var file=new FileInfo(path);
        if(!file.Exists || file.Length>8192 || (file.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Invalid bounded config");
        if(service) VerifyAcl(file.GetAccessControl());
        var config=PolicyJson.Parse<LabConfiguration>(File.ReadAllText(path));
        if(config.InteractiveMode!=interactive) throw new InvalidDataException("Wrong workspace mode");
        if(config.RunId!=folder[prefix.Length..] || !config.ProbeSid.StartsWith("S-1-5-21-",StringComparison.Ordinal) || config.TargetSipHash.Length!=66 || !config.TargetSipHash.StartsWith("0x",StringComparison.Ordinal) || config.TargetSize<=0)
            throw new InvalidDataException("Invalid fixed laboratory configuration");
        _=new SecurityIdentifier(config.ProbeSid);
        if(!service && (identity.User?.Value!=config.ProbeSid || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || Process.GetCurrentProcess().SessionId==0)) throw new InvalidOperationException("An interactive limited-token client is required");
        return config;
    }
    static void VerifyAcl(FileSystemSecurity acl) {
        string owner=acl.GetOwner(typeof(SecurityIdentifier))?.Value ?? throw new InvalidDataException("Missing privileged input owner");
        if(owner is not ("S-1-5-18" or "S-1-5-32-544")) throw new InvalidDataException("Privileged input is not owned by SYSTEM/Administrators");
        const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.DeleteSubdirectoriesAndFiles;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow && rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544") && (rule.FileSystemRights&writes)!=0) throw new InvalidDataException("Privileged directory/config is writable by an ordinary account");
    }
    internal static void Save(string path,object value) {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
            using var text=new StreamWriter(stream,new System.Text.UTF8Encoding(false),leaveOpen:true);text.Write(PolicyJson.Serialize(value));text.Flush();stream.Flush(true);
        }File.Move(temp,path,true);} finally {if(File.Exists(temp)) File.Delete(temp);}
    }
}
