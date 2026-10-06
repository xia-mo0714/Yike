using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal sealed class SpeechWorkerManager:IDisposable {
 private readonly object sync=new object();private readonly SpeechRuntimePaths paths;
 private readonly Func<long> now;private readonly Func<ISpeechWorkerProcess> factory;
 private readonly Dictionary<Guid,string> directories=new Dictionary<Guid,string>();private readonly HashSet<Guid> cleanupRequested=new HashSet<Guid>();private readonly Timer timer;
 private ISpeechWorkerProcess process;private Guid instance,activeSession,stoppedSession;private CancellationTokenSource ownerCancellation;
 private TaskCompletionSource<SpeechWorkerStatus> loading;private Task cancelTask=Task.FromResult(0);
 private SpeechWorkerStatus status=new SpeechWorkerStatus(SpeechWorkerState.Unloaded);
 private long lastNow,lastUsed,loadStarted,cancelStarted,preheatDue;private int failures,requests;
 private bool disposed,capturing,preheat,uiReady,preheatConsumed,releaseAfterSession,cancelling;
 internal event Action<SpeechWorkerStatus> StatusChanged;internal event Action<SpeechWorkerMessage> WorkerEvent;
 internal SpeechWorkerStatus Status{get{lock(sync)return status;}}
 internal bool IsCircuitOpen {get{lock(sync)return failures>=2;}}
 internal SpeechWorkerManager(SpeechRuntimePaths paths,Func<long> nowMilliseconds=null,Func<ISpeechWorkerProcess> processFactory=null){
  if(paths==null)throw new ArgumentNullException("paths");this.paths=paths;var clock=Stopwatch.StartNew();now=nowMilliseconds??(()=>clock.ElapsedMilliseconds);factory=processFactory??(()=>new OwnedSpeechWorkerProcess());
  if(nowMilliseconds==null)timer=new Timer(_=>Tick(),null,100,100);
 }
 private long Now(){lastNow=Math.Max(lastNow,now());return lastNow;}
 private void SetStatus(SpeechWorkerState state,bool warm=false,int? load=null,int? ready=null,string capture=null,string refinement=null){
  status=new SpeechWorkerStatus(state,warm,load??status.LoadMilliseconds,ready??status.CaptureReadyMilliseconds,capture??status.CaptureFailureCode,refinement??status.RefinementFailureCode);
  var snapshot=status;ThreadPool.QueueUserWorkItem(_=>{Action<SpeechWorkerStatus> handler;lock(sync){if(disposed)return;handler=StatusChanged;}if(handler!=null)try{handler(snapshot);}catch(Exception){}});
 }
 private static async Task<T> AwaitCancellation<T>(Task<T> task,CancellationToken token){
  if(!token.CanBeCanceled)return await task.ConfigureAwait(false);token.ThrowIfCancellationRequested();
  var interrupted=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
  using(token.Register(()=>interrupted.TrySetCanceled())){
   var completed=await Task.WhenAny(task,interrupted.Task).ConfigureAwait(false);return await completed.ConfigureAwait(false);
  }
 }
 internal void RegisterOwnedDirectory(Guid session,string root){ValidateDirectory(session,root);lock(sync){if(disposed)throw new ObjectDisposedException("SpeechWorkerManager");if(directories.Count>=64&&!directories.ContainsKey(session))throw new InvalidOperationException("speech_directory_limit");directories[session]=Path.GetFullPath(root);}}
 internal Task<SpeechWorkerStatus> EnsureReadyAsync(CancellationToken token){
  token.ThrowIfCancellationRequested();ISpeechWorkerProcess owned=null;Task<SpeechWorkerStatus> result;
  lock(sync){
   if(disposed)throw new ObjectDisposedException("SpeechWorkerManager");if(failures>=2)throw new InvalidOperationException("speech_worker_circuit_open");
   if(process!=null&&process.HasExited)FailOwned(process,"speech_worker_crashed");
   if(process!=null&&status.State==SpeechWorkerState.Ready&&!cancelling){lastUsed=Now();return Task.FromResult(new SpeechWorkerStatus(SpeechWorkerState.Ready,true,status.LoadMilliseconds,status.CaptureReadyMilliseconds,status.CaptureFailureCode,status.RefinementFailureCode));}
   if(loading!=null&&status.State==SpeechWorkerState.Loading)return AwaitCancellation(loading.Task,token);
   if(process!=null)throw new IOException("speech_worker_exit_unconfirmed");
   if(failures>=2)throw new InvalidOperationException("speech_worker_circuit_open");
   loading=new TaskCompletionSource<SpeechWorkerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);result=loading.Task;loadStarted=lastUsed=Now();instance=Guid.NewGuid();requests=0;ownerCancellation=new CancellationTokenSource();SetStatus(SpeechWorkerState.Loading);
   try{owned=factory();process=owned;owned.EventReceived+=OnWorkerEvent;owned.Start(paths.WorkerPath,instance);}catch(Exception){FailOwned(owned,"speech_worker_start_failed");return AwaitCancellation(result,token);}
  }
  var background=LoadAsync(owned);background.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);return AwaitCancellation(result,token);
 }
 private async Task LoadAsync(ISpeechWorkerProcess owned){try{
  var hello=await SendAsync(owned,"hello",SpeechWorkerProtocol.ControlSession,null,CancellationToken.None).ConfigureAwait(false);
  if(hello.Kind!="ready"||hello.Payload==null||hello.Payload.contextInitializationCount!=0)throw new InvalidDataException("speech_worker_handshake_failed");
  var ready=await SendAsync(owned,"preheat",SpeechWorkerProtocol.ControlSession,null,CancellationToken.None).ConfigureAwait(false);
  if(ready.Kind!="ready"||ready.Payload==null||ready.Payload.contextInitializationCount!=1||ready.Payload.loadMilliseconds<0)throw new InvalidDataException("speech_worker_initialization_failed");
  lock(sync){if(process!=owned||disposed||cancelling)return;lastUsed=Now();SetStatus(SpeechWorkerState.Ready,false,ready.Payload.loadMilliseconds);loading.TrySetResult(status);}
 }catch(Exception){lock(sync){if(process==owned&&!cancelling)FailOwned(owned,"speech_worker_initialization_failed");}}}
 private async Task<SpeechWorkerMessage> SendAsync(ISpeechWorkerProcess owned,string kind,Guid session,SpeechWorkerPayload payload,CancellationToken token){
  SpeechWorkerMessage command;CancellationToken lifetime;lock(sync){
   if(disposed||process!=owned||owned==null||owned.HasExited)throw new IOException("speech_worker_closed");
   command=new SpeechWorkerMessage{Version=1,InstanceId=instance,SessionId=session,RequestId=Guid.NewGuid(),Kind=kind,Payload=payload??new SpeechWorkerPayload()};requests++;lifetime=ownerCancellation.Token;
  }
  using(var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime)){
   var response=await AwaitCancellation(owned.SendAsync(command,linked.Token),linked.Token).ConfigureAwait(false);
   SpeechWorkerProtocol.Validate(response);
   lock(sync){if(process!=owned||response.InstanceId!=command.InstanceId||response.SessionId!=session||response.RequestId!=command.RequestId)throw new InvalidDataException("speech_worker_stale_reply");}
   return response;
  }
 }
 internal async Task<SpeechCaptureEndpoint> StartCaptureAsync(Guid sessionId,string language,string deviceName,string ownedDirectory,CancellationToken token){
  ValidateDirectory(sessionId,ownedDirectory);Task previous;lock(sync){if(disposed)throw new ObjectDisposedException("SpeechWorkerManager");if(activeSession!=Guid.Empty)throw new InvalidOperationException("speech_capture_busy");if(directories.Count>=64&&!directories.ContainsKey(sessionId))throw new InvalidOperationException("speech_directory_limit");activeSession=sessionId;stoppedSession=Guid.Empty;directories[sessionId]=Path.GetFullPath(ownedDirectory);previous=cancelTask;}
  try{
   await previous.ConfigureAwait(false);var ready=await EnsureReadyAsync(token).ConfigureAwait(false);ISpeechWorkerProcess owned;lock(sync){if(activeSession!=sessionId)throw new OperationCanceledException();owned=process;}
   var reply=await SendAsync(owned,"start",sessionId,new SpeechWorkerPayload{language=language,deviceName=deviceName,audioPath=ownedDirectory},token).ConfigureAwait(false);
   if(reply.Kind!="capture_started"||reply.Payload==null)throw new IOException("speech_capture_start_failed");
   lock(sync){if(activeSession!=sessionId)throw new OperationCanceledException();capturing=stoppedSession!=sessionId;lastUsed=Now();SetStatus(SpeechWorkerState.Ready,ready.IsWarm,ready:reply.Payload.captureReadyMilliseconds,capture:capturing?"":null);}
   return new SpeechCaptureEndpoint(reply.Payload.deviceName,reply.Payload.deviceIndex);
  }catch(Exception ex){if(!(ex is OperationCanceledException)){lock(sync){if(activeSession==sessionId){capturing=false;activeSession=Guid.Empty;if(process!=null)FailOwned(process,"speech_capture_start_failed");}}throw;}}
  await CancelAsync(sessionId).ConfigureAwait(false);throw new OperationCanceledException(token);
 }
 internal async Task<SpeechWorkerMessage> StopCaptureAsync(Guid sessionId,string reason,CancellationToken token){
  ISpeechWorkerProcess owned;lock(sync){if(activeSession!=sessionId||process==null)throw new IOException("speech_capture_missing");owned=process;}
  var reply=await SendAsync(owned,"stop",sessionId,new SpeechWorkerPayload{stopReason=reason},token).ConfigureAwait(false);
  if(reply.Kind!="stopped")throw new IOException("speech_capture_stop_failed");
  lock(sync){if(activeSession==sessionId){capturing=false;lastUsed=Now();if(reply.Payload!=null&&!string.IsNullOrEmpty(reply.Payload.failureCode))SetStatus(status.State,status.IsWarm,capture:Code(reply.Payload.failureCode));}}return reply;
 }
 internal async Task<SpeechRecognitionCandidate> DecodeAsync(Guid sessionId,SpeechDecodeRequest request,CancellationToken token){
  if(request==null)throw new ArgumentNullException("request");ISpeechWorkerProcess owned;lock(sync){if(activeSession!=sessionId||capturing||process==null)throw new IOException("speech_decode_session_missing");owned=process;}
  try{
   var reply=await SendAsync(owned,"decode",sessionId,new SpeechWorkerPayload{audioPath=request.AudioPath,language=request.Language,useVad=request.UseVad,timeoutMilliseconds=request.TimeoutMilliseconds},token).ConfigureAwait(false);
   if(reply.Kind!="final"&&reply.Kind!="failure")throw new InvalidDataException("speech_worker_decode_reply");
   string failure=reply.Payload==null?"speech_worker_decode_failed":Code(reply.Payload.failureCode);
   lock(sync){lastUsed=Now();SetStatus(status.State,status.IsWarm,refinement:failure);}
   return new SpeechRecognitionCandidate(reply.Payload==null?"":reply.Payload.text,reply.Kind=="final"?0:-1,failure,reply.Payload==null?null:reply.Payload.probabilities);
  }catch(Exception ex){if(!(ex is OperationCanceledException)){lock(sync){if(process==owned)FailOwned(owned,"speech_worker_decode_crashed",true);}throw;}}
  await CancelAsync(sessionId).ConfigureAwait(false);throw new OperationCanceledException(token);
 }
 internal Task CancelAsync(Guid sessionId){ISpeechWorkerProcess owned;TaskCompletionSource<int> completion;
  lock(sync){if(sessionId!=activeSession&&sessionId!=SpeechWorkerProtocol.ControlSession)return Task.FromResult(0);if(cancelling){activeSession=Guid.Empty;capturing=false;return cancelTask;}if(process==null){activeSession=Guid.Empty;capturing=false;return Task.FromResult(0);}
   activeSession=Guid.Empty;capturing=false;cancelling=true;cancelStarted=Now();owned=process;completion=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);cancelTask=completion.Task;
  }
  var operation=CancelOwnedAsync(owned,sessionId,completion);operation.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);return cancelTask;
 }
 internal async Task EnsureExitedAsync(CancellationToken token){
  token.ThrowIfCancellationRequested();await AwaitCancellation(ConfirmExitAsync(),token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
 }
 private async Task<int> ConfirmExitAsync(){
  await CancelAsync(SpeechWorkerProtocol.ControlSession).ConfigureAwait(false);
  lock(sync){if(process!=null&&!process.HasExited)throw new IOException("speech_worker_exit_unconfirmed");}return 0;
 }
 private async Task CancelOwnedAsync(ISpeechWorkerProcess owned,Guid session,TaskCompletionSource<int> completion){try{
  var reply=await SendAsync(owned,"cancel",session,null,CancellationToken.None).ConfigureAwait(false);if(reply.Kind!="cancelled")throw new InvalidDataException("speech_worker_cancel_failed");
 }catch(Exception){}finally{lock(sync){if(process==owned)Retire(owned,SpeechWorkerState.Released);cancelling=false;if(owned.HasExited)completion.TrySetResult(0);else completion.TrySetException(new IOException("speech_worker_exit_unconfirmed"));}}}
 internal void SetPreheat(bool enabled,bool realUiReady){bool release=false;lock(sync){if(disposed)return;bool disabling=preheat&&!enabled;if(enabled&&realUiReady&&(!preheat||!uiReady)){preheatDue=Now()+2000;preheatConsumed=false;}preheat=enabled;uiReady=realUiReady;
  if(!enabled){preheatConsumed=true;if(disabling){releaseAfterSession=activeSession!=Guid.Empty;release=activeSession==Guid.Empty&&process!=null;}}else releaseAfterSession=false;
 }if(release)CancelAsync(SpeechWorkerProtocol.ControlSession);}
 internal void CompleteSession(Guid sessionId){lock(sync){if(activeSession!=sessionId||capturing)return;activeSession=Guid.Empty;failures=0;lastUsed=Now();}Tick();}
 internal void RetryWorker(){lock(sync){if(disposed)return;if(process!=null&&!process.HasExited)throw new InvalidOperationException("speech_worker_still_owned");failures=0;SetStatus(SpeechWorkerState.Unloaded,capture:"",refinement:"");}}
 internal void Tick(){bool start=false;lock(sync){long current=Now();
  if(!disposed){if(process!=null&&cancelling){if(process.HasExited||current-cancelStarted>=2000)Retire(process,SpeechWorkerState.Released,"speech_worker_cancel_timeout");}
   else if(process!=null&&process.HasExited)FailOwned(process,"speech_worker_crashed");
   else if(process!=null&&status.State==SpeechWorkerState.Loading&&current-loadStarted>=20000)FailOwned(process,"speech_worker_load_timeout");
   else if(process!=null&&activeSession==Guid.Empty&&!cancelling&&status.State==SpeechWorkerState.Ready&&(releaseAfterSession||current-lastUsed>=120000||requests>=4000))Retire(process,SpeechWorkerState.Released);
   if(preheat&&uiReady&&!preheatConsumed&&current>=preheatDue){preheatConsumed=true;start=process==null&&failures<2;}
  }
 }CleanupDirectories();if(start){try{var task=EnsureReadyAsync(CancellationToken.None);task.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);}catch(Exception){}}}
 private void OnWorkerEvent(SpeechWorkerMessage message){Action<SpeechWorkerMessage> handler;lock(sync){if(disposed||message.InstanceId!=instance||message.SessionId!=activeSession)return;
  if(message.Kind=="stopped"){stoppedSession=message.SessionId;capturing=false;if(message.Payload!=null&&!string.IsNullOrEmpty(message.Payload.failureCode))SetStatus(status.State,status.IsWarm,capture:Code(message.Payload.failureCode));}handler=WorkerEvent;
 }if(handler!=null)handler(message);}
 private void FailOwned(ISpeechWorkerProcess owned,string code,bool refinement=false){failures++;if(loading!=null)loading.TrySetException(new IOException(code));activeSession=Guid.Empty;capturing=false;Retire(owned,SpeechWorkerState.Degraded,code,refinement);}
 private void Retire(ISpeechWorkerProcess owned,SpeechWorkerState state,string code=null,bool refinement=false){
  if(owned!=null){if(ownerCancellation!=null)ownerCancellation.Cancel();try{owned.TerminateOwnedJob();}catch(Exception){}if(!owned.HasExited){SetStatus(SpeechWorkerState.Degraded,capture:"speech_worker_exit_unconfirmed");return;}owned.EventReceived-=OnWorkerEvent;owned.Dispose();}
  if(loading!=null&&!loading.Task.IsCompleted)loading.TrySetException(new IOException(code??"speech_worker_released"));loading=null;process=null;instance=Guid.Empty;releaseAfterSession=false;if(ownerCancellation!=null){ownerCancellation.Dispose();ownerCancellation=null;}SetStatus(state,capture:refinement?null:code,refinement:refinement?code:null);
 }
 private static string Code(string code){if(string.IsNullOrEmpty(code))return "";if(code.Length>80)return "speech_worker_failed";foreach(char c in code)if(!((c>='a'&&c<='z')||c=='_'))return "speech_worker_failed";return code;}
 private static void ValidateDirectory(Guid session,string root){
  string full=Path.GetFullPath(root),temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
  if(session==Guid.Empty||session==SpeechWorkerProtocol.ControlSession||!full.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(full)!="Yike-voice-"+session.ToString("N")||!Directory.Exists(full))throw new InvalidDataException("speech_directory_unowned");
  for(var item=new DirectoryInfo(full);item!=null;item=item.Parent)if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("speech_directory_reparse");
 }
 internal bool TryCleanupOwnedDirectory(Guid session){string root;lock(sync){if(activeSession==session||!directories.TryGetValue(session,out root))return false;cleanupRequested.Add(session);if(cancelling||(process!=null&&status.State!=SpeechWorkerState.Ready&&!process.HasExited))return false;
  try{if(Directory.Exists(root)){ValidateDirectory(session,root);if(!PlainTree(root))return false;Directory.Delete(root,true);}directories.Remove(session);cleanupRequested.Remove(session);return true;}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
 }}
 private static bool PlainTree(string root){var pending=new Stack<string>();pending.Push(root);int entries=0;while(pending.Count>0){foreach(string path in Directory.EnumerateFileSystemEntries(pending.Pop())){if(++entries>1024)return false;var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)return false;if((attributes&FileAttributes.Directory)!=0)pending.Push(path);}}return true;}
 private void CleanupDirectories(){Guid[] sessions;lock(sync)sessions=cleanupRequested.Where(id=>id!=activeSession).ToArray();foreach(Guid session in sessions)TryCleanupOwnedDirectory(session);}
 public void Dispose(){lock(sync){if(disposed)return;disposed=true;if(timer!=null)timer.Dispose();activeSession=Guid.Empty;capturing=false;foreach(Guid session in directories.Keys)cleanupRequested.Add(session);Retire(process,SpeechWorkerState.Released);}CleanupDirectories();}
}
internal sealed class OwnedSpeechWorkerProcess:ISpeechWorkerProcess {
 private readonly object sync=new object();private readonly Func<Process,bool> launch;private Process process;private ProcessJob job;private SpeechWorkerTransport transport;private Guid instance;private bool disposed,started;
 internal OwnedSpeechWorkerProcess(Func<Process,bool> launch=null){this.launch=launch??(owned=>owned.Start());}
 public event Action<SpeechWorkerMessage> EventReceived;
 public bool HasExited{get{lock(sync){return process==null||!started||process.HasExited;}}}
 public void Start(string workerPath,Guid instanceId){lock(sync){
  if(disposed||process!=null)throw new InvalidOperationException("speech_worker_already_started");instance=instanceId;
  string full=Path.GetFullPath(workerPath);for(var parent=new FileInfo(full) as FileSystemInfo;parent!=null;parent=parent is FileInfo?(FileSystemInfo)((FileInfo)parent).Directory:((DirectoryInfo)parent).Parent)if((parent.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("speech_worker_path_reparse");
  ValidateExecutable(full);
  var info=new ProcessStartInfo(full,"--instance-id "+instanceId.ToString("D")+" --parent-pid "+Process.GetCurrentProcess().Id){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(full),RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
  process=new Process{StartInfo=info};if(!launch(process))throw new IOException("speech_worker_start_failed");started=true;job=ProcessJob.AttachOrTerminate(process);
  transport=new SpeechWorkerTransport(process.StandardOutput.BaseStream,process.StandardInput.BaseStream,process.StandardError.BaseStream,instanceId);transport.EventReceived+=message=>{var handler=EventReceived;if(handler!=null)handler(message);};
 }}
 private static void ValidateExecutable(string path){using(var stream=File.OpenRead(path))using(var reader=new BinaryReader(stream)){
  if(stream.Length<512||reader.ReadUInt16()!=0x5a4d)throw new IOException("speech_worker_image_invalid");stream.Position=0x3c;int offset=reader.ReadInt32();if(offset<0x40||offset>1048576||offset>stream.Length-256)throw new IOException("speech_worker_image_invalid");stream.Position=offset;
  if(reader.ReadUInt32()!=0x4550||reader.ReadUInt16()!=0x8664)throw new IOException("speech_worker_image_invalid");stream.Position=offset+24;if(reader.ReadUInt16()!=0x20b)throw new IOException("speech_worker_image_invalid");
 }}
 public Task<SpeechWorkerMessage> SendAsync(SpeechWorkerMessage message,CancellationToken token){lock(sync){if(disposed||!started||process==null||process.HasExited||transport==null||message.InstanceId!=instance)throw new IOException("speech_worker_not_owned");return transport.SendAsync(message,token);}}
 public void TerminateOwnedJob(){lock(sync){if(job!=null){job.Dispose();job=null;}if(started&&process!=null&&!process.HasExited){process.WaitForExit(1000);}}}
 public void Dispose(){lock(sync){if(disposed)return;TerminateOwnedJob();if(started&&process!=null&&!process.HasExited)throw new IOException("speech_worker_exit_unconfirmed");disposed=true;if(transport!=null){transport.Dispose();transport=null;}if(process!=null){process.Dispose();process=null;}}}
}
}
