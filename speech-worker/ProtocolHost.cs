using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsTranslator {
internal static class ProtocolHost {
 internal static void Run(Stream input,Stream output,Action<SpeechWorkerMessage> dispatch,CancellationToken token){
  using(var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token))using(var work=new BlockingCollection<SpeechWorkerMessage>(1))using(var writer=new SemaphoreSlim(1,1)){
   var seen=new HashSet<Guid>();SpeechWorkerMessage last=null;bool shutdownDispatched=false;
   Action<SpeechWorkerMessage,string> failure=(command,code)=>{
    var message=new SpeechWorkerMessage {Version=1,InstanceId=command.InstanceId,SessionId=command.SessionId,RequestId=command.RequestId,Kind="failure",Payload=new SpeechWorkerPayload {failureCode=code}};
    writer.Wait(lifetime.Token);try{SpeechWorkerProtocol.WriteAsync(output,message,SpeechWorkerProtocol.ResultLimit,lifetime.Token).GetAwaiter().GetResult();}finally{writer.Release();}
   };
   var executor=Task.Run(()=>{try{foreach(var command in work.GetConsumingEnumerable(lifetime.Token))try{dispatch(command);}catch(Exception){if(!lifetime.IsCancellationRequested)failure(command,"worker_dispatch_failed");}}catch(OperationCanceledException){}},CancellationToken.None);
   try{while(!lifetime.IsCancellationRequested){
    var command=SpeechWorkerProtocol.ReadAsync(input,SpeechWorkerProtocol.CommandLimit,lifetime.Token).GetAwaiter().GetResult();if(command==null)break;
    if(!SpeechWorkerProtocol.IsCommand(command.Kind))throw new InvalidDataException("speech_protocol_wrong_direction");
    if(last!=null&&last.InstanceId!=command.InstanceId)throw new InvalidDataException("speech_protocol_wrong_instance");
    last=command;
    if(!seen.Add(command.RequestId)){failure(command,"worker_duplicate_request");continue;}
    if(seen.Count>4096)throw new InvalidDataException("worker_request_limit");
    if(command.Kind=="stop"||command.Kind=="cancel"||command.Kind=="shutdown"){
     if(command.Kind=="shutdown")shutdownDispatched=true;
     dispatch(command);if(shutdownDispatched)break;
    }
    else if(!work.TryAdd(command))failure(command,"worker_busy");
   }}finally{
    lifetime.Cancel();work.CompleteAdding();try{input.Dispose();}catch(IOException){}
    try{if(last!=null&&!shutdownDispatched)dispatch(new SpeechWorkerMessage {Version=1,InstanceId=last.InstanceId,SessionId=SpeechWorkerProtocol.ControlSession,RequestId=Guid.NewGuid(),Kind="shutdown",Payload=new SpeechWorkerPayload()});}
    finally{
     // The owner aborts decode on shutdown. Do not free its native context until the executor has left it.
     // If native abort fails, the parent terminates this worker's Job; resources are never freed under a running call.
     executor.GetAwaiter().GetResult();
    }
   }
  }
 }
}
}
