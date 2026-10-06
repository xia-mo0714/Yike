using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class InstallerPhaseTwoTests {
    private static readonly List<string> failures = new List<string>();
    private static MethodInfo Method(string name) { return typeof(Setup).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static); }
    private static bool Reject(string name,params object[] args) { try { Method(name).Invoke(null,args);return false; } catch(TargetInvocationException e) { if(e.InnerException is IOException || e.InnerException is InvalidDataException || e.InnerException is InvalidOperationException || e.InnerException is UnauthorizedAccessException || e.InnerException is BadImageFormatException)return true;throw; } }
    private static void Check(bool value,string name) { Console.WriteLine((value?"PASS ":"FAIL ")+name);if(!value)failures.Add(name); }
    private static int Main(string[] args) {
        try { return Run(args); }
        catch(Exception error) { Console.WriteLine("TEST ERROR TYPE: "+error.GetType().FullName);Console.WriteLine("TEST ERROR: "+error.Message);if(error.InnerException!=null)Console.WriteLine("INNER: "+error.InnerException.GetType().FullName+" "+error.InnerException.Message);return 2; }
    }
    private static int Run(string[] args) {
        string source=Path.GetFullPath(args[0]),root=Path.Combine(Path.GetTempPath(),"Yike-install-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("Created owned test root");
        try {
            foreach(string file in Directory.GetFiles(source,"*",SearchOption.AllDirectories)) {
                string relative=file.Substring(source.TrimEnd('\\').Length+1);
                if(relative.EndsWith("ggml-small-q8_0.bin",StringComparison.OrdinalIgnoreCase))continue;
                string target=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target);
            }
            File.Copy(args[1],Path.Combine(root,"uninstall.ps1"));
            Method("ValidateInstallation").Invoke(null,new object[]{root,false});
            var required=new List<string>{"YikeSpeechWorker.exe","whisper-1.9.4-abi.json","whisper-runtime\\LICENSE-whisper.cpp.txt","whisper-runtime\\LICENSE-SDL2.txt","whisper-runtime\\LICENSE-openai-whisper.txt","whisper-runtime\\THIRD-PARTY-NOTICES.txt"};
            required.AddRange(new[]{"ggml-base.dll","ggml.dll","whisper.dll","SDL2.dll","ggml-cpu-alderlake.dll","ggml-cpu-cannonlake.dll","ggml-cpu-cascadelake.dll","ggml-cpu-haswell.dll","ggml-cpu-icelake.dll","ggml-cpu-sandybridge.dll","ggml-cpu-skylakex.dll","ggml-cpu-sse42.dll","ggml-cpu-x64.dll"}.Select(x=>"whisper-runtime\\Release\\"+x));
            foreach(string relative in required) {
                string file=Path.Combine(root,relative),backup=file+".test-backup";
                File.Move(file,backup);
                try { Check(Reject("ValidateInstallation",root,false),"MissingDependencyRejected: "+relative); }
                finally { File.Move(backup,file); }
            }
            foreach(string relative in new[]{"YikeSpeechWorker.exe","whisper-1.9.4-abi.json","whisper-runtime\\Release\\whisper.dll"}) {
                string file=Path.Combine(root,relative);byte[] original=File.ReadAllBytes(file);File.WriteAllText(file,"corrupt");
                try { Check(Reject("ValidateInstallation",root,false),"CorruptDependencyRejected: "+relative); }
                finally { File.WriteAllBytes(file,original); }
            }
            string foreign=Path.Combine(root,"foreign");Directory.CreateDirectory(foreign);File.WriteAllText(Path.Combine(foreign,"preserve"),"unchanged");
            Check(Reject("DeleteOwnedTemporary",foreign,root,"Yike-install-test-")&&File.Exists(Path.Combine(foreign,"preserve")),"NonOwnedDirectoryPreserved");
            Check(Reject("ExtractPayload",root),"CorruptEmbeddedPayloadRejected");
            string backupRoot=Path.Combine(root,"backup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backupRoot);
            File.WriteAllText(Path.Combine(backupRoot,"a-unlocked"),"backup");File.WriteAllText(Path.Combine(backupRoot,"z-locked"),"backup");
            using(var locked=new FileStream(Path.Combine(backupRoot,"z-locked"),FileMode.Open,FileAccess.Read,FileShare.None)) {
                Check(Reject("DeleteOwnedTemporary",backupRoot,root,"backup-")&&File.Exists(Path.Combine(backupRoot,"a-unlocked"))&&File.Exists(Path.Combine(backupRoot,"z-locked")),"LockedBackupPreservedWithoutPartialDeletion");
            }
            string link=Path.Combine(backupRoot,"linked-tree");
            using(var process=Process.Start(new ProcessStartInfo("cmd.exe","/c mklink /J \""+link+"\" \""+foreign+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})) {
                process.StandardOutput.ReadToEnd();process.StandardError.ReadToEnd();
                if(!process.WaitForExit(5000)||process.ExitCode!=0)throw new Exception("Owned junction fixture unavailable");
            }
            try { Check(Reject("DeleteOwnedTemporary",backupRoot,root,"backup-")&&File.Exists(Path.Combine(foreign,"preserve"))&&File.Exists(Path.Combine(backupRoot,"a-unlocked"))&&File.Exists(Path.Combine(backupRoot,"z-locked"))&&Directory.Exists(link),"LinkedBackupRejectedWithoutPartialDeletionOrTouchingTarget"); }
            finally {if(Directory.Exists(link))Directory.Delete(link,false);}
        } finally {
            // This test owns a fresh direct child of TEMP, never the installed app.
            if(Path.GetDirectoryName(root)!=Path.GetTempPath().TrimEnd('\\')||!Path.GetFileName(root).StartsWith("Yike-install-test-",StringComparison.Ordinal))throw new Exception("Unsafe test cleanup");
            Directory.Delete(root,true);
        }
        return failures.Count==0?0:1;
    }
}
