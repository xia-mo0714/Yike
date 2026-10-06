using System;
using System.IO;
namespace WindowsTranslator {
internal sealed class SpeechRuntimePaths {
 internal string RuntimeRoot{get;private set;}internal string ModelPath{get;private set;}internal string VadPath{get;private set;}internal string WorkerPath{get;private set;}internal string ApplicationRoot{get;private set;}
 internal SpeechRuntimePaths(string applicationRoot){ApplicationRoot=Path.GetFullPath(applicationRoot);RuntimeRoot=Path.Combine(ApplicationRoot,"whisper-runtime","Release");ModelPath=Path.Combine(ApplicationRoot,"whisper-runtime","ggml-small-q8_0.bin");VadPath=Path.Combine(ApplicationRoot,"whisper-runtime","ggml-silero-v6.2.0.bin");WorkerPath=Path.Combine(ApplicationRoot,"YikeSpeechWorker.exe");}
}
}
