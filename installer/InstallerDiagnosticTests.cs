using System;
using System.IO;
using System.Reflection;

internal static class InstallerDiagnosticTests {
	private static void Main(){
		MethodInfo method=typeof(Setup).GetMethod("ValidateDiagnosticRoot",BindingFlags.NonPublic|BindingFlags.Static);
		if(method==null)throw new Exception("Diagnostic install path guard is missing");
		string valid=Path.Combine(Path.GetTempPath(),"Yike-install-test-"+Guid.NewGuid().ToString("N"));
		foreach(string invalid in new[]{Path.GetPathRoot(valid),Path.Combine(Path.GetTempPath(),"Yike"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Yike"),Path.Combine(valid,"child")}){
			bool rejected=false;try{method.Invoke(null,new object[]{invalid});}catch(TargetInvocationException){rejected=true;}if(!rejected)throw new Exception("Unsafe diagnostic root accepted");
		}
		method.Invoke(null,new object[]{valid});Directory.CreateDirectory(valid);
		try{bool rejected=false;try{method.Invoke(null,new object[]{valid});}catch(TargetInvocationException){rejected=true;}if(!rejected)throw new Exception("Existing diagnostic directory accepted");}finally{Directory.Delete(valid);}
		Console.WriteLine("PASS: isolated diagnostic install roots only; existing paths and installed application rejected.");
	}
}
