using System.Threading;
using System.Threading.Tasks;
namespace WindowsTranslator {
internal interface ISpeechFinalDecoder {
 Task<SpeechRecognitionCandidate> DecodeAsync(SpeechDecodeRequest request,CancellationToken token);
}
}
