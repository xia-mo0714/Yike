using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WindowsTranslator {
public static partial class Tests {
	internal static void WriteTestWave(string path,int channels,Func<int,double> sample){
		using(BinaryWriter w=new BinaryWriter(File.Create(path),Encoding.ASCII)){int count=32000;w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+count*channels*2);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)channels);w.Write(16000);w.Write(16000*channels*2);w.Write((short)(channels*2));w.Write((short)16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(count*channels*2);for(int i=0;i<count;i++)for(int c=0;c<channels;c++)w.Write((short)Math.Max(-32768,Math.Min(32767,Math.Round(sample(i)*32768))));}
	}
	internal static void RunSafeEnhancementTests(List<string> lines){
		string path=Path.Combine(Path.GetTempPath(),"Yike-enhance-test-"+Guid.NewGuid().ToString("N")+".wav");string output=null;AudioEnhancementReport report;
		try{
			WriteTestWave(path,1,i=>0);Check(!WaveAudioEnhancer.TryEnhance(path,out output,out report)&&output==null&&report.SkipReason.Length>0,"Pure silence amplified");
			foreach(int channels in new[]{1,2}){
				WriteTestWave(path,channels,i=>(i%16000<4000?.00005:.015)*Math.Sin(2*Math.PI*350*i/16000));byte[] original=File.ReadAllBytes(path);
				Check(WaveAudioEnhancer.TryEnhance(path,out output,out report)&&report.EnhancementUsed&&report.AppliedGain<=3&&report.SampleRate==16000&&report.Channels==channels,"Quiet speech safe gain failed");
				Check(original.SequenceEqual(File.ReadAllBytes(path))&&new FileInfo(output).Length==original.Length,"Enhancement changed original WAV");File.Delete(output);output=null;
			}
			WriteTestWave(path,1,i=>i%16000<4000?0:(i%2==0?1:-1));Check(!WaveAudioEnhancer.TryEnhance(path,out output,out report)&&report.ClippingRatio>.001,"Clipped audio amplified");
			Random random=new Random(1);WriteTestWave(path,1,i=>(random.NextDouble()-.5)*.4);Check(!WaveAudioEnhancer.TryEnhance(path,out output,out report)&&report.SkipReason.Length>0,"High noise amplified");
			WriteTestWave(path,1,i=>.01*Math.Sin(2*Math.PI*50*i/16000));Check(!WaveAudioEnhancer.TryEnhance(path,out output,out report),"Continuous rumble amplified");
			WriteTestWave(path,1,i=>(i%16000<4000?.006:.02)*Math.Sin(2*Math.PI*350*i/16000));WaveAudioEnhancer.TryEnhance(path,out output,out report);Check(report.EstimatedSnrDb>=12||report.AppliedGain<=1.5,"Low SNR exceeded safety gain");if(output!=null)File.Delete(output);output=null;
			File.WriteAllBytes(path,new byte[50]);Check(!WaveAudioEnhancer.TryEnhance(path,out output,out report)&&report.SkipReason.Length>0,"Damaged WAV accepted");
			WriteTestWave(path,1,i=>.1);byte[] unsupported=File.ReadAllBytes(path);unsupported[34]=8;File.WriteAllBytes(path,unsupported);Check(!WaveAudioEnhancer.TryEnhance(path,out output,out report)&&report.SkipReason.Length>0,"Unsupported PCM accepted");
		}finally{if(File.Exists(path))File.Delete(path);if(output!=null&&File.Exists(output))File.Delete(output);}
		lines.Add("PASS speech enhancement: original preserved, silence/noise/clipping bypass, mono/stereo and bounded gain");
	}
}
}
