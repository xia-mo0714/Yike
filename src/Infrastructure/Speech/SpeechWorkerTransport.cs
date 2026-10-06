using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal sealed class SpeechWorkerTransport:IDisposable {
 private sealed class Pending {internal SpeechWorkerMessage Command;internal readonly TaskCompletionSource<SpeechWorkerMessage> Completion=new TaskCompletionSource<SpeechWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);internal CancellationTokenRegistration Cancellation;}
 private readonly object sync=new object();
 private readonly Stream input,output,error;
 private readonly Guid instance;
 private readonly Dictionary<Guid,Pending> pending=new Dictionary<Guid,Pending>();
 private readonly HashSet<Guid> issued=new HashSet<Guid>();
 private readonly SemaphoreSlim writer=new SemaphoreSlim(1,1);
 private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
 private Guid activeSession,activeCaptureRequest;
 private bool closed;
 private readonly Task readerTask,errorTask;
 internal SpeechWorkerTransport(Stream input,Stream output,Stream error,Guid instanceId){
  if(input==null||output==null||error==null||instanceId==Guid.Empty)throw new ArgumentException("speech_transport_arguments");
  this.input=input;this.output=output;this.error=error;instance=instanceId;
  readerTask=Task.Run((Func<Task>)ReadLoop);errorTask=Task.Run((Func<Task>)DrainError);
 }
 internal event Action<SpeechWorkerMessage> EventReceived;
 internal Task<SpeechWorkerMessage> SendAsync(SpeechWorkerMessage message,CancellationToken token){
  SpeechWorkerProtocol.Validate(message);if(!SpeechWorkerProtocol.IsCommand(message.Kind)||message.InstanceId!=instance)throw new InvalidDataException("speech_transport_wrong_command");
  var entry=new Pending {Command=message};
  lock(sync){
   if(closed)throw new IOException("speech_worker_closed");
   if(token.IsCancellationRequested){entry.Completion.SetCanceled();return entry.Completion.Task;}
   if(issued.Contains(message.RequestId))throw new InvalidOperationException("speech_duplicate_request");
   if(issued.Count>=4096||pending.Count>=8)throw new InvalidOperationException("speech_request_limit");
   if(message.Kind=="cancel"||message.Kind=="shutdown"){
    if(message.Kind=="shutdown"||activeSession==message.SessionId){activeSession=activeCaptureRequest=Guid.Empty;}
    foreach(Pending old in pending.Values.Where(p=>message.Kind=="shutdown"||p.Command.SessionId==message.SessionId).ToArray())CancelPending(old);
   }
   if(message.Kind=="start"){activeSession=message.SessionId;activeCaptureRequest=message.RequestId;}
   issued.Add(message.RequestId);pending.Add(message.RequestId,entry);
  }
  entry.Cancellation=token.Register(()=>CancelPending(entry));if(entry.Completion.Task.IsCompleted)entry.Cancellation.Dispose();
  // Never dispose a registration while holding sync: its callback may be waiting for sync.
  entry.Completion.Task.ContinueWith(t=>entry.Cancellation.Dispose(),CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
  var write=WriteCommand(entry,token);write.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
  return entry.Completion.Task;
 }
 private void CancelPending(Pending entry){
  lock(sync){
   Pending current;if(!pending.TryGetValue(entry.Command.RequestId,out current)||!object.ReferenceEquals(entry,current))return;
   pending.Remove(entry.Command.RequestId);
   if(entry.Command.Kind=="start"&&activeCaptureRequest==entry.Command.RequestId)activeSession=activeCaptureRequest=Guid.Empty;
   entry.Completion.TrySetCanceled();
  }
 }
 private async Task WriteCommand(Pending entry,CancellationToken token){
  using(var combined=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token)){
   bool acquired=false;
   try{await writer.WaitAsync(combined.Token).ConfigureAwait(false);acquired=true;
    if(entry.Completion.Task.IsCompleted)return;
    await SpeechWorkerProtocol.WriteAsync(output,entry.Command,SpeechWorkerProtocol.CommandLimit,combined.Token).ConfigureAwait(false);
   }catch(OperationCanceledException){CancelPending(entry);}
   catch(Exception){Fail("speech_worker_write_failed");}
   finally{if(acquired)writer.Release();}
  }
 }
 private static bool Matches(Pending entry,SpeechWorkerMessage message){
  if(entry.Command.SessionId!=message.SessionId)return false;
  if(message.Kind=="failure")return true;
  switch(entry.Command.Kind){
   case "hello":case "preheat":return message.Kind=="ready";
   case "start":return message.Kind=="capture_started";
   case "stop":return message.Kind=="stopped";
   case "decode":return message.Kind=="final";
   case "cancel":case "shutdown":return message.Kind=="cancelled";
   default:return false;
  }
 }
 private async Task ReadLoop(){
  try{while(!lifetime.IsCancellationRequested){
   var message=await SpeechWorkerProtocol.ReadAsync(input,SpeechWorkerProtocol.ResultLimit,lifetime.Token).ConfigureAwait(false);
   if(message==null){Fail("speech_worker_eof");return;}
   if(!SpeechWorkerProtocol.IsEvent(message.Kind)){Fail("speech_protocol_wrong_direction");return;}
   Pending completed=null;bool deliver=false;
   lock(sync){
    if(closed)return;if(message.InstanceId!=instance)continue;
    Pending request;if(pending.TryGetValue(message.RequestId,out request)&&Matches(request,message)){pending.Remove(message.RequestId);completed=request;}
    else if(message.SessionId==activeSession&&message.RequestId==activeCaptureRequest&&(message.Kind=="draft"||message.Kind=="failure"||message.Kind=="stopped"))deliver=true;
    if(message.Kind=="stopped"&&(completed!=null||deliver))activeSession=activeCaptureRequest=Guid.Empty;
   }
   if(completed!=null){completed.Cancellation.Dispose();completed.Completion.TrySetResult(message);}
   else if(deliver){var handler=EventReceived;if(handler!=null)handler(message);}
  }}catch(OperationCanceledException){if(!lifetime.IsCancellationRequested)Fail("speech_worker_read_cancelled");}
  catch(Exception){if(!lifetime.IsCancellationRequested)Fail("speech_worker_protocol_failed");}
 }
 private async Task DrainError(){
  byte[] bytes=new byte[4096];try{while(!lifetime.IsCancellationRequested&&await error.ReadAsync(bytes,0,bytes.Length,lifetime.Token).ConfigureAwait(false)>0)Array.Clear(bytes,0,bytes.Length);}
  catch(Exception){if(!lifetime.IsCancellationRequested)Fail("speech_worker_stderr_failed");}
 }
 private void Fail(string code){Close(new IOException(code));}
 private void Close(Exception failure){
  Pending[] waiting;
  lock(sync){if(closed)return;closed=true;activeSession=activeCaptureRequest=Guid.Empty;waiting=pending.Values.ToArray();pending.Clear();}
  lifetime.Cancel();
  foreach(var entry in waiting){entry.Cancellation.Dispose();if(failure==null)entry.Completion.TrySetCanceled();else entry.Completion.TrySetException(failure);}
  foreach(var stream in new[]{input,output,error})try{stream.Dispose();}catch(IOException){}
 }
 public void Dispose(){Close(null);}
 internal Task Completion{get{return Task.WhenAll(readerTask,errorTask);}}
}
}
