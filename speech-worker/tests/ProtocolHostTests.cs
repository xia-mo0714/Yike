using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal static class ProtocolHostTests {
 internal static void Run(){
  using(var input=new TestPipe())using(var output=new MemoryStream())using(var started=new ManualResetEventSlim())using(var finish=new ManualResetEventSlim()){
   int shutdown=0,completed=0;
   var running=Task.Run(()=>ProtocolHost.Run(input,output,message=>{
    if(message.Kind=="start"){started.Set();if(!finish.Wait(3000))throw new Exception("test_dispatch_timeout");Interlocked.Exchange(ref completed,1);}
    if(message.Kind=="shutdown"){Interlocked.Exchange(ref shutdown,1);finish.Set();}
   },CancellationToken.None));
   try{
    SpeechWorkerProtocol.WriteAsync(input,Message("start"),65536,CancellationToken.None).GetAwaiter().GetResult();
    Check(started.Wait(3000),"HostStartsDispatch");input.Complete();
    Check(running.Wait(3000),"HostEofTerminates");
    Check(shutdown==1&&completed==1,"HostEofStopsAndJoinsDispatchBeforeReleasingResources");
   }finally{finish.Set();input.Complete();try{running.Wait(3000);}catch(AggregateException){}}
  }
  using(var input=new TestPipe())using(var output=new MemoryStream())using(var started=new ManualResetEventSlim())using(var finish=new ManualResetEventSlim()){
   int executions=0,cancelled=0;var command=Message("start");
   var running=Task.Run(()=>ProtocolHost.Run(input,output,message=>{
    if(message.Kind=="start"){Interlocked.Increment(ref executions);started.Set();finish.Wait(3000);}
    if(message.Kind=="cancel"){Interlocked.Exchange(ref cancelled,1);finish.Set();}
    if(message.Kind=="shutdown")finish.Set();
   },CancellationToken.None));
   try{
    SpeechWorkerProtocol.WriteAsync(input,command,65536,CancellationToken.None).GetAwaiter().GetResult();Check(started.Wait(3000),"HostDispatchBlocksOnlyExecutor");
    SpeechWorkerProtocol.WriteAsync(input,command,65536,CancellationToken.None).GetAwaiter().GetResult();
    var cancel=Message("cancel");cancel.InstanceId=command.InstanceId;cancel.SessionId=command.SessionId;
    SpeechWorkerProtocol.WriteAsync(input,cancel,65536,CancellationToken.None).GetAwaiter().GetResult();
    Check(finish.Wait(3000)&&cancelled==1,"HostCancelBypassesBlockedDecode");input.Complete();Check(running.Wait(3000),"HostCancelThenEofTerminates");
    Check(executions==1,"HostRejectsDuplicateExecution");output.Position=0;
    var failure=SpeechWorkerProtocol.ReadAsync(output,1048576,CancellationToken.None).GetAwaiter().GetResult();
    Check(failure.Kind=="failure"&&failure.Payload.failureCode=="worker_duplicate_request","HostRejectsDuplicateNonContentCode");
   }finally{finish.Set();input.Complete();try{running.Wait(3000);}catch(AggregateException){}}
  }
 }
 private static SpeechWorkerMessage Message(string kind){return new SpeechWorkerMessage{Version=1,InstanceId=Guid.NewGuid(),SessionId=Guid.NewGuid(),RequestId=Guid.NewGuid(),Kind=kind,Payload=new SpeechWorkerPayload()};}
 private static void Check(bool condition,string name){if(!condition)throw new Exception(name);}
 private sealed class TestPipe:Stream {
  private readonly BlockingCollection<byte[]> packets=new BlockingCollection<byte[]>();private byte[] current;private int position;
  internal void Complete(){packets.CompleteAdding();}
  public override bool CanRead{get{return true;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}
  public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
  public override void Flush(){}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}
  public override int Read(byte[] bytes,int offset,int count){return ReadAsync(bytes,offset,count,CancellationToken.None).GetAwaiter().GetResult();}
  public override Task<int> ReadAsync(byte[] bytes,int offset,int count,CancellationToken token){return Task.Run(()=>{if(current==null||position==current.Length){if(!packets.TryTake(out current,Timeout.Infinite,token))return 0;position=0;}int read=Math.Min(count,current.Length-position);Buffer.BlockCopy(current,position,bytes,offset,read);position+=read;return read;},token);}
  public override void Write(byte[] bytes,int offset,int count){var copy=new byte[count];Buffer.BlockCopy(bytes,offset,copy,0,count);packets.Add(copy);}
  public override Task WriteAsync(byte[] bytes,int offset,int count,CancellationToken token){token.ThrowIfCancellationRequested();Write(bytes,offset,count);return Task.FromResult(0);}
 }
}
}
