using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Aict.Beta.Service.Wfp;
namespace Aict.Beta.Service;
// FWPM_CONDITION_ALE_USER_ID takes an FWP_SECURITY_DESCRIPTOR_TYPE byte blob, not FWP_SID.
internal sealed class UserScope:IDisposable {
    internal static readonly Guid Field=new("af043a0a-b34d-4f86-979c-c90371af6e66");
    IntPtr bytes,blob;
    [DllImport("advapi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool IsValidSecurityDescriptor(IntPtr descriptor);
    internal Condition Condition=>new(){field=Field,value=new(){type=(uint)DataType.SecurityDescriptor,data=blob}};
    internal UserScope(string sid){
        var descriptor=new RawSecurityDescriptor("O:SYG:SYD:(A;;CC;;;"+sid+")");byte[] data=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(data,0);
        bytes=Marshal.AllocHGlobal(data.Length);blob=Marshal.AllocHGlobal(Marshal.SizeOf<Blob>());
        try{Marshal.Copy(data,0,bytes,data.Length);if(!IsValidSecurityDescriptor(bytes))throw new InvalidDataException("Invalid managed-user security descriptor");Marshal.StructureToPtr(new Blob{size=(uint)data.Length,data=bytes},blob,false);}catch{Dispose();throw;}
    }
    internal static void ValidateOffline(){using var scope=new UserScope("S-1-5-21-1-2-3-1001");var c=scope.Condition;var b=Marshal.PtrToStructure<Blob>(c.value.data);if(c.value.type!=14 || c.field!=Field || c.match!=0 || !IsValidSecurityDescriptor(b.data))throw new InvalidDataException("ALE user scope ABI mismatch");var data=new byte[b.size];Marshal.Copy(b.data,data,0,data.Length);var sd=new RawSecurityDescriptor(data,0);if(sd.DiscretionaryAcl is not {Count:1} acl || acl[0] is not CommonAce ace || ace.SecurityIdentifier.Value!="S-1-5-21-1-2-3-1001" || ace.AccessMask!=1)throw new InvalidDataException("Managed-user scope readback mismatch");}
    public void Dispose(){if(blob!=IntPtr.Zero){Marshal.FreeHGlobal(blob);blob=IntPtr.Zero;}if(bytes!=IntPtr.Zero){Marshal.FreeHGlobal(bytes);bytes=IntPtr.Zero;}}
}
