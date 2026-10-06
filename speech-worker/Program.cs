using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal static class SpeechWorkerProgram {
 internal static float[] ReadOwnedWave(string root,string path){
  if(string.IsNullOrWhiteSpace(root)||string.IsNullOrWhiteSpace(path))throw new InvalidDataException("worker_audio_unowned");
  string prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,full=Path.GetFullPath(path);
  if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("worker_audio_unowned");NativeLibraryLoader.AssertPlainPath(full);
  try{using(var file=File.OpenRead(full))using(var reader=new BinaryReader(file,Encoding.ASCII)){
   if(file.Length<44||file.Length>20200000||reader.ReadUInt32()!=0x46464952||reader.ReadUInt32()+8L!=file.Length||reader.ReadUInt32()!=0x45564157)throw new InvalidDataException("worker_wave_invalid");
   bool formatted=false;int format=0,bits=0,align=0;long data=-1;uint bytes=0;
   while(file.Position+8<=file.Length){uint kind=reader.ReadUInt32(),length=reader.ReadUInt32();long next=file.Position+length+(length%2);if(next>file.Length)throw new InvalidDataException("worker_wave_invalid");
    if(kind==0x20746d66){if(formatted||length<16)throw new InvalidDataException("worker_wave_invalid");formatted=true;format=reader.ReadUInt16();int channels=reader.ReadUInt16();uint rate=reader.ReadUInt32(),byteRate=reader.ReadUInt32();align=reader.ReadUInt16();bits=reader.ReadUInt16();
     if(channels!=1||rate!=16000||!((format==1&&bits==16)||(format==3&&bits==32))||align!=bits/8||byteRate!=rate*align)throw new InvalidDataException("worker_wave_format");
    }else if(kind==0x61746164){if(data>=0)throw new InvalidDataException("worker_wave_invalid");data=file.Position;bytes=length;}
    file.Position=next;
   }
   if(!formatted||data<0||bytes==0||bytes%align!=0||bytes/align>4800000)throw new InvalidDataException("worker_wave_invalid");
   var samples=new float[bytes/align];file.Position=data;for(int i=0;i<samples.Length;i++){float value=format==1?reader.ReadInt16()/32768f:reader.ReadSingle();if(float.IsNaN(value)||float.IsInfinity(value)||value<-1||value>1)throw new InvalidDataException("worker_wave_sample");samples[i]=value;}return samples;
  }}catch(EndOfStreamException){throw new InvalidDataException("worker_wave_truncated");}
 }
 private sealed class Operation {internal Guid Session;internal readonly CancellationTokenSource Cancel=new CancellationTokenSource();internal readonly TaskCompletionSource<int> Finished=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);}
 internal sealed class HostState:IDisposable {
  private readonly object sync=new object();private readonly HashSet<Guid> cancelled=new HashSet<Guid>();private readonly Stream output;private readonly Guid instance;private readonly string fileRoot;
  private readonly SpeechRuntimePaths paths=new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory);private readonly NativeDecodeQueue queue=new NativeDecodeQueue();
  private WhisperContext context;private Operation active;private bool stopping;private int initializations,loadMilliseconds;
  private readonly object captureGate=new object();private SpeechWorkerSession captureSession;private SpeechWorkerMessage startCommand,stopCommand;private string registeredRoot;
  private readonly Func<NativeLibraryLoader,SdlCaptureSession> createCapture;
  internal HostState(Stream output,Guid instance,string fileRoot,Func<NativeLibraryLoader,SdlCaptureSession> createCapture=null){this.output=output;this.instance=instance;this.fileRoot=fileRoot;this.createCapture=createCapture??(loader=>new SdlCaptureSession(loader));}
  private void Reply(SpeechWorkerMessage command,string kind,SpeechWorkerPayload payload){SpeechWorkerProtocol.WriteAsync(output,new SpeechWorkerMessage{Version=1,InstanceId=instance,SessionId=command.SessionId,RequestId=command.RequestId,Kind=kind,Payload=payload??new SpeechWorkerPayload()},SpeechWorkerProtocol.ResultLimit,CancellationToken.None).GetAwaiter().GetResult();}
  private SpeechWorkerPayload State(bool warm){return new SpeechWorkerPayload{contextInitializationCount=initializations,loadMilliseconds=loadMilliseconds,isWarm=warm};}
  internal void Dispatch(SpeechWorkerMessage command){
   if(command.InstanceId!=instance)throw new InvalidDataException("worker_instance_mismatch");
   if(command.Kind=="cancel"||command.Kind=="shutdown"){
    Operation operation;lock(sync){cancelled.Add(command.SessionId);if(command.Kind=="shutdown")stopping=true;operation=active;if(operation!=null&&(command.Kind=="shutdown"||operation.Session==command.SessionId))operation.Cancel.Cancel();else operation=null;}
    Task captureFinished=Task.FromResult(0);lock(captureGate){if(captureSession!=null&&(command.Kind=="shutdown"||startCommand.SessionId==command.SessionId)){captureSession.Cancel();captureFinished=captureSession.Completion;}}
    var tasks=operation==null?new[]{captureFinished}:new[]{captureFinished,operation.Finished.Task};
    Task.WhenAll(tasks).ContinueWith(t=>Reply(command,"cancelled",null),TaskScheduler.Default).ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
    return;
   }
   if(command.Kind=="hello"){Reply(command,"ready",State(context!=null));return;}
   if(command.Kind=="stop"){lock(captureGate){
    if(captureSession==null||startCommand.SessionId!=command.SessionId){Reply(command,"failure",new SpeechWorkerPayload{failureCode="capture_session_missing"});return;}
    stopCommand=command;captureSession.Stop(command.Payload==null?"manual":command.Payload.stopReason);Reply(command,"stopped",captureSession.LastStopped);return;
   }}
   if(command.Kind=="start"&&fileRoot!=null){Reply(command,"failure",new SpeechWorkerPayload{failureCode="file_test_capture_disabled"});return;}
   if(command.Kind!="preheat"&&command.Kind!="decode"&&command.Kind!="start"){Reply(command,"failure",new SpeechWorkerPayload{failureCode="worker_command_invalid"});return;}
   Operation current;lock(sync){if(stopping||cancelled.Contains(command.SessionId)){Reply(command,"failure",new SpeechWorkerPayload{failureCode="native_cancelled"});return;}if(active!=null)throw new InvalidOperationException("worker_operation_busy");current=new Operation{Session=command.SessionId};active=current;}
   try{
    if(command.Kind=="start")ValidateRecordingRoot(command);
    bool warm=context!=null;
    queue.Enqueue(()=>{
     current.Cancel.Token.ThrowIfCancellationRequested();
     if(context==null){var clock=Stopwatch.StartNew();context=WhisperContext.Open(paths);initializations++;loadMilliseconds=(int)Math.Min(int.MaxValue,clock.ElapsedMilliseconds);}
     current.Cancel.Token.ThrowIfCancellationRequested();
     if(command.Kind=="preheat"){Reply(command,"ready",State(warm));return;}
     if(command.Kind=="start"){if(!context.SupportsLanguage(command.Payload.language))throw new InvalidDataException("native_language_invalid");return;}
     string root=fileRoot;lock(captureGate){if(root==null&&startCommand!=null&&command.SessionId==startCommand.SessionId){
      string audio=Path.GetFullPath(command.Payload.audioPath);bool owned=string.Equals(audio,Path.Combine(registeredRoot,"recording.wav"),StringComparison.OrdinalIgnoreCase)||string.Equals(audio,Path.Combine(registeredRoot,"recording.enhanced.wav"),StringComparison.OrdinalIgnoreCase);
      if(captureSession.IsCapturing||!owned)throw new InvalidDataException("worker_audio_unowned");root=registeredRoot;
     }}
     if(root==null)throw new InvalidDataException("worker_audio_unowned");
     var payload=command.Payload;if(payload==null||payload.timeoutMilliseconds<1||payload.timeoutMilliseconds>300000)throw new InvalidDataException("worker_decode_arguments");
     using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(current.Cancel.Token)){
      deadline.CancelAfter(payload.timeoutMilliseconds);var candidate=context.Decode(ReadOwnedWave(root,payload.audioPath),payload.language,false,payload.useVad,deadline.Token);
      current.Cancel.Token.ThrowIfCancellationRequested();var result=State(warm);result.text=candidate.Text;result.probabilities=candidate.OrdinaryTokenProbabilities;result.failureCode=candidate.FailureCode;Reply(command,candidate.ExitCode==0?"final":"failure",result);
     }
    },current.Cancel.Token).GetAwaiter().GetResult();
    if(command.Kind=="start"){lock(captureGate){
     current.Cancel.Token.ThrowIfCancellationRequested();
     if(captureSession!=null){if(captureSession.IsCapturing)throw new InvalidOperationException("capture_session_busy");captureSession.Dispose();}
     startCommand=command;stopCommand=null;registeredRoot=Path.GetFullPath(command.Payload.audioPath);
     captureSession=new SpeechWorkerSession(createCapture(context.Libraries),(samples,language,token)=>{
      SpeechRecognitionCandidate result=null;queue.Enqueue(()=>result=context.Decode(samples,language,true,false,token),token).GetAwaiter().GetResult();return result;
     });
     captureSession.EventReceived+=message=>{
      if(message.Kind=="stopped"&&stopCommand!=null)return;Reply(startCommand,message.Kind,message.Payload);
     };
     try{captureSession.Start(command.SessionId,command.Payload.language,command.Payload.deviceName,registeredRoot);current.Cancel.Token.ThrowIfCancellationRequested();}
     catch{captureSession.Cancel();throw;}
    }}
   }catch(OperationCanceledException){Reply(command,"failure",new SpeechWorkerPayload{failureCode="native_cancelled"});}
   catch(InvalidDataException ex){Reply(command,"failure",new SpeechWorkerPayload{failureCode=SafeCode(ex.Message)});}
   catch(Exception){Reply(command,"failure",new SpeechWorkerPayload{failureCode="native_operation_failed"});}
   finally{lock(sync){active=null;current.Cancel.Dispose();current.Finished.TrySetResult(0);}}
  }
  private static void ValidateRecordingRoot(SpeechWorkerMessage command){
   var payload=command.Payload;if(payload==null||string.IsNullOrWhiteSpace(payload.audioPath))throw new InvalidDataException("capture_session_arguments");
   string root=Path.GetFullPath(payload.audioPath);SessionWaveWriter.ValidateRoot(root);
   if(command.SessionId==SpeechWorkerProtocol.ControlSession||Path.GetFileName(root)!="Yike-voice-"+command.SessionId.ToString("N")||!SpeechLanguage.IsCode(payload.language))throw new InvalidDataException("capture_session_arguments");
  }
  private static string SafeCode(string code){foreach(char c in code)if(!((c>='a'&&c<='z')||c=='_'))return "native_operation_failed";return code.Length<80?code:"native_operation_failed";}
  public void Dispose(){lock(sync){stopping=true;if(active!=null)active.Cancel.Cancel();}lock(captureGate){if(captureSession!=null){captureSession.Dispose();captureSession=null;}}queue.Dispose();if(context!=null)context.Dispose();}
 }
 internal static int Main(string[] args){
  try{
   Guid instance=Guid.Empty;int parentId=0;string root=null;
   for(int i=0;i<args.Length;i+=2){if(i+1>=args.Length)return 2;switch(args[i]){case "--instance-id":if(!Guid.TryParse(args[i+1],out instance))return 2;break;case "--parent-pid":if(!int.TryParse(args[i+1],out parentId))return 2;break;case "--file-test-root":root=Path.GetFullPath(args[i+1]);break;default:return 2;}}
   if(instance==Guid.Empty||parentId<=0||parentId==Process.GetCurrentProcess().Id)return 2;
   if(root!=null){Guid owner;string leaf=Path.GetFileName(root);string temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if(!root.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)||!leaf.StartsWith("Yike-voice-",StringComparison.Ordinal)||!Guid.TryParseExact(leaf.Substring(11),"N",out owner)||!Directory.Exists(root))return 2;NativeLibraryLoader.AssertPlainPath(root);
   }
   using(var parent=Process.GetProcessById(parentId)){
    DateTime started=parent.StartTime;
    using(var watch=new Timer(_=>{try{using(var observed=Process.GetProcessById(parentId))if(observed.HasExited||observed.StartTime!=started)Environment.Exit(0);}catch(ArgumentException){Environment.Exit(0);}catch(InvalidOperationException){Environment.Exit(0);}},null,500,500))
    using(var input=Console.OpenStandardInput())using(var output=Console.OpenStandardOutput())using(var state=new HostState(output,instance,root))ProtocolHost.Run(input,output,state.Dispatch,CancellationToken.None);
   }
   return 0;
  }catch(Exception){return 1;}
 }
}
}
