using System;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal sealed class WorkerSpeechDecoder:ISpeechFinalDecoder {
 private readonly SpeechWorkerManager manager;private readonly Guid session;private readonly ISpeechFinalDecoder cli;private bool failed;
 internal WorkerSpeechDecoder(SpeechWorkerManager manager,Guid sessionId,ISpeechFinalDecoder cli=null,bool workerFailed=false){this.manager=manager;session=sessionId;this.cli=cli??new CliSpeechDecoder(null,new WhisperVadConfiguration());failed=workerFailed;}
 public async Task<SpeechRecognitionCandidate> DecodeAsync(SpeechDecodeRequest request,CancellationToken token){
  token.ThrowIfCancellationRequested();
  if(failed){await manager.EnsureExitedAsync(token).ConfigureAwait(false);token.ThrowIfCancellationRequested();return await cli.DecodeAsync(request,token).ConfigureAwait(false);}
  try{return await manager.DecodeAsync(session,request,token).ConfigureAwait(false);}
  catch(OperationCanceledException){throw;}
  catch(Exception){token.ThrowIfCancellationRequested();failed=true;}
  // This request consumes exactly one parent-reserved attempt. A CLI fallback
  // can only run on the next request, after owned native execution has exited.
  await manager.EnsureExitedAsync(token).ConfigureAwait(false);
  return new SpeechRecognitionCandidate("",-1,"speech_worker_decode_crashed",null);
 }
}
}
