using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsTranslator {
public static partial class Tests {
 private const string ProtocolJson="{\"Version\":1,\"InstanceId\":\"11111111-1111-1111-1111-111111111111\",\"SessionId\":\"22222222-2222-2222-2222-222222222222\",\"RequestId\":\"33333333-3333-3333-3333-333333333333\",\"Kind\":\"draft\",\"Payload\":{\"text\":\"中文𠮷\"}}";
 internal static void RunSpeechWorkerProtocolTests(List<string> lines){
  using(var input=new FragmentedProtocolStream(Encoding.UTF8.GetBytes(ProtocolJson+"\n"))){
   var message=SpeechWorkerProtocol.ReadAsync(input,65536,CancellationToken.None).GetAwaiter().GetResult();
   Check(message!=null&&message.Payload!=null&&message.Payload.text=="中文𠮷","SplitUtf8ControlMessageRemainsIntact");
  }
  foreach(int limit in new[]{65536,1048576}){
   int prefix=Encoding.UTF8.GetByteCount(ProtocolJson);
   using(var input=new MemoryStream(Encoding.UTF8.GetBytes(ProtocolJson+new string(' ',limit-prefix)+"\n")))
    Check(SpeechWorkerProtocol.ReadAsync(input,limit,CancellationToken.None).GetAwaiter().GetResult()!=null,"ExactUtf8ByteBoundaryAccepted");
   RejectProtocol(ProtocolJson+new string(' ',limit-prefix+1)+"\n",limit,"OversizeUtf8LineRejected");
   RejectProtocol(new string('x',limit+1),limit,"UnterminatedOversizeLineRejectedBeforeEof");
  }
  foreach(string invalid in new[]{"garbage\n","{}\n",ProtocolJson.Replace("\"Version\":1","\"Version\":2")+"\n",ProtocolJson.Replace("11111111-1111-1111-1111-111111111111",Guid.Empty.ToString())+"\n",ProtocolJson.Replace("\"draft\"","\"unknown\"")+"\n",ProtocolJson})
   RejectProtocol(invalid,65536,"MalformedMessageFailsClosed");
  using(var input=new MemoryStream(new byte[]{0xc0,0xaf,10})){
   bool rejected=false;try{SpeechWorkerProtocol.ReadAsync(input,65536,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException){rejected=true;}
   Check(rejected,"InvalidUtf8IsNotReplacementDecoded");
  }
  using(var input=new MemoryStream(Encoding.UTF8.GetBytes(ProtocolJson+"\n"+ProtocolJson+"\n"))){
   Check(SpeechWorkerProtocol.ReadAsync(input,65536,CancellationToken.None).GetAwaiter().GetResult()!=null&&SpeechWorkerProtocol.ReadAsync(input,65536,CancellationToken.None).GetAwaiter().GetResult()!=null,"BufferedReaderPreservesNextLine");
   Check(SpeechWorkerProtocol.ReadAsync(input,65536,CancellationToken.None).GetAwaiter().GetResult()==null,"CleanEofTerminatesReader");
  }
  using(var input=new MemoryStream()){
   var message=ProtocolMessage("draft",Guid.Parse("11111111-1111-1111-1111-111111111111"),Guid.NewGuid(),Guid.NewGuid());message.Payload.text="中文𠮷";
   SpeechWorkerProtocol.WriteAsync(input,message,1048576,CancellationToken.None).GetAwaiter().GetResult();input.Position=0;
   Check(SpeechWorkerProtocol.ReadAsync(input,1048576,CancellationToken.None).GetAwaiter().GetResult().Payload.text=="中文𠮷","ProtocolWriterDoesNotEscapeOrLoseUnicode");
  }
  TestConcurrentProtocolWriters();TestTransportGenerationAndCancel();TestAutomaticCaptureStop();TestTransportBrokenPipe();
  lines.Add("PASS speech protocol: UTF-8 byte bounds, fragmented input, malformed/oversize rejection, old-generation filtering and priority cancellation");
 }
 private static void RejectProtocol(string data,int limit,string name){
  using(var input=new MemoryStream(Encoding.UTF8.GetBytes(data))){bool rejected=false;try{SpeechWorkerProtocol.ReadAsync(input,limit,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException){rejected=true;}Check(rejected,name);}
 }
 private static SpeechWorkerMessage ProtocolMessage(string kind,Guid instance,Guid session,Guid request){return new SpeechWorkerMessage {Version=1,InstanceId=instance,SessionId=session,RequestId=request,Kind=kind,Payload=new SpeechWorkerPayload()};}
 private sealed class FragmentedProtocolStream:MemoryStream {
  internal FragmentedProtocolStream(byte[] bytes):base(bytes){}
  public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token){return base.ReadAsync(buffer,offset,Math.Min(1,count),token);}
 }
 private sealed class FragmentedProtocolWriter:MemoryStream {
  public override async Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token){
   for(int i=0;i<count;i++){token.ThrowIfCancellationRequested();lock(this){WriteByte(buffer[offset+i]);}await Task.Yield();}
  }
 }
 private static void TestConcurrentProtocolWriters(){
  using(var stream=new FragmentedProtocolWriter()){
   var tasks=new List<Task>();Guid instance=Guid.NewGuid(),session=Guid.NewGuid();
   for(int i=0;i<4;i++)tasks.Add(SpeechWorkerProtocol.WriteAsync(stream,ProtocolMessage("draft",instance,session,Guid.NewGuid()),1048576,CancellationToken.None));
   Task.WhenAll(tasks).GetAwaiter().GetResult();stream.Position=0;
   for(int i=0;i<4;i++){SpeechWorkerMessage message=null;try{message=SpeechWorkerProtocol.ReadAsync(stream,1048576,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException){}
    Check(message!=null,"ConcurrentProtocolWritesRemainWholeLines");}
  }
 }
 private sealed class ProtocolTestPipe:Stream {
  private readonly BlockingCollection<byte[]> packets=new BlockingCollection<byte[]>();
  private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
  private byte[] current;private int position;
  internal void Complete(){packets.CompleteAdding();}
  public override bool CanRead {get{return true;}}public override bool CanWrite {get{return true;}}public override bool CanSeek {get{return false;}}
  public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
  public override void Flush(){}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}
  public override int Read(byte[] buffer,int offset,int count){return ReadAsync(buffer,offset,count,CancellationToken.None).GetAwaiter().GetResult();}
  public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token){return Task.Run(()=>{using(var combined=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token)){
   if(current==null||position==current.Length){if(!packets.TryTake(out current,Timeout.Infinite,combined.Token))return 0;position=0;}
   int read=Math.Min(count,current.Length-position);Buffer.BlockCopy(current,position,buffer,offset,read);position+=read;return read;
  }},token);}
  public override void Write(byte[] buffer,int offset,int count){byte[] bytes=new byte[count];Buffer.BlockCopy(buffer,offset,bytes,0,count);packets.Add(bytes);}
  public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token){token.ThrowIfCancellationRequested();Write(buffer,offset,count);return Task.FromResult(0);}
  protected override void Dispose(bool disposing){if(disposing){lifetime.Cancel();Complete();}base.Dispose(disposing);}
 }
 private static void WaitProtocol(Task task,string name){Check(task.Wait(3000),name);}
 private static void TestTransportGenerationAndCancel(){
  Guid instance=Guid.NewGuid(),session=Guid.NewGuid();
  using(var input=new ProtocolTestPipe())using(var output=new ProtocolTestPipe())using(var error=new ProtocolTestPipe())using(var transport=new SpeechWorkerTransport(input,output,error,instance)){
   int events=0;transport.EventReceived+=m=>Interlocked.Increment(ref events);
   var start=ProtocolMessage("start",instance,session,Guid.NewGuid());var pending=transport.SendAsync(start,CancellationToken.None);
   var received=SpeechWorkerProtocol.ReadAsync(output,65536,CancellationToken.None).GetAwaiter().GetResult();Check(received.RequestId==start.RequestId,"StartRequestWrittenExactlyOnce");
   bool duplicate=false;try{transport.SendAsync(start,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidOperationException){duplicate=true;}
   Check(duplicate,"DuplicateRequestDoesNotExecuteTwice");
   var ready=ProtocolMessage("capture_started",instance,session,start.RequestId);SpeechWorkerProtocol.WriteAsync(input,ready,1048576,CancellationToken.None).GetAwaiter().GetResult();WaitProtocol(pending,"CaptureStartedCompletesStartRequest");
   var decode=ProtocolMessage("decode",instance,session,Guid.NewGuid());var finalPending=transport.SendAsync(decode,CancellationToken.None);
   Check(SpeechWorkerProtocol.ReadAsync(output,65536,CancellationToken.None).GetAwaiter().GetResult().Kind=="decode","DecodeSent");
   var cancel=ProtocolMessage("cancel",instance,session,Guid.NewGuid());var cancelPending=transport.SendAsync(cancel,CancellationToken.None);
   Check(SpeechWorkerProtocol.ReadAsync(output,65536,CancellationToken.None).GetAwaiter().GetResult().Kind=="cancel","CancelBypassesPendingDraft");
   SpeechWorkerProtocol.WriteAsync(input,ProtocolMessage("draft",instance,session,start.RequestId),1048576,CancellationToken.None).GetAwaiter().GetResult();
   SpeechWorkerProtocol.WriteAsync(input,ProtocolMessage("final",Guid.NewGuid(),session,decode.RequestId),1048576,CancellationToken.None).GetAwaiter().GetResult();
   SpeechWorkerProtocol.WriteAsync(input,ProtocolMessage("final",instance,Guid.NewGuid(),decode.RequestId),1048576,CancellationToken.None).GetAwaiter().GetResult();
   SpeechWorkerProtocol.WriteAsync(input,ProtocolMessage("cancelled",instance,session,cancel.RequestId),1048576,CancellationToken.None).GetAwaiter().GetResult();WaitProtocol(cancelPending,"CancelAcknowledgedWithoutWaitingForFinal");
   Check(events==0&&finalPending.IsCanceled,"CancelInvalidatesLateDraftAndOldFinal");
   using(var stop=new CancellationTokenSource()){
    var next=ProtocolMessage("start",instance,Guid.NewGuid(),Guid.NewGuid());var nextPending=transport.SendAsync(next,stop.Token);
    SpeechWorkerProtocol.ReadAsync(output,65536,CancellationToken.None).GetAwaiter().GetResult();stop.Cancel();
    Check(nextPending.IsCanceled,"CancelledRequestCompletesWithoutWorkerResponse");
   }
  }
 }
 private static void TestAutomaticCaptureStop(){
  Guid instance=Guid.NewGuid(),session=Guid.NewGuid();
  using(var input=new ProtocolTestPipe())using(var output=new ProtocolTestPipe())using(var error=new ProtocolTestPipe())using(var transport=new SpeechWorkerTransport(input,output,error,instance)){
   int stopped=0,drafts=0;transport.EventReceived+=m=>{if(m.Kind=="stopped")Interlocked.Increment(ref stopped);if(m.Kind=="draft")Interlocked.Increment(ref drafts);};
   var start=ProtocolMessage("start",instance,session,Guid.NewGuid());var pending=transport.SendAsync(start,CancellationToken.None);SpeechWorkerProtocol.ReadAsync(output,65536,CancellationToken.None).GetAwaiter().GetResult();
   SpeechWorkerProtocol.WriteAsync(input,ProtocolMessage("capture_started",instance,session,start.RequestId),1048576,CancellationToken.None).GetAwaiter().GetResult();WaitProtocol(pending,"AutomaticStopTestCaptureStarts");
   SpeechWorkerProtocol.WriteAsync(input,ProtocolMessage("stopped",instance,session,start.RequestId),1048576,CancellationToken.None).GetAwaiter().GetResult();Check(SpinWait.SpinUntil(()=>Volatile.Read(ref stopped)==1,3000),"AutomaticDurationOrDisconnectStopIsDelivered");
   SpeechWorkerProtocol.WriteAsync(input,ProtocolMessage("draft",instance,session,start.RequestId),1048576,CancellationToken.None).GetAwaiter().GetResult();
   var hello=ProtocolMessage("hello",instance,SpeechWorkerProtocol.ControlSession,Guid.NewGuid());var barrier=transport.SendAsync(hello,CancellationToken.None);SpeechWorkerProtocol.ReadAsync(output,65536,CancellationToken.None).GetAwaiter().GetResult();SpeechWorkerProtocol.WriteAsync(input,ProtocolMessage("ready",instance,hello.SessionId,hello.RequestId),1048576,CancellationToken.None).GetAwaiter().GetResult();WaitProtocol(barrier,"AutomaticStopLateDraftBarrier");
   Check(drafts==0,"StoppedCaptureDropsLateDraft");
  }
 }
 private static void TestTransportBrokenPipe(){
  using(var input=new ProtocolTestPipe())using(var output=new ProtocolTestPipe())using(var error=new ProtocolTestPipe())using(var transport=new SpeechWorkerTransport(input,output,error,Guid.Parse("11111111-1111-1111-1111-111111111111"))){
   var command=ProtocolMessage("hello",Guid.Parse("11111111-1111-1111-1111-111111111111"),Guid.Parse("00000000-0000-0000-0000-000000000001"),Guid.NewGuid());
   var pending=transport.SendAsync(command,CancellationToken.None);input.Complete();
   Check(((IAsyncResult)pending).AsyncWaitHandle.WaitOne(3000)&&pending.IsFaulted,"BrokenPipeCompletesAllWaiters");
   bool failed=false;try{pending.GetAwaiter().GetResult();}catch(IOException){failed=true;}Check(failed,"EofReportsNonContentFailure");
  }
 }
}
}
