using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsTranslator {
internal sealed class SpeechRefinement {
	internal sealed class CliOutput {
		internal int ExitCode {get;private set;}
		internal string Text {get;private set;}
		internal string FailureCode {get;private set;}
		internal CliOutput(int code,string text,string failure){ExitCode=code;Text=text??"";FailureCode=failure??"";}
	}
	private readonly ISpeechFinalDecoder decoder;
	private readonly WhisperVadConfiguration vad;
	internal SpeechRefinement():this(null){}
	internal SpeechRefinement(Func<ProcessStartInfo,CancellationToken,Task<CliOutput>> runner):this(runner,new WhisperVadConfiguration()){}
	internal SpeechRefinement(Func<ProcessStartInfo,CancellationToken,Task<CliOutput>> runner,WhisperVadConfiguration config){decoder=new CliSpeechDecoder(runner,config);vad=config;}
	internal static string NativePath(string path,string directory){return Uri.UnescapeDataString(new Uri(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString()).Replace('/',Path.DirectorySeparatorChar);}
	internal Task<SpeechRefinementResult> RunAsync(string audioPath,string language,string liveText,CancellationToken token){
		var clock=Stopwatch.StartNew();var budget=new SpeechDecodeBudget(AudioDuration(audioPath),()=>clock.ElapsedMilliseconds);
		return new SpeechFinalizer(false,vad).RunAsync(audioPath,language,liveText,budget,decoder,token);
	}
	internal static TimeSpan AudioDuration(string audioPath){
		try{
			using(var stream=File.OpenRead(audioPath))using(var reader=new BinaryReader(stream,Encoding.ASCII)){
				if(stream.Length<44||Encoding.ASCII.GetString(reader.ReadBytes(4))!="RIFF")return TimeSpan.Zero;
				reader.ReadUInt32();if(Encoding.ASCII.GetString(reader.ReadBytes(4))!="WAVE")return TimeSpan.Zero;
				uint byteRate=0;
				while(stream.Position+8<=stream.Length){
					string name=Encoding.ASCII.GetString(reader.ReadBytes(4));uint size=reader.ReadUInt32();long next=stream.Position+size+(size&1);
					if(next>stream.Length)return TimeSpan.Zero;
					if(name=="fmt "&&size>=16){reader.ReadUInt16();reader.ReadUInt16();reader.ReadUInt32();byteRate=reader.ReadUInt32();}
					if(name=="data"&&byteRate>0)return TimeSpan.FromSeconds((double)size/byteRate);
					stream.Position=next;
				}
			}
		}catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
		return TimeSpan.Zero;
	}
}
}
