using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsTranslator {
internal static class SpeechCaptureDevice {
	private static string Normalize(string value) {return Regex.Replace((value??"").Normalize(NormalizationForm.FormKC).Trim(),@"\s+"," ").ToLowerInvariant();}
	internal static int? ResolveCaptureIndex(string endpointName,string[] captureNames) {
		string name=Normalize(endpointName);if(name.Length==0||captureNames==null)return null;int? found=null;
		for(int i=0;i<captureNames.Length;i++)if(Normalize(captureNames[i])==name){if(found.HasValue)return null;found=i;}return found;
	}
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init(uint flags);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Quit(uint flags);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(int capture);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Name(int index,int capture);
	[DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr LoadLibrary(string path);
	[DllImport("kernel32.dll",CharSet=CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module,string name);
	[DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
	private static T Function<T>(IntPtr dll,string name){return(T)(object)Marshal.GetDelegateForFunctionPointer(GetProcAddress(dll,name),typeof(T));}
	internal static string[] EnumerateCaptureNames(){
		IntPtr dll=LoadLibrary(Path.Combine(WhisperSpeechInput.RuntimeRoot,"Release","SDL2.dll"));if(dll==IntPtr.Zero)return new string[0];bool initialized=false;Quit quit=null;
		try {Init init=Function<Init>(dll,"SDL_InitSubSystem");quit=Function<Quit>(dll,"SDL_QuitSubSystem");if(init(0x10)!=0)return new string[0];initialized=true;
			Count count=Function<Count>(dll,"SDL_GetNumAudioDevices");Name name=Function<Name>(dll,"SDL_GetAudioDeviceName");List<string> result=new List<string>();
			for(int i=0,n=count(1);i<n;i++){IntPtr pointer=name(i,1);List<byte> bytes=new List<byte>();if(pointer!=IntPtr.Zero)for(int j=0;j<4096&&Marshal.ReadByte(pointer,j)!=0;j++)bytes.Add(Marshal.ReadByte(pointer,j));result.Add(Encoding.UTF8.GetString(bytes.ToArray()));}return result.ToArray();
		}catch{return new string[0];}finally{if(initialized)quit(0x10);FreeLibrary(dll);}
	}
}
}
