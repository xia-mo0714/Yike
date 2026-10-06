namespace WindowsTranslator {
internal enum SpeechWorkerState {Unloaded,Loading,Ready,Released,Degraded}
internal sealed class SpeechWorkerStatus {
 internal SpeechWorkerState State{get;private set;}
 internal bool IsWarm{get;private set;}
 internal int LoadMilliseconds{get;private set;}
 internal int CaptureReadyMilliseconds{get;private set;}
 internal string CaptureFailureCode{get;private set;}
 internal string RefinementFailureCode{get;private set;}
 internal SpeechWorkerStatus(SpeechWorkerState state,bool warm=false,int load=0,int captureReady=0,string captureFailure="",string refinementFailure=""){State=state;IsWarm=warm;LoadMilliseconds=load;CaptureReadyMilliseconds=captureReady;CaptureFailureCode=captureFailure??"";RefinementFailureCode=refinementFailure??"";}
}
}
