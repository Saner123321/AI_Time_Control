using System;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using AictEnforceLab;

static class AuthenticodeHashCheck {
    static string PackageHash(string path) {using(var hash=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(hash.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
    static int Main(string[] args) {
        try {
            if(args.Length!=2)throw new ArgumentException("Two inert sample files required");
            string target=AuthenticodeHash.Compute(args[0]),control=AuthenticodeHash.Compute(args[1]);
            if(target==control)throw new InvalidDataException("Target and control SIP hashes must differ");
            Console.WriteLine(new JavaScriptSerializer().Serialize(new {Kind="READ_ONLY_SIP_FIXTURE_IDENTITIES_NOT_APPLOCKER_PROOF",TargetSipHash=target,ControlSipHash=control,
                TargetSha256=PackageHash(args[0]),ControlSha256=PackageHash(args[1]),TargetSize=new FileInfo(args[0]).Length}));return 0;
        }catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}
    }
}
