using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsTranslator {
public static partial class Tests {
	internal static void SpeechVadSmoke(string speech,string output){
		string silence=Path.Combine(Path.GetTempPath(),"Yike-refine-silence-"+Guid.NewGuid().ToString("N")+".wav");
		try{WriteTestWave(silence,1,i=>0);var service=new SpeechRefinement();var spoken=service.RunAsync(speech,"en","",CancellationToken.None).GetAwaiter().GetResult();var quiet=service.RunAsync(silence,"en","",CancellationToken.None).GetAwaiter().GetResult();Check(spoken.UsedVad&&!string.IsNullOrWhiteSpace(spoken.Text),"Real VAD speech smoke failed");Check(quiet.FellBack,"Real silent VAD did not safely fall back");File.WriteAllText(output,"PASS: real VAD speech output nonempty; silence safely falls back; owned audio removed.");}finally{File.Delete(silence);}
	}
	internal static void RunSpeechRefinementTests(List<string> lines){
		var config=new WhisperVadConfiguration();
		Check(config.AppendArguments("base").EndsWith(" --vad -vm \""+config.ModelPath+"\" -vt 0.50 -vspd 250 -vsd 500 -vp 150 -vo 0.10"),"VAD padding/segmentation arguments wrong");
		string path=Path.Combine(Path.GetTempPath(),"Yike-vad-test-"+Guid.NewGuid().ToString("N"));File.WriteAllText(path,"not a model");
		try {Check(!new WhisperVadConfiguration(path).IsModelValid(),"Corrupt VAD model accepted");}finally{File.Delete(path);}
		Check(!new WhisperVadConfiguration(path).IsModelValid(),"Missing VAD model accepted");
		Check(config.IsModelValid(),"Bundled VAD model/hash unavailable for smoke tests");
		string wave=Path.Combine(Path.GetTempPath(),"Yike-refine-test-"+Guid.NewGuid().ToString("N")+".wav");WriteTestWave(wave,1,i=>0);
		try {
			int calls=0;
			var refinement=new SpeechRefinement((info,ct)=>{calls++;return Task.FromResult(new SpeechRefinement.CliOutput(calls==1?1:0,calls==1?"":"final words",""));});
			var result=refinement.RunAsync(wave,"en","live draft",CancellationToken.None).GetAwaiter().GetResult();
			Check(result.Text=="final words"&&result.FellBack&&calls==2,"VAD crash did not use exactly one full-audio retry");
			calls=0;refinement=new SpeechRefinement((info,ct)=>{calls++;return Task.FromResult(new SpeechRefinement.CliOutput(0,"",""));});
			result=refinement.RunAsync(wave,"auto","live draft",CancellationToken.None).GetAwaiter().GetResult();
			Check(result.Text=="live draft"&&result.FellBack&&calls==2,"Empty VAD/fallback erased draft");
			using(var cancel=new CancellationTokenSource()){
				calls=0;refinement=new SpeechRefinement((info,ct)=>{calls++;cancel.Cancel();return Task.FromResult(new SpeechRefinement.CliOutput(1,"","cancelled"));});
				bool cancelled=false;try{refinement.RunAsync(wave,"en","draft",cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
				Check(cancelled&&calls==1,"CancelledVadDoesNotSpawnRetry");
			}
			refinement=new SpeechRefinement((info,ct)=>Task.FromResult(new SpeechRefinement.CliOutput(-1,"","timeout")));
			result=refinement.RunAsync(wave,"en","draft",CancellationToken.None).GetAwaiter().GetResult();Check(result.Text=="draft"&&result.FailureCode=="timeout","TimedOutRefinementKeepsDraft");
		}finally{File.Delete(wave);}
		lines.Add("PASS speech refinement: verified VAD, bounded fallback, cancellation, timeout and live-draft preservation");
	}
}
}
