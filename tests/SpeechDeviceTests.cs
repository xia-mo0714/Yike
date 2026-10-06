using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace WindowsTranslator {
public static partial class Tests {
	internal static void RunSpeechDeviceTests(List<string> lines) {
		string lockedDirectory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Yike-voice-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(lockedDirectory);
		string lockedWave=System.IO.Path.Combine(lockedDirectory,"locked.wav");System.IO.File.WriteAllText(lockedWave,"owned test fixture");
		var cleanupInput=new WhisperSpeechInput(()=>new ProcessStartInfo(),()=>0);
		var directoryField=typeof(WhisperSpeechInput).GetField("recordingDirectory",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
		var cleanupMethod=typeof(WhisperSpeechInput).GetMethod("CleanupRecordingDirectory",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);directoryField.SetValue(cleanupInput,lockedDirectory);
		try{using(var locked=System.IO.File.Open(lockedWave,System.IO.FileMode.Open,System.IO.FileAccess.ReadWrite,System.IO.FileShare.None)){
			cleanupMethod.Invoke(cleanupInput,null);Check((string)directoryField.GetValue(cleanupInput)==lockedDirectory,"CancelledRefinementRetainsCleanupPathWhileWaveLocked");
		}cleanupMethod.Invoke(cleanupInput,null);Check(!System.IO.Directory.Exists(lockedDirectory)&&directoryField.GetValue(cleanupInput)==null,"RefinementCompletionRetriesOwnedAudioCleanup");}
		finally{cleanupInput.Dispose();if(System.IO.Directory.Exists(lockedDirectory))System.IO.Directory.Delete(lockedDirectory,true);}
		Check(SpeechCaptureDevice.ResolveCaptureIndex(" Microphone (USB) ",new[]{"Other","microphone (usb)"})==1,"DeviceNamesMatchUniquely");
		Check(!SpeechCaptureDevice.ResolveCaptureIndex("Mic",new[]{"Mic","MIC"}).HasValue,"Ambiguous capture chosen arbitrarily");
		Check(!SpeechCaptureDevice.ResolveCaptureIndex("Mic",new[]{"Different mic"}).HasValue,"Mismatching capture chosen");
		Check(!SpeechCaptureDevice.ResolveCaptureIndex("",new[]{""}).HasValue,"Missing endpoint identity accepted");
		List<FakeSpeechBackend> backends=new List<FakeSpeechBackend>();
		using(SpeechInput facade=new SpeechInput(lang=>{var b=new FakeSpeechBackend();backends.Add(b);return b;})) {
			int calls=0; facade.ActivityChanged += s=>calls++;
			facade.Start(); backends[0].EmitActivity(new AdaptiveSpeechDetector().Observe(0,0));
			facade.Cancel(); facade.Start(); backends[0].EmitActivity(new AdaptiveSpeechDetector().Observe(.1,120));
			backends[1].EmitActivity(new AdaptiveSpeechDetector().MarkUnavailable(0));
			Check(calls==2,"StaleActivityIsIgnored");
		}
		Check(typeof(MicrophoneLevel).GetMethod("ReadPeak")!=null,"Floating microphone peak missing");
		string producer="[Console]::OutputEncoding=[Text.Encoding]::UTF8; [Console]::WriteLine('[Start speaking]'); [Console]::Out.Flush(); Start-Sleep -Milliseconds 250; [Console]::Write('保留'); [Console]::Out.Flush(); Start-Sleep -Milliseconds 700; [Console]::WriteLine(' 结果'); [Console]::Out.Flush(); Start-Sleep -Seconds 30";
		Func<ProcessStartInfo> helper=()=>new ProcessStartInfo {FileName=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),Arguments="-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(producer)),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
		List<string> finals=new List<string>();int auto=0;bool unavailable=false;
		Stopwatch stopClock=Stopwatch.StartNew();long lastSpeechAt=0,stoppedAt=0;
		Func<ProcessStartInfo> delayedHelper=()=>{var info=helper();info.Arguments="-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes("Start-Sleep -Milliseconds 1200; "+producer));return info;};
		Stopwatch voiceClock=null;
		using(var timed=new WhisperSpeechInput(delayedHelper,()=>{if(voiceClock==null)voiceClock=Stopwatch.StartNew();return voiceClock.ElapsedMilliseconds<1000?.05:0.0;},true,"Pinned mic")){
			timed.ActivityChanged+=s=>{if(s.Peak>.01)lastSpeechAt=stopClock.ElapsedMilliseconds;};timed.AutoStopped+=()=>stoppedAt=stopClock.ElapsedMilliseconds;
			string timedError;Check(timed.Start(out timedError)&&SpinWait.SpinUntil(()=>stoppedAt>0,10000),"SixSecondStopIntegrationDidNotStop");
			Check(stoppedAt-lastSpeechAt>=6000&&stoppedAt-lastSpeechAt<=6600,"SixSecondStopIntegrationOutside6To6Point6Seconds");timed.Dispose();Check(timed.Completion.Wait(3000),"Timed helper leaked");
		}
		using(var input=new WhisperSpeechInput(helper,()=>{throw new InvalidOperationException("meter failure");},true,"Pinned mic")){
			input.ActivityChanged+=s=>unavailable=s.State==SpeechActivityState.Unavailable;
			input.Recognized+=s=>{lock(finals)finals.Add(s);};input.AutoStopped+=()=>auto++;
			string error;Check(input.Start(out error),"Broken meter should not block helper startup");
			Check(SpinWait.SpinUntil(()=>unavailable,3000)&&input.IsListening,"Meter failure aborted live recognition");
			Thread.Sleep(400);Check(input.Stop()&&input.Completion.Wait(4000),"Broken meter manual drain failed");
			lock(finals)Check(finals.Count(s=>s=="保留 结果")==1 && auto==0,"BrokenMeterStillAllowsFinalText");
		}
		using(var mismatch=new WhisperSpeechInput(helper,()=>.5,false,"Pinned mic")){
			bool disabled=false;mismatch.ActivityChanged+=s=>disabled=s.State==SpeechActivityState.Unavailable;
			string error;Check(mismatch.Start(out error)&&SpinWait.SpinUntil(()=>disabled,3000)&&mismatch.IsListening,"DeviceMismatchDisablesAutoStop");mismatch.Dispose();Check(mismatch.Completion.Wait(3000),"Mismatch cancellation leaked helper");
		}
		for(int i=0;i<30;i++)using(var input=new WhisperSpeechInput(helper,()=>0.0,true,"Pinned mic")){
			string error;Check(input.Start(out error),"Soak helper startup failed");input.Dispose();Check(input.Completion.Wait(3000)&&!input.IsListening,"30-cycle helper cleanup failed");
		}
		lines.Add("PASS speech device: unique endpoint/capture alignment and generation-filtered activity");
	}
}
}
