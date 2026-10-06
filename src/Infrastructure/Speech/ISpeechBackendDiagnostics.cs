using System;
namespace WindowsTranslator {
internal interface ISpeechBackendDiagnostics {SpeechWorkerStatus Status{get;}}
internal interface ISpeechBackendCompletion {
 event Action<string> CaptureEnded;
 event Action<string> Finalized;
}
internal interface ISpeechCaptureMeter:IDisposable {
 string DeviceName{get;}string[] CaptureNames{get;}
 double ReadPeak();bool DefaultDeviceChanged();
}
}
