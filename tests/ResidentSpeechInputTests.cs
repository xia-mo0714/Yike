using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
public static partial class Tests {
 private static void RunResidentSpeechInputTests(List<string> lines){
  TestStoppedBeforeStartContinuation();TestResidentTiming();TestResidentAmbiguousMeter();TestResidentDecoderBudget();TestResidentCrash();TestResidentCancellation();TestResidentQualityPolicy();TestResidentImmediateCleanup();TestUnconfirmedWorkerNeverStartsCli();TestResidentRealSampling();
  lines.Add("PASS resident input: ready-based silence timing, manual/quality policy, generation invalidation and bounded crash fallback");
 }
 private sealed class ResidentMeter:ISpeechCaptureMeter {
  internal double Peak;internal string[] Names=new[]{"Microphone A"};internal int Releases;
  public string DeviceName{get{return "Microphone A";}}public string[] CaptureNames{get{return Names;}}
  public double ReadPeak(){return Peak;}public bool DefaultDeviceChanged(){return false;}public void Dispose(){Releases++;}
 }
 private sealed class ResidentWorker:ISpeechWorkerProcess {
  internal bool Exited,HoldStart,CrashDecode,HoldDecode,NoAudio,RefuseExit;internal Guid Instance;internal string Root;internal int Decodes,Starts,Stops;
  internal string StopReason,StopFailure="",Draft="",Final="valid final";internal double[] Probabilities;
  internal TaskCompletionSource<SpeechWorkerMessage> StartReply=new TaskCompletionSource<SpeechWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously),DecodeReply=new TaskCompletionSource<SpeechWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
  internal SpeechWorkerMessage StartCommand;
  public event Action<SpeechWorkerMessage> EventReceived;public bool HasExited{get{return Exited;}}
  public void Start(string path,Guid instance){Instance=instance;}
  private SpeechWorkerMessage Reply(SpeechWorkerMessage c,string kind,SpeechWorkerPayload p){return new SpeechWorkerMessage{Version=1,InstanceId=Instance,SessionId=c.SessionId,RequestId=c.RequestId,Kind=kind,Payload=p};}
  public Task<SpeechWorkerMessage> SendAsync(SpeechWorkerMessage c,CancellationToken token){
   token.ThrowIfCancellationRequested();if(c.Kind=="hello"||c.Kind=="preheat")return Task.FromResult(Reply(c,"ready",new SpeechWorkerPayload{contextInitializationCount=c.Kind=="hello"?0:1,loadMilliseconds=10}));
   if(c.Kind=="start"){Starts++;Root=c.Payload.audioPath;StartCommand=c;if(!NoAudio)WriteResidentWave(Path.Combine(Root,"recording.wav"));if(HoldStart)return StartReply.Task;return Task.FromResult(Reply(c,"capture_started",new SpeechWorkerPayload{deviceName="Microphone A",deviceIndex=0,captureReadyMilliseconds=5}));}
   if(c.Kind=="stop"){Stops++;StopReason=c.Payload.stopReason;return Task.FromResult(Reply(c,"stopped",new SpeechWorkerPayload{audioPath=Path.Combine(Root,"recording.wav"),text=Draft,stopReason=StopReason,failureCode=StopFailure}));}
   if(c.Kind=="decode"){Decodes++;if(HoldDecode)return DecodeReply.Task;if(CrashDecode){Exited=true;throw new IOException("owned worker crashed");}return Task.FromResult(Reply(c,"final",new SpeechWorkerPayload{text=Final,probabilities=Probabilities}));}
   return Task.FromResult(Reply(c,"cancelled",new SpeechWorkerPayload()));
  }
  internal void Ready(){StartReply.TrySetResult(Reply(StartCommand,"capture_started",new SpeechWorkerPayload{deviceName="Microphone A",deviceIndex=0,captureReadyMilliseconds=5}));}
  internal void Emit(string kind,string text="",string failure="",string reason=""){var h=EventReceived;if(h!=null)h(Reply(StartCommand,kind,new SpeechWorkerPayload{text=text,failureCode=failure,stopReason=reason,audioPath=Path.Combine(Root,"recording.wav")}));}
  public void TerminateOwnedJob(){if(!RefuseExit)Exited=true;}public void Dispose(){if(!RefuseExit)Exited=true;}
 }
 private sealed class ResidentCli:ISpeechFinalDecoder {
  internal int Calls;internal ResidentWorker Worker;internal string Text="cli final";
  public Task<SpeechRecognitionCandidate> DecodeAsync(SpeechDecodeRequest r,CancellationToken t){t.ThrowIfCancellationRequested();Check(Worker==null||Worker.HasExited,"CliNeverRunsAlongsideOwnedNativeWorker");Calls++;return Task.FromResult(new SpeechRecognitionCandidate(Text,0,"",null));}
 }
 private static void WriteResidentWave(string path){using(var f=File.Create(path))using(var w=new BinaryWriter(f)){w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));w.Write(36+32000);w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(16000);w.Write(32000);w.Write((short)2);w.Write((short)16);w.Write(System.Text.Encoding.ASCII.GetBytes("data"));w.Write(32000);for(int i=0;i<16000;i++)w.Write((short)(Math.Sin(i*.1)*1000));}}
 private static void WaitResident(Func<bool> condition,string name){Check(SpinWait.SpinUntil(condition,3000),name);}
 private sealed class EarlyStoppedTransportWorker:ISpeechWorkerProcess {
  private ProtocolTestPipe input=new ProtocolTestPipe(),output=new ProtocolTestPipe(),error=new ProtocolTestPipe();private SpeechWorkerTransport transport;private Guid instance;internal SpeechWorkerMessage StartCommand;
  internal readonly TaskCompletionSource<int> StartAcknowledged=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),ContinueStart=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
  public event Action<SpeechWorkerMessage> EventReceived;public bool HasExited{get;private set;}
  public void Start(string path,Guid id){instance=id;transport=new SpeechWorkerTransport(input,output,error,id);transport.EventReceived+=m=>{var h=EventReceived;if(h!=null)h(m);};}
  public Task<SpeechWorkerMessage> SendAsync(SpeechWorkerMessage command,CancellationToken token){
   var pending=transport.SendAsync(command,token);SpeechWorkerProtocol.ReadAsync(output,65536,CancellationToken.None).GetAwaiter().GetResult();
   string kind=command.Kind=="start"?"capture_started":command.Kind=="hello"||command.Kind=="preheat"?"ready":"cancelled";
   var reply=ProtocolMessage(kind,instance,command.SessionId,command.RequestId);reply.Payload=new SpeechWorkerPayload{contextInitializationCount=command.Kind=="hello"?0:1,deviceName="Microphone A",deviceIndex=0};
   if(command.Kind=="start")StartCommand=command;SpeechWorkerProtocol.WriteAsync(input,reply,1048576,CancellationToken.None).GetAwaiter().GetResult();
   return command.Kind=="start"?HoldStartContinuation(pending):pending;
  }
  private async Task<SpeechWorkerMessage> HoldStartContinuation(Task<SpeechWorkerMessage> pending){var reply=await pending.ConfigureAwait(false);StartAcknowledged.TrySetResult(0);await ContinueStart.Task.ConfigureAwait(false);return reply;}
  internal void StopImmediately(){var message=ProtocolMessage("stopped",instance,StartCommand.SessionId,StartCommand.RequestId);message.Payload=new SpeechWorkerPayload{text="saved early draft",failureCode="capture_device_disconnected",stopReason="capture-failure"};SpeechWorkerProtocol.WriteAsync(input,message,1048576,CancellationToken.None).GetAwaiter().GetResult();}
  public void TerminateOwnedJob(){HasExited=true;ContinueStart.TrySetResult(0);if(transport!=null)transport.Dispose();}public void Dispose(){TerminateOwnedJob();input.Dispose();output.Dispose();error.Dispose();}
 }
 private static void TestStoppedBeforeStartContinuation(){
  foreach(bool ambiguous in new[]{false,true}){var worker=new EarlyStoppedTransportWorker();var meter=new ResidentMeter{Names=ambiguous?new[]{"Microphone A","MICROPHONE A"}:new[]{"Microphone A"}};
   using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>0,()=>worker))using(var backend=new ResidentSpeechInput(manager,()=>meter,()=>0,new ResidentCli(),()=>new FakeSpeechBackend())){
    int finals=0,auto=0;string text="",error;backend.Recognized+=s=>{text=s;finals++;};backend.AutoStopped+=()=>auto++;
    Check(backend.Start("en",out error)&&worker.StartAcknowledged.Task.Wait(3000),"EarlyStopTransportAcknowledgesCaptureBeforeBarrier");worker.StopImmediately();
    WaitResident(()=>!backend.IsListening,"TerminalStopIsRetainedBeforeStartContinuation");worker.ContinueStart.SetResult(0);
    Check(backend.Completion.Wait(3000)&&text=="saved early draft"&&finals==1&&auto==0,"EarlyStopCompletesOnceRetainingDraftWithoutAutoSend");
    Check(backend.Status.CaptureFailureCode=="capture_device_disconnected","EarlyStopRetainsDeviceFailure");
    Check(!(bool)typeof(SpeechWorkerManager).GetField("capturing",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(manager),"LateStartCannotRestoreManagerCapture");
   }
  }
 }
 private static void TestResidentTiming(){
  long now=0;var worker=new ResidentWorker{HoldStart=true};var meter=new ResidentMeter();
  using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker))using(var input=new ResidentSpeechInput(manager,()=>meter,()=>now,new ResidentCli(),()=>new FakeSpeechBackend())){
   int auto=0,finals=0;input.AutoStopped+=()=>auto++;input.Recognized+=s=>finals++;string error;Check(input.Start("zh",out error),"ResidentStartIsNonBlockingWhileModelLoads");WaitResident(()=>worker.StartCommand!=null,"ResidentStartsOwnedCapture");now=10000;input.Sample();Check(input.IsListening&&worker.Stops==0,"EightSecondTimerDoesNotStartBeforeCaptureStarted");worker.Ready();WaitResident(()=>input.DeviceName=="Microphone A","ResidentReceivesActualCaptureReady");
   meter.Peak=.05;input.Sample();now=10120;input.Sample();meter.Peak=0;now=16119;input.Sample();Check(worker.Stops==0,"ResidentDoesNotStopBeforeSixSeconds");now=16120;input.Sample();Check(input.Completion.Wait(3000)&&auto==1&&finals==1&&worker.Stops==1,"ResidentStopsAtSixSecondsFromLastConfirmedVoice");Check(!worker.HasExited,"NormalStopKeepsWarmModel");
  }
  now=0;worker=new ResidentWorker();meter=new ResidentMeter();using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker))using(var input=new ResidentSpeechInput(manager,()=>meter,()=>now,new ResidentCli(),()=>new FakeSpeechBackend())){
   int auto=0,finals=0;input.AutoStopped+=()=>auto++;input.Recognized+=s=>finals++;string error;Check(input.Start("auto",out error),"ResidentZeroStarts");WaitResident(()=>input.DeviceName!="","ResidentZeroReady");now=7999;input.Sample();Check(input.IsListening,"ResidentZeroWaitsEightSeconds");now=8000;input.Sample();Check(input.Completion.Wait(3000)&&auto==0&&finals==0&&worker.Decodes==0,"EightSecondNoSpeechNeverSubmitsOrHallucinates");
  }
  foreach(string reason in new[]{"manual","duration-limit"}){now=0;worker=new ResidentWorker();meter=new ResidentMeter();using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker))using(var input=new ResidentSpeechInput(manager,()=>meter,()=>now,new ResidentCli(),()=>new FakeSpeechBackend())){
   int auto=0;input.AutoStopped+=()=>auto++;string error;Check(input.Start("en",out error),"ResidentManualStarts");WaitResident(()=>input.DeviceName!="","ResidentManualReady");if(reason=="manual")input.Stop();else worker.Emit("stopped",reason:reason);Check(input.Completion.Wait(3000)&&auto==0&&input.LastRefinement.Text=="valid final","SpaceManualAndDurationLimitOnlyFillText");
  }}
 }
 private static void TestResidentAmbiguousMeter(){
  foreach(double peak in new[]{0.0,.002,.006,.9}){long now=0;var worker=new ResidentWorker();var meter=new ResidentMeter{Names=new[]{"Microphone A","MICROPHONE A"},Peak=peak};using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker))using(var input=new ResidentSpeechInput(manager,()=>meter,()=>now,new ResidentCli(),()=>new FakeSpeechBackend())){
   SpeechActivitySnapshot snapshot=null;int auto=0;input.ActivityChanged+=s=>snapshot=s;input.AutoStopped+=()=>auto++;string error;input.Start("zh",out error);WaitResident(()=>input.DeviceName!="","AmbiguousReady");input.Sample();now=20000;input.Sample();Check(snapshot!=null&&snapshot.State==SpeechActivityState.Unavailable&&input.IsListening&&auto==0,"AmbiguousMeterNeverTriggersAutoSend");input.Stop();Check(input.Completion.Wait(3000)&&input.LastRefinement.Text=="valid final","AmbiguousMeterKeepsValidManualResult");
  }}
 }
 private static void TestResidentDecoderBudget(){
  long now=0;var worker=new ResidentWorker{CrashDecode=true};var cli=new ResidentCli{Worker=worker};Guid id=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+id.ToString("N"));Directory.CreateDirectory(root);
  using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker)){
   try{manager.StartCaptureAsync(id,"en","",root,CancellationToken.None).GetAwaiter().GetResult();manager.StopCaptureAsync(id,"manual",CancellationToken.None).GetAwaiter().GetResult();var decoder=new WorkerSpeechDecoder(manager,id,cli);var budget=new SpeechDecodeBudget(TimeSpan.FromSeconds(1),()=>now);var result=new SpeechFinalizer().RunAsync(Path.Combine(root,"recording.wav"),"en","draft",budget,decoder,CancellationToken.None).GetAwaiter().GetResult();Check(result.Text=="cli final"&&budget.AttemptsUsed==2&&worker.Decodes==1&&cli.Calls==1,"CrashAfterAttemptOneLeavesOneCliAttempt");Check(manager.Status.CaptureFailureCode==""&&manager.Status.RefinementFailureCode=="speech_worker_decode_crashed","DecodeCrashDoesNotMasqueradeAsCaptureFailure");}finally{manager.CompleteSession(id);manager.TryCleanupOwnedDirectory(id);}
  }
 }
 private static void TestResidentCrash(){
  foreach(bool noAudio in new[]{true,false}){long now=0;var worker=new ResidentWorker{NoAudio=noAudio};var meter=new ResidentMeter();var cli=new ResidentCli{Worker=worker};using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker))using(var input=new ResidentSpeechInput(manager,()=>meter,()=>now,cli,()=>new FakeSpeechBackend())){
   string delivered="";input.Recognized+=s=>delivered=s;string error;input.Start("zh",out error);WaitResident(()=>input.DeviceName!="","CrashCaptureReady");worker.Emit("draft","saved draft");worker.Exited=true;manager.Tick();Check(input.Completion.Wait(3000),"CrashRecoveryCompletes");Check(delivered==(noAudio?"saved draft":"cli final")&&worker.Starts==1,"CrashWithoutAudioPreservesDraftAndNeverRerecords");Check(input.Status.CaptureFailureCode!="","SuccessfulRefinementKeepsCaptureFailure");Check(cli.Calls==(noAudio?0:1),"CaptureCrashUsesOnlyAvailableOwnedAudio");
  }}
 }
 private static void TestResidentCancellation(){
  long now=0;var worker=new ResidentWorker{HoldStart=true};var cli=new ResidentCli{Worker=worker};int legacy=0;using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker))using(var input=new ResidentSpeechInput(manager,()=>new ResidentMeter(),()=>now,cli,()=>{legacy++;return new FakeSpeechBackend();})){
   int events=0;input.Recognized+=s=>events++;string error;input.Start("zh",out error);WaitResident(()=>worker.StartCommand!=null,"CancelPendingCapture");input.Dispose();worker.Ready();Check(input.Completion.Wait(3000)&&events==0&&cli.Calls==0&&legacy==0,"CancelIsNotFallbackFailure");
  }
  worker=new ResidentWorker{HoldDecode=true};var workers=new List<ResidentWorker>();using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>{var w=workers.Count==0?worker:new ResidentWorker();workers.Add(w);return w;}))using(var facade=new SpeechInput(lang=>new ResidentSpeechInput(manager,()=>new ResidentMeter(),()=>now,new ResidentCli(),()=>new FakeSpeechBackend()))){
   var results=new List<string>();facade.Recognized+=s=>results.Add(s);facade.Start("en");WaitResident(()=>facade.DeviceName!="","OldFinalReady");facade.Stop();WaitResident(()=>worker.Decodes==1,"OldFinalPending");facade.Cancel();facade.Start("zh");WaitResident(()=>workers.Count==2&&facade.DeviceName!="","NewRecordingReady");worker.Emit("draft","old draft");worker.DecodeReply.TrySetResult(new SpeechWorkerMessage{Version=1,InstanceId=worker.Instance,SessionId=worker.StartCommand.SessionId,RequestId=Guid.NewGuid(),Kind="final",Payload=new SpeechWorkerPayload{text="old final"}});facade.Stop();WaitResident(()=>results.Count==1,"NewFinalDelivered");Check(results[0]=="valid final","EditThenNewRecordingIgnoresOldFinal");
  }
 }
 private static void TestResidentImmediateCleanup(){
  long now=0;var worker=new ResidentWorker();using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker)){
   Guid pending=Guid.NewGuid();string owned=Path.Combine(Path.GetTempPath(),"Yike-voice-"+pending.ToString("N"));Directory.CreateDirectory(owned);manager.RegisterOwnedDirectory(pending,owned);
   try{Check(manager.TryCleanupOwnedDirectory(pending)&&!Directory.Exists(owned),"PreCaptureOwnedDirectoryCanBeCancelledCleanly");}finally{if(Directory.Exists(owned))Directory.Delete(owned);}
   var input=new ResidentSpeechInput(manager,()=>new ResidentMeter(),()=>now,new ResidentCli(),()=>new FakeSpeechBackend());string error;input.Start("en",out error);input.Dispose();Check(input.Completion.Wait(3000),"ImmediateCancelCompletes");
   string directory=(string)typeof(ResidentSpeechInput).GetField("directory",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(input);
   WaitResident(()=>!Directory.Exists(directory),"ImmediateCancelNeverLeaksUnregisteredOwnedDirectory");
  }
 }
 private static void TestUnconfirmedWorkerNeverStartsCli(){
  long now=0;var worker=new ResidentWorker{RefuseExit=true};var cli=new ResidentCli{Worker=worker};Guid id=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+id.ToString("N"));Directory.CreateDirectory(root);using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker)){
   try{manager.StartCaptureAsync(id,"en","",root,CancellationToken.None).GetAwaiter().GetResult();manager.StopCaptureAsync(id,"manual",CancellationToken.None).GetAwaiter().GetResult();var decoder=new WorkerSpeechDecoder(manager,id,cli,true);bool rejected=false;try{decoder.DecodeAsync(new SpeechDecodeRequest(Path.Combine(root,"recording.wav"),"en",false,30000),CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){rejected=true;}Check(rejected&&cli.Calls==0,"UnconfirmedNativeExitNeverRunsCliFallback");}finally{worker.RefuseExit=false;manager.CancelAsync(SpeechWorkerProtocol.ControlSession).GetAwaiter().GetResult();manager.TryCleanupOwnedDirectory(id);}
  }
 }
 private static void TestResidentQualityPolicy(){
  foreach(bool low in new[]{true,false}){long now=0;var worker=new ResidentWorker{Probabilities=low?new[]{.1,.1}:null,StopFailure="capture_device_disconnected"};var meter=new ResidentMeter();using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>now,()=>worker))using(var input=new ResidentSpeechInput(manager,()=>meter,()=>now,new ResidentCli(),()=>new FakeSpeechBackend())){
   string error;input.Start("zh",out error);WaitResident(()=>input.DeviceName!="","QualityCaptureReady");input.Stop();Check(input.Completion.Wait(3000),"QualityRefinementComplete");var result=input.LastRefinement;Check(result.AllowAutoSubmit==!low,"OnlyExplicitlyLowQualityBlocksAutoSend");
   Check(App.ShouldAutoSubmitVoiceResult(true,true,false,true,result.Text,result.AllowAutoSubmit)==!low,"SixConditionVoiceAutoSendChecksFinalQuality");Check(!App.ShouldAutoSubmitVoiceResult(false,true,false,true,result.Text,result.AllowAutoSubmit),"LatestPreferenceAtFinalDeliveryOverridesEarlierAutoSend");
   Check(input.Status.CaptureFailureCode=="capture_device_disconnected","SuccessfulRefinementKeepsSpecificCaptureFailure");
  }}
 }
 private static void TestResidentRealSampling(){
  var clock=System.Diagnostics.Stopwatch.StartNew();var worker=new ResidentWorker();var meter=new ResidentMeter{Peak=.05};long lastVoice=0,stopped=0;
  using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.ElapsedMilliseconds,()=>worker))using(var input=new ResidentSpeechInput(manager,()=>meter,()=>clock.ElapsedMilliseconds,new ResidentCli(),()=>new FakeSpeechBackend(),true)){
   input.ActivityChanged+=s=>{if(s.Peak>.01)lastVoice=clock.ElapsedMilliseconds;if(clock.ElapsedMilliseconds>=700)meter.Peak=0;};input.AutoStopped+=()=>stopped=clock.ElapsedMilliseconds;string error;Check(input.Start("en",out error),"ResidentRealTimerStarts");Check(SpinWait.SpinUntil(()=>stopped>0,9000),"ResidentRealTimerStopsWithoutManualSampling");Check(stopped-lastVoice>=6000&&stopped-lastVoice<=6600,"ResidentRealSixtyMillisecondSamplingStopsIn6To6Point6Seconds");Check(input.Completion.Wait(3000),"ResidentRealTimerFinalizationCompletes");
  }
 }
}
}
