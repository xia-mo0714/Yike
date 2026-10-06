using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal sealed class NativeDecodeQueue:IDisposable {
 private sealed class Work {internal Action Action;internal CancellationToken Token;internal readonly TaskCompletionSource<int> Result=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);}
 private readonly BlockingCollection<Work> waiting=new BlockingCollection<Work>(1);private readonly Thread thread;private int disposed;
 internal NativeDecodeQueue(){thread=new Thread(Run){IsBackground=true,Name="Yike native speech decode"};thread.Start();}
 internal Task Enqueue(Action action,CancellationToken token){
  if(action==null)throw new ArgumentNullException("action");if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException("NativeDecodeQueue");
  var work=new Work{Action=action,Token=token};if(token.IsCancellationRequested)work.Result.SetCanceled();else if(!waiting.TryAdd(work))work.Result.SetException(new InvalidOperationException("native_decode_queue_busy"));return work.Result.Task;
 }
 private void Run(){foreach(var work in waiting.GetConsumingEnumerable()){
  if(Volatile.Read(ref disposed)!=0||work.Token.IsCancellationRequested){work.Result.TrySetCanceled();continue;}
  try{work.Action();work.Token.ThrowIfCancellationRequested();work.Result.TrySetResult(0);}catch(OperationCanceledException){work.Result.TrySetCanceled();}catch(Exception ex){work.Result.TrySetException(ex);}
 }}
 public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;waiting.CompleteAdding();if(Thread.CurrentThread==thread)throw new InvalidOperationException("native_decode_self_dispose");thread.Join();waiting.Dispose();}
}
}
