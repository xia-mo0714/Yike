using System;
using System.IO;
using System.Text;
namespace WindowsTranslator {
internal sealed class SessionWaveWriter:IDisposable {
 internal const int MaximumSamples=4800000;private readonly object sync=new object();private FileStream stream;private BinaryWriter writer;private int count;private bool complete;
 internal string Path{get;private set;}internal int SampleCount{get{lock(sync)return count;}}
 internal static void ValidateRoot(string root){
  string full=System.IO.Path.GetFullPath(root),parent=System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar)+System.IO.Path.DirectorySeparatorChar,leaf=System.IO.Path.GetFileName(full);Guid id;
  if(!full.StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!leaf.StartsWith("Yike-voice-",StringComparison.Ordinal)||!Guid.TryParseExact(leaf.Substring(11),"N",out id)||!Directory.Exists(full))throw new InvalidDataException("worker_audio_unowned");NativeLibraryLoader.AssertPlainPath(full);
 }
 internal SessionWaveWriter(string root){ValidateRoot(root);Path=System.IO.Path.Combine(root,"recording.wav");stream=new FileStream(Path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);writer=new BinaryWriter(stream,Encoding.ASCII,true);WriteHeader(writer,0);writer.Flush();stream.Flush(true);}
 private static void WriteHeader(BinaryWriter writer,int samples){writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+samples*2);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(16000);writer.Write(32000);writer.Write((short)2);writer.Write((short)16);writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(samples*2);}
 internal void Append(float[] samples){if(samples==null)throw new ArgumentNullException("samples");lock(sync){if(stream==null)throw new InvalidOperationException("worker_wave_closed");int allowed=Math.Min(samples.Length,MaximumSamples-count);for(int i=0;i<allowed;i++){
  float sample=samples[i];if(float.IsNaN(sample)||float.IsInfinity(sample)||sample<-1||sample>1)throw new InvalidDataException("worker_wave_sample");writer.Write((short)Math.Max(short.MinValue,Math.Min(short.MaxValue,Math.Round(sample*32768))));
 }count+=allowed;writer.Flush();stream.Flush(true);}}
 internal string Complete(){lock(sync){if(stream==null){if(complete)return Path;throw new InvalidOperationException("worker_wave_closed");}stream.Position=0;WriteHeader(writer,count);writer.Flush();stream.Flush(true);complete=true;Dispose();return Path;}}
 internal static bool Recover(string path){
  string full=System.IO.Path.GetFullPath(path);ValidateRoot(System.IO.Path.GetDirectoryName(full));if(System.IO.Path.GetFileName(full)!="recording.wav")throw new InvalidDataException("worker_audio_unowned");NativeLibraryLoader.AssertPlainPath(full);
  try{using(var file=new FileStream(full,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))using(var reader=new BinaryReader(file,Encoding.ASCII,true)){
   if(file.Length<44||file.Length>44L+MaximumSamples*2||((file.Length-44)%2)!=0||reader.ReadUInt32()!=0x46464952)return false;
   uint declared=reader.ReadUInt32();if(reader.ReadUInt32()!=0x45564157||reader.ReadUInt32()!=0x20746d66||reader.ReadUInt32()!=16||reader.ReadUInt16()!=1||reader.ReadUInt16()!=1||reader.ReadUInt32()!=16000||reader.ReadUInt32()!=32000||reader.ReadUInt16()!=2||reader.ReadUInt16()!=16||reader.ReadUInt32()!=0x61746164)return false;
   uint data=reader.ReadUInt32();if(!(declared==36&&data==0)&&!(declared+8L==file.Length&&data+44L==file.Length))return false;
   file.Position=0;using(var writer=new BinaryWriter(file,Encoding.ASCII,true)){WriteHeader(writer,(int)((file.Length-44)/2));writer.Flush();file.Flush(true);}return true;
  }}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
 }
 public void Dispose(){lock(sync){if(stream==null)return;writer.Flush();stream.Flush(true);writer.Dispose();stream.Dispose();writer=null;stream=null;}}
}
}
