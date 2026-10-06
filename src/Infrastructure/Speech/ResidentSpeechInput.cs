using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal sealed class ResidentSpeechInput:ISpeechInputBackend,ISpeechBackendDiagnostics,ISpeechBackendCompletion {
 private readonly object sync=new object();private readonly SpeechWorkerManager manager;private readonly Func<ISpeechCaptureMeter> meterFactory;private readonly Func<long> now;private readonly ISpeechFinalDecoder cli;private readonly Func<ISpeechInputBackend> legacyFactory;private readonly bool automatic;
 private readonly CancellationTokenSource cancellation=new CancellationTokenSource();private readonly TaskCompletionSource<int> completion=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
 private ISpeechCaptureMeter meter;private ISpeechInputBackend legacy;private Timer poll;private AdaptiveSpeechDetector detector;private Guid session;private SpeechWorkerMessage pendingStop;private string directory,language,draft="",captureFailure="",refinementFailure="";private long readyAt,checkedDefault;private bool listening,ready,stopping,disposed,unavailable,started,heard;
 internal ResidentSpeechInput(SpeechWorkerManager manager):this(manager,null,null,null,null,true){}
 internal ResidentSpeechInput(SpeechWorkerManager manager,Func<ISpeechCaptureMeter> meter,Func<long> now,ISpeechFinalDecoder cli,Func<ISpeechInputBackend> legacy,bool automaticSampling=false){
  if(manager==null)throw new ArgumentNullException("manager");this.manager=manager;meterFactory=meter??(()=>new MicrophoneLevel());var clock=Stopwatch.StartNew();this.now=now??(()=>clock.ElapsedMilliseconds);this.cli=cli;legacyFactory=legacy??(()=>new WhisperSpeechInput());automatic=automaticSampling;DeviceName="";
 }
 public bool IsListening{get{lock(sync)return !disposed&&(legacy!=null?legacy.IsListening:listening);}}public string DeviceName{get;private set;}
 public SpeechRefinementResult LastRefinement{get;private set;}public SpeechWorkerStatus Status{get{var current=manager.Status;return new SpeechWorkerStatus(current.State,current.IsWarm,current.LoadMilliseconds,current.CaptureReadyMilliseconds,captureFailure,refinementFailure);}}
 internal Task Completion{get{return completion.Task;}}
 public event Action<SpeechActivitySnapshot> ActivityChanged;public event Action<string> Hypothesized,Recognized,Failed;public event Action AutoStopped;public event Action<int> AudioLevelChanged;
 public event Action<string> CaptureEnded,Finalized;
 public bool Start(string language,out string error){
  lock(sync){error=null;if(started||disposed){error="录音会话不能重复启动。";return false;}this.language=WhisperSpeechInput.LanguageCode(language);session=Guid.NewGuid();directory=Path.Combine(Path.GetTempPath(),"Yike-voice-"+session.ToString("N"));
   try{Directory.CreateDirectory(directory);manager.RegisterOwnedDirectory(session,directory);}catch(Exception){error="无法创建本次录音目录。";return false;}started=listening=true;
  }
  manager.WorkerEvent+=OnWorkerEvent;manager.StatusChanged+=OnStatus;
  var operation=Task.Run(()=>StartAsync());operation.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);return true;
 }
 private async Task StartAsync(){bool failed=false;
  try{
   cancellation.Token.ThrowIfCancellationRequested();string device="";
   lock(sync){if(disposed)throw new OperationCanceledException();try{meter=meterFactory();if(SpeechCaptureDevice.ResolveCaptureIndex(meter.DeviceName,meter.CaptureNames).HasValue)device=meter.DeviceName;else{unavailable=true;captureFailure="capture_device_mismatch";}}catch(Exception){unavailable=true;captureFailure="meter_unavailable";}}
   var endpoint=await manager.StartCaptureAsync(session,language,device,directory,cancellation.Token).ConfigureAwait(false);
   SpeechWorkerMessage terminal=null;
   lock(sync){if(disposed||stopping)throw new OperationCanceledException();
    if(pendingStop!=null){terminal=pendingStop;pendingStop=null;stopping=true;listening=false;ReleaseMeter();}
    else{readyAt=now();detector=new AdaptiveSpeechDetector();ready=true;
     if(meter==null||endpoint.DeviceIndex<0||!SpeechCaptureDevice.ResolveCaptureIndex(endpoint.DeviceName,new[]{meter.DeviceName}).HasValue){unavailable=true;captureFailure="capture_device_mismatch";}
     DeviceName=endpoint.DeviceName;if(automatic)poll=new Timer(_=>Sample(),null,0,60);
    }
   }
   if(terminal!=null)await FinishAsync(terminal,false,false).ConfigureAwait(false);return;
  }catch(OperationCanceledException){}
  catch(Exception){failed=true;}
  if(failed&&!cancellation.IsCancellationRequested){
   captureFailure="speech_capture_start_failed";
   try{await manager.EnsureExitedAsync(cancellation.Token).ConfigureAwait(false);
    // A failed acknowledgement must never open a second mic if this worker
    // already saved audio. Recover it, or retain the draft, instead.
    if(SpeechOwnedWave.Recover(session,directory)){await FinishAsync(null,true,false).ConfigureAwait(false);return;}
    lock(sync){if(disposed||stopping)throw new OperationCanceledException();ReleaseMeter();legacy=legacyFactory();BindLegacy(legacy);string error;if(!legacy.Start(language,out error))throw new IOException("legacy_capture_start_failed");DeviceName=legacy.DeviceName;}
    manager.TryCleanupOwnedDirectory(session);return;
   }catch(OperationCanceledException){}catch(Exception){Deliver(()=>{if(Failed!=null)Failed("语音组件未能启动，请重试。");});}
  }
  await CancelAndComplete().ConfigureAwait(false);
 }
 private void BindLegacy(ISpeechInputBackend backend){
  backend.Hypothesized+=s=>Deliver(()=>{draft=s;if(Hypothesized!=null)Hypothesized(s);});backend.Recognized+=s=>Deliver(()=>{LastRefinement=backend.LastRefinement;if(Recognized!=null)Recognized(s);completion.TrySetResult(0);});
  backend.ActivityChanged+=s=>Deliver(()=>{if(ActivityChanged!=null)ActivityChanged(s);});backend.AudioLevelChanged+=s=>Deliver(()=>{if(AudioLevelChanged!=null)AudioLevelChanged(s);});backend.AutoStopped+=()=>Deliver(()=>{if(AutoStopped!=null)AutoStopped();});backend.Failed+=s=>Deliver(()=>{if(Failed!=null)Failed(s);completion.TrySetResult(0);});
 }
 private void Deliver(Action action){lock(sync){if(!disposed&&!cancellation.IsCancellationRequested)action();}}
 internal void Sample(){bool silence=false,startup=false;
  lock(sync){if(disposed||!listening||!ready||stopping)return;long elapsed=Math.Max(0,now()-readyAt);SpeechActivitySnapshot activity;
   try{activity=unavailable?detector.MarkUnavailable(elapsed):detector.Observe(meter.ReadPeak(),elapsed);if(meter!=null&&elapsed-checkedDefault>=1000){checkedDefault=elapsed;if(meter.DefaultDeviceChanged()&&string.IsNullOrEmpty(captureFailure))captureFailure="default_device_changed_next_session";}}
   catch(Exception){unavailable=true;captureFailure="meter_unavailable";activity=detector.MarkUnavailable(elapsed);}
   if(activity.State==SpeechActivityState.Unavailable){unavailable=true;if(string.IsNullOrEmpty(captureFailure))captureFailure="meter_unavailable";}heard|=activity.HeardSpeech;
   if(ActivityChanged!=null)ActivityChanged(activity);if(AudioLevelChanged!=null)AudioLevelChanged((int)Math.Round(activity.Peak*100));silence=activity.ShouldAutoStop;startup=activity.StartupTimedOut;
  }
  if(silence||startup)BeginStop(silence?"silence":"startup-silence",silence,startup);
 }
 private void OnWorkerEvent(SpeechWorkerMessage message){
  if(message.SessionId!=session)return;
  if(message.Kind=="draft")Deliver(()=>{draft=message.Payload==null?draft:message.Payload.text??"";if((heard||unavailable)&&Hypothesized!=null&&!string.IsNullOrWhiteSpace(draft))Hypothesized(draft);});
  else if(message.Kind=="stopped"){
   bool finish=false;lock(sync){if(disposed||stopping||pendingStop!=null)return;if(!ready){pendingStop=message;listening=false;}else{stopping=true;listening=false;ReleaseMeter();finish=true;}}
   Deliver(()=>{if(CaptureEnded!=null)CaptureEnded(EndNotice(message,false));});
   if(finish)ObserveFinish(FinishAsync(message,false,false));
  }
 }
 private void OnStatus(SpeechWorkerStatus snapshot){
  // Read current state: queued manager notifications can arrive out of order.
  bool recover=false;lock(sync){if(disposed||stopping||!ready||!listening||manager.Status.State!=SpeechWorkerState.Degraded)return;captureFailure=manager.Status.CaptureFailureCode;listening=false;stopping=true;ReleaseMeter();recover=true;}
  if(recover)ObserveFinish(FinishAsync(null,true,false));
 }
 public bool Stop(){ISpeechInputBackend old;lock(sync)old=legacy;if(old!=null)return old.Stop();return BeginStop("manual",false,false);}
 private bool BeginStop(string reason,bool silence,bool startup){bool pending;
  lock(sync){if(disposed||stopping||!listening)return false;stopping=true;listening=false;pending=!ready;ReleaseMeter();if(silence&&AutoStopped!=null)AutoStopped();}
  Deliver(()=>{if(CaptureEnded!=null)CaptureEnded(null);});
  if(pending){cancellation.Cancel();ObserveFinish(CancelAndComplete());}else ObserveFinish(StopAsync(reason,startup));return true;
 }
 private async Task StopAsync(string reason,bool startup){SpeechWorkerMessage stopped=null;bool crashed=false;
  bool cancelled=false;try{stopped=await manager.StopCaptureAsync(session,reason,cancellation.Token).ConfigureAwait(false);}
  catch(OperationCanceledException){cancelled=true;}
  catch(Exception){crashed=true;captureFailure="speech_capture_stop_failed";}
  if(cancelled){await CancelAndComplete().ConfigureAwait(false);return;}
  await FinishAsync(stopped,crashed,startup).ConfigureAwait(false);
 }
 private void ObserveFinish(Task task){task.ContinueWith(t=>{var observed=t.Exception;completion.TrySetResult(0);},TaskContinuationOptions.OnlyOnFaulted);}
 private async Task FinishAsync(SpeechWorkerMessage stopped,bool crashed,bool startup){
  try{
   if(stopped!=null&&stopped.Payload!=null){if(!string.IsNullOrWhiteSpace(stopped.Payload.text))draft=stopped.Payload.text;if(!string.IsNullOrEmpty(stopped.Payload.failureCode))captureFailure=stopped.Payload.failureCode;}
   if(crashed)await manager.EnsureExitedAsync(cancellation.Token).ConfigureAwait(false);cancellation.Token.ThrowIfCancellationRequested();
   if(startup){Deliver(()=>{if(Failed!=null)Failed("未检测到麦克风声音，请检查输入设备和权限。");});return;}
   string audio=Path.Combine(directory,"recording.wav");SpeechRefinementResult result=null;
   if(SpeechOwnedWave.Recover(session,directory)){
    var clock=Stopwatch.StartNew();var budget=new SpeechDecodeBudget(SpeechRefinement.AudioDuration(audio),()=>clock.ElapsedMilliseconds);
    result=await new SpeechFinalizer(SpeechWorkerPolicy.ConfidenceRetryEnabled).RunAsync(audio,language,draft,budget,new WorkerSpeechDecoder(manager,session,cli,crashed),cancellation.Token).ConfigureAwait(false);
   }else{refinementFailure="capture_audio_unavailable";result=new SpeechRefinementResult(draft,false,true,refinementFailure,null);}
   Deliver(()=>{LastRefinement=result;refinementFailure=result.FailureCode;if(Recognized!=null&&!string.IsNullOrWhiteSpace(result.Text))Recognized(result.Text);if(crashed&&Failed!=null)Failed("录音已中断，已保留本次可用结果，请核对。");});
  }catch(OperationCanceledException){}
  catch(Exception){refinementFailure="refinement_failed";Deliver(()=>{LastRefinement=new SpeechRefinementResult(draft,false,true,refinementFailure,null);if(Recognized!=null&&!string.IsNullOrWhiteSpace(draft))Recognized(draft);if(Failed!=null)Failed("识别未完成，已保留草稿。");});}
  finally{
   if(!startup)Deliver(()=>{if(Finalized!=null)Finalized(EndNotice(stopped,true)??(LastRefinement==null||string.IsNullOrWhiteSpace(LastRefinement.Text)?"语音已结束，没有可用的识别结果，请重试。":null));});
   manager.CompleteSession(session);manager.TryCleanupOwnedDirectory(session);completion.TrySetResult(0);
  }
 }
 private static string EndNotice(SpeechWorkerMessage stopped,bool final){
  var payload=stopped==null?null:stopped.Payload;if(payload==null)return null;
  if(payload.stopReason=="duration-limit")return final?"已达到 5 分钟录音上限，已停止录音，请核对结果后翻译。":"已达到 5 分钟录音上限，正在整理已录内容。";
  if(payload.stopReason=="capture-failure"||!string.IsNullOrEmpty(payload.failureCode))return final?"录音已中断，已保留本次可用结果，请核对。":"录音已中断，正在整理本次可用结果。";
  return null;
 }
 private async Task CancelAndComplete(){try{await manager.CancelAsync(session).ConfigureAwait(false);}catch(Exception){}finally{manager.CompleteSession(session);manager.TryCleanupOwnedDirectory(session);completion.TrySetResult(0);}}
 private void ReleaseMeter(){if(poll!=null){poll.Dispose();poll=null;}if(meter!=null){meter.Dispose();meter=null;}}
 public void Dispose(){ISpeechInputBackend old;bool completed;
  lock(sync){if(disposed)return;disposed=true;listening=false;ReleaseMeter();old=legacy;completed=completion.Task.IsCompleted;}
  manager.WorkerEvent-=OnWorkerEvent;manager.StatusChanged-=OnStatus;cancellation.Cancel();if(old!=null)old.Dispose();
  if(!completed)ObserveFinish(CancelAndComplete());else manager.TryCleanupOwnedDirectory(session);
 }
}
}
