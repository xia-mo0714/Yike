using System;
using System.IO;
using System.Security.Cryptography;

namespace WindowsTranslator {
internal sealed class WhisperVadConfiguration {
	internal const string Sha256="2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987";
	internal string ModelPath {get;private set;}
	internal WhisperVadConfiguration():this(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory)){}
	internal WhisperVadConfiguration(SpeechRuntimePaths paths):this(paths.VadPath){}
	internal WhisperVadConfiguration(string path){ModelPath=path;}
	internal bool IsModelValid(){try{using(SHA256 sha=SHA256.Create())using(FileStream file=File.OpenRead(ModelPath))return string.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-",""),Sha256,StringComparison.OrdinalIgnoreCase);}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}}
	internal string AppendArguments(string arguments){return arguments+" --vad -vm \""+ModelPath.Replace("\"","\\\"")+"\" -vt 0.50 -vspd 250 -vsd 500 -vp 150 -vo 0.10";}
}
}
