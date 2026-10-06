using System;
using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal interface ISpeechWorkerProcess:IDisposable {
 void Start(string workerPath,Guid instanceId);
 Task<SpeechWorkerMessage> SendAsync(SpeechWorkerMessage message,CancellationToken token);
 event Action<SpeechWorkerMessage> EventReceived;
 bool HasExited{get;}
 void TerminateOwnedJob();
}
}
