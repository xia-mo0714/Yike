using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
namespace WindowsTranslator {
internal sealed class SpeechWorkerPayload {
 public string language,audioPath,deviceName,text,failureCode,stopReason;
 public int deviceIndex,timeoutMilliseconds;
 public int contextInitializationCount,loadMilliseconds,captureReadyMilliseconds;
 public bool isWarm;
 public bool useVad;
 public double[] probabilities;
}
internal sealed class SpeechWorkerMessage {
 public int Version;
 public Guid InstanceId,SessionId,RequestId;
 public string Kind;
 public SpeechWorkerPayload Payload;
}
internal static class SpeechWorkerProtocol {
 internal const int CommandLimit=65536,ResultLimit=1048576;
 internal static readonly Guid ControlSession=new Guid("00000000-0000-0000-0000-000000000001");
 private static readonly string[] commands={"hello","preheat","start","stop","decode","cancel","shutdown"};
 private static readonly string[] events={"ready","capture_started","draft","stopped","final","cancelled","failure"};
 private static readonly UTF8Encoding utf8=new UTF8Encoding(false,true);
 private sealed class ReaderState {internal readonly byte[] Buffer=new byte[4096];internal int Position,Length;internal readonly SemaphoreSlim Gate=new SemaphoreSlim(1,1);}
 private static readonly ConditionalWeakTable<Stream,ReaderState> readers=new ConditionalWeakTable<Stream,ReaderState>();
 private static readonly ConditionalWeakTable<Stream,SemaphoreSlim> writers=new ConditionalWeakTable<Stream,SemaphoreSlim>();
 internal static bool IsCommand(string kind){return commands.Contains(kind,StringComparer.Ordinal);}
 internal static bool IsEvent(string kind){return events.Contains(kind,StringComparer.Ordinal);}
 internal static void Validate(SpeechWorkerMessage message){
  if(message==null||message.Version!=1||message.InstanceId==Guid.Empty||message.SessionId==Guid.Empty||message.RequestId==Guid.Empty||(!IsCommand(message.Kind)&&!IsEvent(message.Kind)))throw new InvalidDataException("speech_protocol_invalid_envelope");
 }
 private static JavaScriptSerializer Serializer(){return new JavaScriptSerializer {MaxJsonLength=ResultLimit,RecursionLimit=16};}
 internal static async Task<SpeechWorkerMessage> ReadAsync(Stream stream,int maxUtf8Bytes,CancellationToken token){
  if(maxUtf8Bytes<=0||maxUtf8Bytes>ResultLimit)throw new ArgumentOutOfRangeException("maxUtf8Bytes");
  var state=readers.GetValue(stream,_=>new ReaderState());await state.Gate.WaitAsync(token).ConfigureAwait(false);
  try{using(var line=new MemoryStream()){
   while(true){
    token.ThrowIfCancellationRequested();
    if(state.Position==state.Length){state.Length=await stream.ReadAsync(state.Buffer,0,state.Buffer.Length,token).ConfigureAwait(false);state.Position=0;
     if(state.Length==0){if(line.Length==0)return null;throw new InvalidDataException("speech_protocol_unterminated_line");}
    }
    int next=Array.IndexOf(state.Buffer,(byte)10,state.Position,state.Length-state.Position);
    int count=(next<0?state.Length:next)-state.Position;
    if(line.Length+count>maxUtf8Bytes)throw new InvalidDataException("speech_protocol_oversize");
    line.Write(state.Buffer,state.Position,count);state.Position+=count;
    if(next<0)continue;state.Position++;
    try{
     string json=utf8.GetString(line.GetBuffer(),0,(int)line.Length);
     var message=Serializer().Deserialize<SpeechWorkerMessage>(json);Validate(message);return message;
    }catch(InvalidDataException){throw;}catch(Exception ex){if(ex is ArgumentException||ex is InvalidOperationException||ex is DecoderFallbackException)throw new InvalidDataException("speech_protocol_invalid_json");throw;}
   }
  }}finally{state.Gate.Release();}
 }
 internal static async Task WriteAsync(Stream stream,SpeechWorkerMessage message,int maxUtf8Bytes,CancellationToken token){
  Validate(message);token.ThrowIfCancellationRequested();
  if(maxUtf8Bytes<=0||maxUtf8Bytes>ResultLimit)throw new ArgumentOutOfRangeException("maxUtf8Bytes");
  byte[] bytes;
  try{string json=Serializer().Serialize(message);if(utf8.GetByteCount(json)>maxUtf8Bytes)throw new InvalidDataException("speech_protocol_oversize");bytes=utf8.GetBytes(json+"\n");}
  catch(InvalidDataException){throw;}catch(ArgumentException){throw new InvalidDataException("speech_protocol_invalid_json");}
  var gate=writers.GetValue(stream,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(token).ConfigureAwait(false);
  try{await stream.WriteAsync(bytes,0,bytes.Length,token).ConfigureAwait(false);await stream.FlushAsync(token).ConfigureAwait(false);}finally{gate.Release();}
 }
}
}
