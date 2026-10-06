using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
public static partial class Tests {
 private static void RunSpeechWorkerLifecycleTests(List<string> lines){
  Check(!SpeechWorkerPolicy.EnabledForProduction&&!SpeechWorkerPolicy.ConfidenceRetryEnabled,"NativePoliciesRemainDisabledUntilAcceptance");
  var clock=new WorkerClock();var workers=new List<FakeSpeechWorker>();Func<ISpeechWorkerProcess> factory=()=>{var worker=new FakeSpeechWorker();workers.Add(worker);return worker;};
  using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,factory)){
   manager.SetPreheat(false,true);manager.Tick();Check(workers.Count==0,"DefaultPreheatCreatesNoProcess");manager.SetPreheat(true,false);clock.Now=5000;manager.Tick();Check(workers.Count==0,"PreviewNeverPreheats");manager.SetPreheat(true,true);clock.Now=6999;manager.Tick();Check(workers.Count==0,"RealUiPreheatWaits1999Milliseconds");clock.Now=7000;manager.Tick();
   Check(SpinWait.SpinUntil(()=>workers.Count==1&&manager.Status.State==SpeechWorkerState.Ready,3000),"RealUiPreheatsAt2000Milliseconds");
   clock.Now=126999;manager.Tick();Check(!workers[0].HasExited,"Idle119999MillisecondsKeepsOneWorker");clock.Now=127000;manager.Tick();Check(workers[0].HasExited&&manager.Status.State==SpeechWorkerState.Released,"Idle120000MillisecondsReleasesModel");clock.Now=200000;manager.Tick();Check(workers.Count==1,"IdleReleaseNeverLoopsPreheat");
   var ready=manager.EnsureReadyAsync(CancellationToken.None).GetAwaiter().GetResult();Check(workers.Count==2&&ready.State==SpeechWorkerState.Ready&&!ready.IsWarm,"ReleasedWorkerLoadsColdAgain");manager.EnsureReadyAsync(CancellationToken.None).GetAwaiter().GetResult();Check(workers.Count==2,"ReadyCallsReuseOnlyOneWorker");
  }
  clock=new WorkerClock();FakeSpeechWorker blocked=null;using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>blocked=new FakeSpeechWorker{HoldPreheat=true})){
   var ready=manager.EnsureReadyAsync(CancellationToken.None);clock.Now=19999;manager.Tick();Check(!ready.IsCompleted&&!blocked.HasExited,"Load19999MillisecondsDoesNotPrematurelyRelease");clock.Now=20000;manager.Tick();Check(SpinWait.SpinUntil(()=>ready.IsCompleted,3000)&&ready.IsFaulted&&blocked.HasExited,"Load20000MillisecondsTimesOutOwnedWorker");
  }
  clock=new WorkerClock();FakeSpeechWorker capture=null;Guid session=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+session.ToString("N"));Directory.CreateDirectory(root);
  try{using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>capture=new FakeSpeechWorker())){
   manager.SetPreheat(true,true);manager.StartCaptureAsync(session,"zh","",root,CancellationToken.None).GetAwaiter().GetResult();manager.SetPreheat(false,true);manager.Tick();Check(!capture.HasExited,"DisablePreheatDuringActiveCapture");
   manager.StopCaptureAsync(session,"manual",CancellationToken.None).GetAwaiter().GetResult();Check(!capture.HasExited,"StopKeepsModelThroughFinalization");manager.CompleteSession(session);manager.Tick();Check(capture.HasExited,"DisablePreheatReleasesAfterSessionCompletes");
  }}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
  TestStaleCancellationPreservesPendingCapture();TestDefaultCaptureKeepsIdleCache();TestWorkerCrashAndCleanup();TestWorkerCancellationDeadline();TestWorkerCircuitAndExit();TestOldInstanceAndPendingSession();TestCancelHasIndependentDeadline();TestOwnedStartupFailure();TestOwnedJobIsolation();
  lines.Add("PASS resident lifecycle: actual-ready state, monotonic deadlines, lazy preheat, bounded idle reuse and session-safe release");
 }
 private static void TestOwnedStartupFailure(){
  string root=Path.Combine(Path.GetTempPath(),"Yike-worker-invalid-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);string invalid=Path.Combine(root,"worker.exe");File.WriteAllBytes(invalid,new byte[]{0,1,2,3});
  try{using(var owned=new OwnedSpeechWorkerProcess()){bool failed=false;try{owned.Start(invalid,Guid.NewGuid());}catch(IOException){failed=true;}Check(failed&&owned.HasExited,"InvalidWorkerImageIsRejectedWithoutInvokingWindowsLoader");}}finally{Directory.Delete(root,true);}
  using(var owned=new OwnedSpeechWorkerProcess(process=>{throw new System.ComponentModel.Win32Exception(5);})){
   bool failed=false;try{owned.Start(new SpeechRuntimePaths(Path.GetDirectoryName(typeof(Tests).Assembly.Location)).WorkerPath,Guid.NewGuid());}catch(System.ComponentModel.Win32Exception){failed=true;}Check(failed&&owned.HasExited,"InjectedOsStartFailureStillHasSafeOwnedExitState");
  }
 }
 private static void TestStaleCancellationPreservesPendingCapture(){
  Guid oldSession=Guid.NewGuid(),nextSession=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+nextSession.ToString("N"));Directory.CreateDirectory(root);
  var retiredCancellation=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);FakeSpeechWorker worker=null;
  try{using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>0,()=>worker=new FakeSpeechWorker())){
   // Hold the previous cancellation's continuation after its process has retired.
   typeof(SpeechWorkerManager).GetField("cancelTask",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(manager,retiredCancellation.Task);
   var next=manager.StartCaptureAsync(nextSession,"en","",root,CancellationToken.None);
   Check(!next.IsCompleted&&worker==null,"NextCaptureWaitsForRetiredCancellation");
   manager.CancelAsync(oldSession).GetAwaiter().GetResult();retiredCancellation.SetResult(0);
   bool started=false;try{started=next.GetAwaiter().GetResult().DeviceName=="Microphone A";}catch(OperationCanceledException){}
   Check(started,"StaleCancellationCannotEraseNextCaptureReservation");
   manager.StopCaptureAsync(nextSession,"manual",CancellationToken.None).GetAwaiter().GetResult();
   Check(manager.DecodeAsync(nextSession,new SpeechDecodeRequest(Path.Combine(root,"recording.wav"),"en",false,30000),CancellationToken.None).GetAwaiter().GetResult().Text=="text","NextSessionCanDeliverItsOwnFinalAfterStaleCancellation");
   manager.CompleteSession(nextSession);manager.TryCleanupOwnedDirectory(nextSession);
  }}finally{retiredCancellation.TrySetResult(0);if(Directory.Exists(root))Directory.Delete(root,true);}
 }
 private static void TestDefaultCaptureKeepsIdleCache(){
  var clock=new WorkerClock();FakeSpeechWorker worker=null;Guid session=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+session.ToString("N"));Directory.CreateDirectory(root);
  try{using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>worker=new FakeSpeechWorker())){
   manager.SetPreheat(false,true);manager.StartCaptureAsync(session,"en","",root,CancellationToken.None).GetAwaiter().GetResult();manager.StopCaptureAsync(session,"manual",CancellationToken.None).GetAwaiter().GetResult();manager.CompleteSession(session);Check(!worker.HasExited,"DefaultPreheatOffStillCachesSuccessfulSession");clock.Now=119999;manager.Tick();Check(!worker.HasExited,"DefaultSessionRetainsFullIdleWindow");clock.Now=120000;manager.Tick();Check(worker.HasExited,"DefaultSessionEventuallyReleasesIdleModel");
  }}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
 }
 private static void TestCancelHasIndependentDeadline(){
  var clock=new WorkerClock();FakeSpeechWorker worker=null;using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>worker=new FakeSpeechWorker{HoldPreheat=true,HoldCancel=true})){
   var ready=manager.EnsureReadyAsync(CancellationToken.None);clock.Now=19000;var cancel=manager.CancelAsync(SpeechWorkerProtocol.ControlSession);clock.Now=20000;manager.Tick();Check(!cancel.IsCompleted&&!worker.HasExited,"UserCancellationDoesNotBecomeLoadingFailure");clock.Now=21000;manager.Tick();Check(cancel.Wait(3000)&&worker.HasExited&&SpinWait.SpinUntil(()=>ready.IsFaulted,3000),"CancellationUsesOwnTwoSecondDeadlineDuringLoad");
  }
 }
 private static void TestOldInstanceAndPendingSession(){
  var clock=new WorkerClock();FakeSpeechWorker old=null,current=null;int spawned=0;using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>++spawned==1?(old=new FakeSpeechWorker()):(current=new FakeSpeechWorker{HoldPreheat=true}))){
   manager.EnsureReadyAsync(CancellationToken.None).GetAwaiter().GetResult();clock.Now=120000;manager.Tick();var next=manager.EnsureReadyAsync(CancellationToken.None);var command=current.Sent.Find(m=>m.Kind=="preheat");current.Preheat.TrySetResult(new SpeechWorkerMessage{Version=1,InstanceId=old.Instance,SessionId=command.SessionId,RequestId=command.RequestId,Kind="ready",Payload=new SpeechWorkerPayload{contextInitializationCount=1}});
   Check(SpinWait.SpinUntil(()=>next.IsCompleted,3000)&&next.IsFaulted&&manager.Status.State!=SpeechWorkerState.Ready,"OldInstanceReplyCannotCompleteNewSession");
  }
  Guid first=Guid.NewGuid(),second=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+first.ToString("N")),other=Path.Combine(Path.GetTempPath(),"Yike-voice-"+second.ToString("N"));Directory.CreateDirectory(root);Directory.CreateDirectory(other);
  try{using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>new FakeSpeechWorker{HoldPreheat=true})){
   var pending=manager.StartCaptureAsync(first,"auto","",root,CancellationToken.None);bool rejected=false;try{manager.StartCaptureAsync(second,"zh","",other,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidOperationException){rejected=true;}Check(rejected,"AtMostOnePendingUserSession");manager.Dispose();Check(SpinWait.SpinUntil(()=>pending.IsCompleted,3000),"DisposalCompletesPendingCaptureWaiter");
  }}finally{if(Directory.Exists(root))Directory.Delete(root,true);if(Directory.Exists(other))Directory.Delete(other,true);}
 }
 private static void TestWorkerCrashAndCleanup(){
  var clock=new WorkerClock();FakeSpeechWorker worker=null;Guid session=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+session.ToString("N"));Directory.CreateDirectory(root);string wave=Path.Combine(root,"recording.wav");File.WriteAllBytes(wave,new byte[]{1,2,3});
  try{using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>worker=new FakeSpeechWorker())){
   manager.StartCaptureAsync(session,"zh","",root,CancellationToken.None).GetAwaiter().GetResult();worker.Exited=true;manager.Tick();Check(File.Exists(wave),"WorkerCrashPreservesOwnedWaveUntilParentRecoveryFinishes");
   using(var locked=new FileStream(wave,FileMode.Open,FileAccess.Read,FileShare.None))Check(!manager.TryCleanupOwnedDirectory(session)&&Directory.Exists(root),"LockedWaveCleanupRetainsOwnershipThenRetries");
   manager.Tick();Check(!Directory.Exists(root),"ReleasedWaveHandleAllowsOwnedCleanupRetry");
  }}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
 }
 private static void TestWorkerCancellationDeadline(){
  var clock=new WorkerClock();FakeSpeechWorker worker=null;Guid session=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+session.ToString("N"));Directory.CreateDirectory(root);
  try{using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>worker=new FakeSpeechWorker{HoldCancel=true})){
   manager.StartCaptureAsync(session,"en","",root,CancellationToken.None).GetAwaiter().GetResult();int events=0;manager.WorkerEvent+=m=>Interlocked.Increment(ref events);var cancel=manager.CancelAsync(session);worker.Emit(new SpeechWorkerMessage{Version=1,InstanceId=worker.Instance,SessionId=session,RequestId=Guid.NewGuid(),Kind="draft"});Check(events==0,"CancellationImmediatelyInvalidatesDraftGeneration");
   clock.Now=1999;manager.Tick();Check(!cancel.IsCompleted&&!worker.HasExited,"CancelAt1999MillisecondsStillWaitsForAcknowledgement");clock.Now=2000;manager.Tick();Check(cancel.Wait(3000)&&worker.HasExited,"CancelAt2000MillisecondsTerminatesOnlyOwnedWorker");
  }}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
 }
 private static void TestWorkerCircuitAndExit(){
  var clock=new WorkerClock();int spawned=0;using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>{spawned++;return new FakeSpeechWorker{FailPreheat=spawned<=2};})){
   for(int i=0;i<2;i++){bool failed=false;try{manager.EnsureReadyAsync(CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){failed=true;}Check(failed,"InitializationFailureIsReported");}
   bool circuit=false;try{manager.EnsureReadyAsync(CancellationToken.None);}catch(InvalidOperationException){circuit=true;}Check(circuit&&spawned==2,"TwoCrashesOpenCircuitWithoutRestartLoop");manager.RetryWorker();Check(manager.EnsureReadyAsync(CancellationToken.None).GetAwaiter().GetResult().State==SpeechWorkerState.Ready&&spawned==3,"ExplicitRetryClearsCircuit");
  }
  FakeSpeechWorker worker=null;Guid session=Guid.NewGuid();string root=Path.Combine(Path.GetTempPath(),"Yike-voice-"+session.ToString("N"));Directory.CreateDirectory(root);
  try{var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock.Now,()=>worker=new FakeSpeechWorker{HoldDecode=true});
   manager.StartCaptureAsync(session,"en","",root,CancellationToken.None).GetAwaiter().GetResult();manager.StopCaptureAsync(session,"manual",CancellationToken.None).GetAwaiter().GetResult();var decode=manager.DecodeAsync(session,new SpeechDecodeRequest(Path.Combine(root,"recording.wav"),"en",false,30000),CancellationToken.None);manager.Dispose();manager.Dispose();
   Check(SpinWait.SpinUntil(()=>decode.IsCompleted,3000)&&decode.IsCanceled&&worker.HasExited,"ExitWhileDecodeRunningCompletesWaiterAndDisposesIdempotently");
  }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
 }
 private static void TestOwnedJobIsolation(){
  var info=new System.Diagnostics.ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30"){UseShellExecute=false,CreateNoWindow=true};
  using(var unrelated=System.Diagnostics.Process.Start(info))using(var owned=new OwnedSpeechWorkerProcess()){
   try{Guid instance=Guid.NewGuid();owned.Start(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory).WorkerPath,instance);var hello=new SpeechWorkerMessage{Version=1,InstanceId=instance,SessionId=SpeechWorkerProtocol.ControlSession,RequestId=Guid.NewGuid(),Kind="hello",Payload=new SpeechWorkerPayload()};
    var reply=owned.SendAsync(hello,CancellationToken.None);Check(reply.Wait(3000)&&reply.Result.Kind=="ready"&&reply.Result.Payload.contextInitializationCount==0,"OwnedJobHandshakeDoesNotLoadModelOrMicrophone");owned.TerminateOwnedJob();Check(owned.HasExited&&!unrelated.HasExited,"JobExitNeverKillsUnownedProcess");
   }finally{if(!unrelated.HasExited){unrelated.Kill();unrelated.WaitForExit(3000);}}
  }
 }
 private sealed class WorkerClock {internal long Now;}
 private sealed class FakeSpeechWorker:ISpeechWorkerProcess {
  internal bool HoldPreheat,HoldCancel,HoldDecode,Exited,FailPreheat;internal int Starts,Kills;internal Guid Instance;internal readonly List<SpeechWorkerMessage> Sent=new List<SpeechWorkerMessage>();
  internal TaskCompletionSource<SpeechWorkerMessage> Preheat=new TaskCompletionSource<SpeechWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously),Cancel=new TaskCompletionSource<SpeechWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
  public event Action<SpeechWorkerMessage> EventReceived;
  public bool HasExited{get{return Exited;}}
  public void Start(string path,Guid instance){Starts++;Instance=instance;}
  public Task<SpeechWorkerMessage> SendAsync(SpeechWorkerMessage message,CancellationToken token){lock(Sent)Sent.Add(message);if(message.Kind=="preheat"&&HoldPreheat)return Preheat.Task;if((message.Kind=="cancel"||message.Kind=="shutdown")&&HoldCancel)return Cancel.Task;if(message.Kind=="decode"&&HoldDecode)return new TaskCompletionSource<SpeechWorkerMessage>().Task;
   string kind=message.Kind=="hello"||message.Kind=="preheat"?"ready":message.Kind=="start"?"capture_started":message.Kind=="stop"?"stopped":message.Kind=="decode"?"final":"cancelled";
   if(message.Kind=="preheat"&&FailPreheat)kind="failure";return Task.FromResult(new SpeechWorkerMessage{Version=1,InstanceId=Instance,SessionId=message.SessionId,RequestId=message.RequestId,Kind=kind,Payload=new SpeechWorkerPayload{deviceName="Microphone A",deviceIndex=0,contextInitializationCount=message.Kind=="hello"?0:1,loadMilliseconds=10,captureReadyMilliseconds=5,failureCode=kind=="failure"?"native_initialization_failed":"",text=message.Kind=="decode"?"text":""}});
  }
  internal void Emit(SpeechWorkerMessage message){var handler=EventReceived;if(handler!=null)handler(message);}
  public void TerminateOwnedJob(){Kills++;Exited=true;}public void Dispose(){TerminateOwnedJob();}
 }
}
}
