using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
namespace WindowsTranslator {
internal static class SpeechCaptureSessionTests {
 internal static void RunSpeechCaptureSessionTests(){
  var buffer=new PcmWindowBuffer();for(int i=0;i<10;i++){var samples=new float[16000];for(int j=0;j<samples.Length;j++)samples[j]=i/10f;buffer.Append(samples);}
  var latest=buffer.TakeLatestWindow();SpeechWorkerTests.Check(latest!=null&&latest.Length==128000&&latest[0]==0.2f&&latest[127999]==0.9f,"LiveWindowRetainsOnlyLatestEightSeconds");
  SpeechWorkerTests.Check(buffer.TakeLatestWindow()==null,"OnlyOneLiveWindowCanBePending");
  using(var directory=new CaptureDirectory()){
   string wave;using(var writer=new SessionWaveWriter(directory.Root)){writer.Append(new float[]{0.2f,-0.2f});wave=writer.Path;SpeechWorkerTests.Check(new FileInfo(wave).Length>=48,"AudioIsFlushedBeforeSessionEnds");}
   SpeechWorkerTests.Check(SessionWaveWriter.Recover(wave),"IncompleteOwnedWaveCanBeRecoveredAfterWriterExits");
   SpeechWorkerTests.Check(SpeechWorkerProgram.ReadOwnedWave(directory.Root,wave).Length==2,"RecoveredWaveHasExactFrameCount");
   byte[] bytes=File.ReadAllBytes(wave);File.WriteAllBytes(wave,new byte[10]);SpeechWorkerTests.Check(!SessionWaveWriter.Recover(wave),"CorruptWaveHeaderIsNotRepairedAsAudio");
   var truncated=new byte[bytes.Length-1];Array.Copy(bytes,truncated,truncated.Length);File.WriteAllBytes(wave,truncated);SpeechWorkerTests.Check(!SessionWaveWriter.Recover(wave),"CompletedTruncatedWaveIsRejected");File.Delete(wave);
   using(var writer=new SessionWaveWriter(directory.Root)){
    var chunk=new float[16000];for(int i=0;i<301;i++)writer.Append(chunk);wave=writer.Complete();SpeechWorkerTests.Check(SpeechWorkerProgram.ReadOwnedWave(directory.Root,wave).Length==4800000,"SessionWaveNeverExceedsFiveMinutes");
    bool closed=false;try{writer.Append(chunk);}catch(InvalidOperationException){closed=true;}SpeechWorkerTests.Check(closed,"ClosedWriterCannotAppendOldAudio");
   }
  }
  bool unowned=false;try{SessionWaveWriter.Recover(Path.Combine(Path.GetTempPath(),"other.wav"));}catch(InvalidDataException){unowned=true;}SpeechWorkerTests.Check(unowned,"WaveRecoveryCannotAccessUnownedPaths");
  var loader=NativeLibraryLoader.Open(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory));
  using(var converter=new SdlCaptureSession.PcmConverter(loader,48000,2,0x8120)){
   var source=new float[96000];for(int i=0;i<source.Length;i+=2){source[i]=0.5f;source[i+1]=-0.5f;}byte[] bytes=new byte[source.Length*4];Buffer.BlockCopy(source,0,bytes,0,bytes.Length);
   var mono=converter.Convert(bytes,true);SpeechWorkerTests.Check(mono.Length==16000,"RealSdlConvertsOneSecond48kStereoToExactly16kMono");foreach(float sample in mono)SpeechWorkerTests.Check(Math.Abs(sample)<0.001,"StereoDownmixPreservesOppositeChannelCancellation");
  }
  using(var converter=new SdlCaptureSession.PcmConverter(loader,44100,1,0x8120)){
   int received=0;for(int offset=0;offset<44100;){int count=Math.Min(1024,44100-offset);var bytes=new byte[count*4];received+=converter.Convert(bytes,false).Length;offset+=count;}received+=converter.Convert(new byte[0],true).Length;
   SpeechWorkerTests.Check(received==16000,"Fragmented44100ConversionPreservesExactDuration");
  }
  SpeechWorkerTests.Check(System.Runtime.InteropServices.Marshal.SizeOf(typeof(SdlCaptureSession.AudioSpec))==32&&System.Runtime.InteropServices.Marshal.OffsetOf(typeof(SdlCaptureSession.AudioSpec),"callback").ToInt32()==16,"SdlAudioSpecUsesPinnedWindows64BitLayout");
  using(var converter=new SdlCaptureSession.PcmConverter(loader,48000,1,0x8120)){
   var source=new float[48000];for(int i=45000;i<48000;i++)source[i]=0.25f;var bytes=new byte[source.Length*4];Buffer.BlockCopy(source,0,bytes,0,bytes.Length);var result=converter.Convert(bytes,true);
   double tail=0;for(int i=15100;i<15900;i++)tail+=result[i];SpeechWorkerTests.Check(result.Length==16000&&tail/800>0.2,"ResamplerFlushPreservesActualEndingNotZeroPadding");
  }
  var driver=new FakeDriver();using(var capture=new SdlCaptureSession(loader,driver)){
   SpeechWorkerTests.Check(driver.Opens==0,"CaptureConstructionNeverOpensMicrophone");var first=capture.Start("");SpeechWorkerTests.Check(first.DeviceName=="Microphone A"&&first.DeviceIndex==0,"FreshDefaultDeviceIsReported");capture.Stop();
   driver.DefaultName="Microphone B";var next=capture.Start("");SpeechWorkerTests.Check(next.DeviceName=="Microphone B"&&next.DeviceIndex==1&&driver.Enumerations==2,"DefaultDeviceChangesBetweenSessions");capture.Stop();
  }
  driver=new FakeDriver{Names=new[]{"Microphone A","MICROPHONE A"}};using(var capture=new SdlCaptureSession(loader,driver)){
   var endpoint=capture.Start("");SpeechWorkerTests.Check(endpoint.DeviceIndex==-1&&driver.Opens==1,"AmbiguousDefaultStillAllowsManualCaptureWithoutMeterIdentity");capture.Stop();
  }
  using(var directory=new CaptureDirectory()){
   driver=new FakeDriver();using(var capture=new SdlCaptureSession(loader,driver))using(var session=new SpeechWorkerSession(capture,(audio,language,token)=>new SpeechRecognitionCandidate("bonjour",0,"",null))){
    session.Start(directory.Id,"fr","",directory.Root);SpeechWorkerTests.Check(driver.Opens==1,"ExplicitFrenchDoesNotLoseExistingLanguageChoice");session.Cancel();
   }
  }
  using(var directory=new CaptureDirectory()){
   driver=new FakeDriver();using(var capture=new SdlCaptureSession(loader,driver))using(var session=new SpeechWorkerSession(capture,(samples,language,token)=>new SpeechRecognitionCandidate("draft",0,"",null))){
    var messages=new List<SpeechWorkerMessage>();using(var stopped=new ManualResetEventSlim()){
     session.EventReceived+=message=>{lock(messages)messages.Add(message);if(message.Kind=="stopped")stopped.Set();};session.Start(directory.Id,"zh","",directory.Root);
     driver.Push(new float[16000]);SpeechWorkerTests.Check(SpinWait.SpinUntil(()=>File.Exists(Path.Combine(directory.Root,"recording.wav"))&&new FileInfo(Path.Combine(directory.Root,"recording.wav")).Length>44,3000),"SessionWritesActualCapturedSamples");
     SpeechWorkerTests.Check(SpinWait.SpinUntil(()=>{lock(messages)return messages.Exists(m=>m.Kind=="draft");},3000),"LiveDraftIsDeliveredWithoutBlockingCapture");
     driver.Disconnect();SpeechWorkerTests.Check(stopped.Wait(3000),"DeviceDisconnectClosesCaptureAndReturnsOwnedWave");SpeechWorkerMessage result;lock(messages)result=messages.FindLast(m=>m.Kind=="stopped");
     SpeechWorkerTests.Check(result.Payload.failureCode=="capture_device_disconnected"&&File.Exists(result.Payload.audioPath)&&SpeechWorkerProgram.ReadOwnedWave(directory.Root,result.Payload.audioPath).Length>0,"DeviceDisconnectPreservesWaveAndCaptureFailure");
     long length=new FileInfo(result.Payload.audioPath).Length;driver.Push(new float[16000]);Thread.Sleep(50);SpeechWorkerTests.Check(new FileInfo(result.Payload.audioPath).Length==length,"ClosedCaptureCannotWriteLateCallbacks");
     SpeechWorkerTests.Check(result.Payload.text=="draft","DeviceDisconnectPreservesDraftAndWave");
    }
   }
  }
  using(var directory=new CaptureDirectory()){
   driver=new FakeDriver();using(var capture=new SdlCaptureSession(loader,driver))using(var session=new SpeechWorkerSession(capture,(samples,language,token)=>new SpeechRecognitionCandidate("",0,"",null)))using(var stopped=new ManualResetEventSlim()){
    SpeechWorkerMessage result=null;session.EventReceived+=m=>{if(m.Kind=="stopped"){result=m;stopped.Set();}};session.Start(directory.Id,"en","",directory.Root);
    for(int i=0;i<300;i++){driver.Push(new float[16000]);int expected=(i+1)*16000;SpeechWorkerTests.Check(SpinWait.SpinUntil(()=>session.CapturedSamples>=expected,3000),"CaptureQueueDrainsBoundedFrames");}
    SpeechWorkerTests.Check(stopped.Wait(3000)&&result.Payload.stopReason=="duration-limit"&&result.Payload.stopReason!="silence","MaximumDurationDoesNotSignalSilence");
    SpeechWorkerTests.Check(SpeechWorkerProgram.ReadOwnedWave(directory.Root,result.Payload.audioPath).Length==4800000,"DurationLimitClosesExactFiveMinuteWave");
   }
  }
  SpeechWorkerTests.Check(SpeechWorkerSession.Merge("hello world","world again")=="hello world again","LiveWindowsMergeOverlappingWords");
  using(var directory=new CaptureDirectory()){
   var lengths=new List<int>();driver=new FakeDriver();using(var capture=new SdlCaptureSession(loader,driver))using(var session=new SpeechWorkerSession(capture,(audio,language,token)=>{lock(lengths)lengths.Add(audio.Length);return new SpeechRecognitionCandidate("hello world",0,"",null);})){
    session.Start(directory.Id,"en","",directory.Root);for(int i=0;i<8;i++){driver.Push(new float[16000]);int expected=(i+1)*16000;SpeechWorkerTests.Check(SpinWait.SpinUntil(()=>session.CapturedSamples==expected,3000),"OverlapTestReceivesInput");}
    session.Preview();SpeechWorkerTests.Check(session.Completion.Wait(3000),"FullLiveWindowIsDecoded");driver.Push(new float[16000]);SpeechWorkerTests.Check(SpinWait.SpinUntil(()=>session.CapturedSamples==144000,3000),"NextLiveStepReceivesInput");session.Preview();SpeechWorkerTests.Check(session.Completion.Wait(3000),"NextLiveStepIsDecoded");
    lock(lengths)SpeechWorkerTests.Check(lengths.Count==2&&lengths[0]==128000&&lengths[1]==24000,"CompletedLiveWindowKeepsExactlyHalfSecondOverlap");session.Cancel();
   }
  }
  using(var directory=new CaptureDirectory())using(var entered=new ManualResetEventSlim()){
   driver=new FakeDriver();using(var capture=new SdlCaptureSession(loader,driver))using(var session=new SpeechWorkerSession(capture,(samples,language,token)=>{entered.Set();token.WaitHandle.WaitOne();token.ThrowIfCancellationRequested();return new SpeechRecognitionCandidate("old",0,"",null);})){ 
    int drafts=0;session.EventReceived+=m=>{if(m.Kind=="draft")Interlocked.Increment(ref drafts);};session.Start(directory.Id,"auto","",directory.Root);driver.Push(new float[16000]);SpeechWorkerTests.Check(entered.Wait(3000),"CancellationTestHasInFlightDraft");session.Cancel();SpeechWorkerTests.Check(session.Completion.Wait(3000),"CancelAbortsInFlightDraftBeforeCleanup");
    string path=Path.Combine(directory.Root,"recording.wav");long length=new FileInfo(path).Length;driver.Push(new float[16000]);SpeechWorkerTests.Check(new FileInfo(path).Length==length&&drafts==0&&!session.IsCapturing,"CancelClosesWaveAndSuppressesOldDraft");
    using(var next=new CaptureDirectory()){session.Start(next.Id,"en","",next.Root);session.Stop("manual");SpeechWorkerTests.Check(session.LastStopped.text=="","NewSessionNeverInheritsPreviousDraft");}
   }
  }
  TestHostCaptureLifecycle();
  Console.WriteLine("PASS capture contracts: native PCM conversion, bounded latest window, owned crash-recoverable WAV, fresh endpoints and disconnect cleanup");
 }
 private static void TestHostCaptureLifecycle(){
  var driver=new FakeDriver();Guid instance=Guid.NewGuid();using(var output=new MemoryStream())using(var directory=new CaptureDirectory())using(var host=new SpeechWorkerProgram.HostState(output,instance,null,loader=>new SdlCaptureSession(loader,driver))){
   var preheat=Command("preheat",instance,SpeechWorkerProtocol.ControlSession);host.Dispatch(preheat);
   SpeechWorkerTests.Check(driver.Opens==0&&driver.Enumerations==0,"PreheatNeverOpensMicrophone");
   var start=Command("start",instance,directory.Id);start.Payload=new SpeechWorkerPayload{language="zh",audioPath=directory.Root};host.Dispatch(start);driver.Push(new float[16000]);
   SpeechWorkerTests.Check(SpinWait.SpinUntil(()=>new FileInfo(Path.Combine(directory.Root,"recording.wav")).Length>44,3000),"HostCapturesRegisteredSessionWave");
   var stop=Command("stop",instance,directory.Id);stop.Payload.stopReason="manual";host.Dispatch(stop);
   var repeated=Command("stop",instance,directory.Id);host.Dispatch(repeated);
   var decode=Command("decode",instance,Guid.NewGuid());decode.Payload=new SpeechWorkerPayload{language="zh",audioPath=Path.Combine(directory.Root,"recording.wav"),timeoutMilliseconds=30000};host.Dispatch(decode);
   output.Position=0;var first=SpeechWorkerProtocol.ReadAsync(output,1048576,CancellationToken.None).GetAwaiter().GetResult();SpeechWorkerTests.Check(first.Kind=="ready"&&first.Payload.contextInitializationCount==1,"HostPreheatIsActualContextReady");
   var ready=SpeechWorkerProtocol.ReadAsync(output,1048576,CancellationToken.None).GetAwaiter().GetResult();SpeechWorkerTests.Check(ready.Kind=="capture_started"&&ready.SessionId==directory.Id&&ready.RequestId==start.RequestId,"HostCaptureRepliesUseOriginalEnvelope");
   var stopped=SpeechWorkerProtocol.ReadAsync(output,1048576,CancellationToken.None).GetAwaiter().GetResult();SpeechWorkerTests.Check(stopped.Kind=="stopped"&&stopped.RequestId==stop.RequestId&&SpeechWorkerProgram.ReadOwnedWave(directory.Root,stopped.Payload.audioPath).Length==16000,"HostStopReturnsClosedExactWave");
   var again=SpeechWorkerProtocol.ReadAsync(output,1048576,CancellationToken.None).GetAwaiter().GetResult();SpeechWorkerTests.Check(again.Kind=="stopped"&&again.RequestId==repeated.RequestId,"RepeatedStopReturnsAlreadyClosedWave");
   var failed=SpeechWorkerProtocol.ReadAsync(output,1048576,CancellationToken.None).GetAwaiter().GetResult();SpeechWorkerTests.Check(failed.Kind=="failure"&&failed.Payload.failureCode=="worker_audio_unowned","HostRejectsWaveFromDifferentSession");
   output.Position=output.Length;var enhanced=Command("decode",instance,directory.Id);enhanced.Payload=new SpeechWorkerPayload{language="zh",audioPath=Path.Combine(directory.Root,"recording.enhanced.wav"),timeoutMilliseconds=0};long before=output.Length;host.Dispatch(enhanced);output.Position=before;
   var allowed=SpeechWorkerProtocol.ReadAsync(output,1048576,CancellationToken.None).GetAwaiter().GetResult();SpeechWorkerTests.Check(allowed.Payload.failureCode=="worker_decode_arguments","HostAllowsApprovedEnhancedSiblingToReachArgumentValidation");
  }
 }
 private static SpeechWorkerMessage Command(string kind,Guid instance,Guid session){return new SpeechWorkerMessage{Version=1,InstanceId=instance,SessionId=session,RequestId=Guid.NewGuid(),Kind=kind,Payload=new SpeechWorkerPayload()};}
 private sealed class CaptureDirectory:IDisposable {
  internal readonly Guid Id=Guid.NewGuid();internal readonly string Root;internal CaptureDirectory(){Root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+Id.ToString("N"));Directory.CreateDirectory(Root);}public void Dispose(){Directory.Delete(Root,true);}
 }
 private sealed class FakeDriver:SdlCaptureSession.ICaptureDriver {
  internal int Opens,Enumerations;internal string DefaultName="Microphone A";internal string[] Names=new[]{"Microphone A","Microphone B"};private Action<byte[]> callback;private bool connected;
  public string[] Enumerate(){Enumerations++;return Names;}public string GetDefaultName(){return DefaultName;}
  public SdlCaptureSession.AudioSpec Open(string name,Action<byte[]> callback){Opens++;this.callback=callback;connected=true;return new SdlCaptureSession.AudioSpec{freq=16000,channels=1,format=0x8120};}
  public void Resume(){}public bool IsConnected{get{return connected;}}public void Close(){connected=false;callback=null;}
  internal void Push(float[] samples){var cb=callback;if(cb==null)return;var bytes=new byte[samples.Length*4];Buffer.BlockCopy(samples,0,bytes,0,bytes.Length);cb(bytes);}internal void Disconnect(){connected=false;}
 }
}
}
