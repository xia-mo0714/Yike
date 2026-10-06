using System;
using System.IO;
using System.Text;
namespace WindowsTranslator {
internal static class SpeechOwnedWave {
 internal static bool Recover(Guid session,string root){
  string full=Path.GetFullPath(root),temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
  if(session==Guid.Empty||session==SpeechWorkerProtocol.ControlSession||!full.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(full)!="Yike-voice-"+session.ToString("N"))return false;
  string path=Path.Combine(full,"recording.wav");
  try{
   for(FileSystemInfo item=new FileInfo(path);item!=null;item=item is FileInfo?(FileSystemInfo)((FileInfo)item).Directory:((DirectoryInfo)item).Parent)if((item.Attributes&FileAttributes.ReparsePoint)!=0)return false;
   using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))using(var reader=new BinaryReader(file,Encoding.ASCII,true)){
    if(file.Length<=44||file.Length>9600044||((file.Length-44)%2)!=0||reader.ReadUInt32()!=0x46464952)return false;
    uint declared=reader.ReadUInt32();if(reader.ReadUInt32()!=0x45564157||reader.ReadUInt32()!=0x20746d66||reader.ReadUInt32()!=16||reader.ReadUInt16()!=1||reader.ReadUInt16()!=1||reader.ReadUInt32()!=16000||reader.ReadUInt32()!=32000||reader.ReadUInt16()!=2||reader.ReadUInt16()!=16||reader.ReadUInt32()!=0x61746164)return false;
    uint data=reader.ReadUInt32();if(!(declared==36&&data==0)&&!(declared+8L==file.Length&&data+44L==file.Length))return false;
    using(var writer=new BinaryWriter(file,Encoding.ASCII,true)){file.Position=4;writer.Write((uint)(file.Length-8));file.Position=40;writer.Write((uint)(file.Length-44));writer.Flush();file.Flush(true);}return true;
   }
  }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
 }
}
}
