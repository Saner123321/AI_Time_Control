// Documented SIP APIs only. Read-only PE identity; this does not apply policy.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Aict.Desktop {
    static class AuthenticodeHash {
        const string Sha256Oid="2.16.840.1.101.3.4.2.1";
        [StructLayout(LayoutKind.Sequential)] struct Blob {public uint Size;public IntPtr Data;}
        [StructLayout(LayoutKind.Sequential)] struct Algorithm {public IntPtr Oid;public Blob Parameters;}
        [StructLayout(LayoutKind.Sequential)] struct Attribute {public IntPtr Oid;public Blob Value;}
        [StructLayout(LayoutKind.Sequential)] struct IndirectData {public Attribute Data;public Algorithm Algorithm;public Blob Digest;}
        [StructLayout(LayoutKind.Sequential)] struct Subject {
            public uint Size;public IntPtr Guid;public IntPtr File;public IntPtr FileName;public IntPtr DisplayName;
            public uint Reserved1,Version;public IntPtr Provider;public Algorithm Algorithm;
            public uint Flags,Encoding,Reserved2,CapiSettings,SecuritySettings,Index,UnionChoice;
            public IntPtr UnionData,ClientData;
        }
        [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
        [return:MarshalAs(UnmanagedType.Bool)] static extern bool CryptSIPRetrieveSubjectGuid(string file,IntPtr handle,out Guid subject);
        [DllImport("crypt32.dll",SetLastError=true,ExactSpelling=true)]
        [return:MarshalAs(UnmanagedType.Bool)] static extern bool CryptSIPCreateIndirectData(ref Subject subject,ref uint size,IntPtr output);
        static void Assert(bool condition,string message) {if(!condition)throw new InvalidDataException(message);}
        internal static void ValidateLayout() {
            Assert(IntPtr.Size==8,"Only the checked x64 ABI is supported");
            Assert(Marshal.SizeOf(typeof(Blob))==16 && Marshal.SizeOf(typeof(Algorithm))==24 && Marshal.SizeOf(typeof(Attribute))==24,"Crypto blob ABI mismatch");
            Assert(Marshal.SizeOf(typeof(IndirectData))==64 && Marshal.OffsetOf(typeof(IndirectData),"Digest").ToInt32()==48,"SIP digest ABI mismatch");
            Assert(Marshal.SizeOf(typeof(Subject))==128 && Marshal.OffsetOf(typeof(Subject),"Algorithm").ToInt32()==56 && Marshal.OffsetOf(typeof(Subject),"UnionData").ToInt32()==112,"SIP subject ABI mismatch");
        }
        internal static string Compute(string file) {
            ValidateLayout();
            Assert(File.Exists(file) && (File.GetAttributes(file)&FileAttributes.ReparsePoint)==0,"Regular existing fixture required");
            Guid guid;
            if(!CryptSIPRetrieveSubjectGuid(file,IntPtr.Zero,out guid))throw new Win32Exception(Marshal.GetLastWin32Error(),"CryptSIPRetrieveSubjectGuid");
            // Restrict this harness to Windows PE SIP. No third-party/flat-file hash fallback.
            Assert(guid==new Guid("c689aab8-8e78-11d0-8c47-00c04fc295ee"),"Expected Windows PE SIP subject");
            IntPtr guidMemory=IntPtr.Zero,nameMemory=IntPtr.Zero,oidMemory=IntPtr.Zero,data=IntPtr.Zero;
            try {
                guidMemory=Marshal.AllocHGlobal(16);Marshal.StructureToPtr(guid,guidMemory,false);
                nameMemory=Marshal.StringToHGlobalUni(Path.GetFullPath(file));oidMemory=Marshal.StringToHGlobalAnsi(Sha256Oid);
                var subject=new Subject {Size=(uint)Marshal.SizeOf(typeof(Subject)),Guid=guidMemory,File=new IntPtr(-1),FileName=nameMemory,
                    Algorithm=new Algorithm {Oid=oidMemory},Encoding=0x10001,Flags=0x00010000|0x10}; // prohibit resize, omit PE page hashes
                uint size=0;
                if(!CryptSIPCreateIndirectData(ref subject,ref size,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"SIP digest size query");
                Assert(size>=64 && size<=1048576,"Unexpected SIP output size");
                uint allocated=size;data=Marshal.AllocHGlobal((int)allocated);
                if(!CryptSIPCreateIndirectData(ref subject,ref size,data))throw new Win32Exception(Marshal.GetLastWin32Error(),"SIP digest calculation");
                Assert(size<=allocated && size>=64,"SIP output grew beyond allocation");
                var output=Marshal.PtrToStructure<IndirectData>(data);
                Assert(output.Algorithm.Oid!=IntPtr.Zero && Marshal.PtrToStringAnsi(output.Algorithm.Oid)==Sha256Oid,"SIP returned another digest algorithm");
                long offset=output.Digest.Data.ToInt64()-data.ToInt64();
                Assert(output.Digest.Size==32 && offset>=0 && offset+32<=allocated,"SIP digest outside returned buffer");
                byte[] digest=new byte[32];Marshal.Copy(output.Digest.Data,digest,0,32);
                return "0x"+BitConverter.ToString(digest).Replace("-","");
            } finally {
                if(data!=IntPtr.Zero)Marshal.FreeHGlobal(data);if(oidMemory!=IntPtr.Zero)Marshal.FreeHGlobal(oidMemory);
                if(nameMemory!=IntPtr.Zero)Marshal.FreeHGlobal(nameMemory);if(guidMemory!=IntPtr.Zero)Marshal.FreeHGlobal(guidMemory);
            }
        }
    }
}
