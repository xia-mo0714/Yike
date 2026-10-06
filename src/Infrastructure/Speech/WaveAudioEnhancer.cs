using System;
using System.IO;
using System.Text;

namespace WindowsTranslator {
internal static class WaveAudioEnhancer
{
	internal static bool TryEnhance(string inputPath,out string outputPath){AudioEnhancementReport report;return TryEnhance(inputPath,out outputPath,out report);}
	internal static bool TryEnhance(string inputPath,out string outputPath,out AudioEnhancementReport report)
	{
		outputPath=null;report=new AudioEnhancementReport();
		try{
			byte[] bytes=File.ReadAllBytes(inputPath);if(bytes.Length<44||Encoding.ASCII.GetString(bytes,0,4)!="RIFF"||Encoding.ASCII.GetString(bytes,8,4)!="WAVE")return false;
			int channels=0,rate=0,bits=0,format=0,data=-1,length=0;
			for(int offset=12;offset+8<=bytes.Length;){
				int size=BitConverter.ToInt32(bytes,offset+4);if(size<0||offset+8L+size>bytes.Length)return false;
				string id=Encoding.ASCII.GetString(bytes,offset,4);
				if(id=="fmt "&&size>=16){format=BitConverter.ToUInt16(bytes,offset+8);channels=BitConverter.ToUInt16(bytes,offset+10);rate=BitConverter.ToInt32(bytes,offset+12);bits=BitConverter.ToUInt16(bytes,offset+22);}
				if(id=="data"){data=offset+8;length=size;break;}offset+=8+size+(size&1);
			}
			if(format!=1||bits!=16||channels<1||channels>2||rate<8000||rate>192000||data<0||length<channels*2||length%(channels*2)!=0)return false;
			report.SampleRate=rate;report.Channels=channels;
			int samples=length/2,frameSize=Math.Max(1,rate/50)*channels,frameCount=(samples+frameSize-1)/frameSize;
			double[] filtered=new double[samples],previousInput=new double[channels],previousOutput=new double[channels],energy=new double[frameCount];
			int[] frameSamples=new int[frameCount];double rawEnergy=0,dc=0,peak=0;int clipped=0;
			double alpha=1.0/(1.0+2*Math.PI*80/rate);
			for(int i=0;i<samples;i++){
				double sample=BitConverter.ToInt16(bytes,data+i*2)/32768.0;dc+=sample;rawEnergy+=sample*sample;peak=Math.Max(peak,Math.Abs(sample));if(Math.Abs(sample)>=.999)clipped++;
				int channel=i%channels;double high=alpha*(previousOutput[channel]+sample-previousInput[channel]);previousInput[channel]=sample;previousOutput[channel]=high;filtered[i]=high;
				int frame=i/frameSize;energy[frame]+=high*high;frameSamples[frame]++;
			}
			report.Peak=peak;report.Rms=Math.Sqrt(rawEnergy/samples);report.DcOffset=dc/samples;report.ClippingRatio=(double)clipped/samples;
			double[] frameRms=new double[frameCount];for(int i=0;i<frameCount;i++)frameRms[i]=Math.Sqrt(energy[i]/Math.Max(1,frameSamples[i]));
			double[] sorted=(double[])frameRms.Clone();Array.Sort(sorted);double noise=sorted[(int)Math.Floor((sorted.Length-1)*.2)];report.NoiseRms=noise;
			double activeEnergy=0;int active=0,activeSamples=0;
			for(int i=0;i<frameCount;i++)if(frameRms[i]>Math.Max(.0002,noise*1.4)){active++;activeEnergy+=energy[i];activeSamples+=frameSamples[i];}
			double rms=activeSamples==0?0:Math.Sqrt(activeEnergy/activeSamples);
			report.SpeechFrameRatio=(double)active/frameCount;report.SilenceRatio=1-report.SpeechFrameRatio;
			report.EstimatedSnrDb=rms<=0?0:20*Math.Log10(rms/Math.Max(noise,.000001));
			if(peak<.0002){report.SkipReason="silence";return false;}
			if(report.ClippingRatio>.001){report.SkipReason="input_clipped";return false;}
			if(active<5){report.SkipReason="insufficient_speech";return false;}
			if(report.EstimatedSnrDb<6){report.SkipReason="noise_too_high";return false;}
			double gain=Math.Max(1,Math.Min(3,.1/Math.Max(rms,.000001)));if(report.EstimatedSnrDb<12)gain=Math.Min(gain,1.5);report.AppliedGain=gain;
			for(int i=0;i<samples;i++){
				double value=filtered[i]*gain,absolute=Math.Abs(value);
				if(absolute>.85)value=Math.Sign(value)*(.85+.13*Math.Tanh((absolute-.85)/.13));
				short encoded=(short)Math.Max(-32768,Math.Min(32767,Math.Round(value*32768)));bytes[data+i*2]=(byte)(encoded&255);bytes[data+i*2+1]=(byte)(encoded>>8);
			}
			string destination=Path.Combine(Path.GetDirectoryName(inputPath),Path.GetFileNameWithoutExtension(inputPath)+".enhanced.wav");
			File.WriteAllBytes(destination,bytes);outputPath=destination;report.EnhancementUsed=true;report.SkipReason="";return true;
		}catch(IOException){report.SkipReason="wave_io_failed";return false;}catch(UnauthorizedAccessException){report.SkipReason="wave_access_failed";return false;}catch(ArgumentException){report.SkipReason="invalid_wave_path";return false;}
	}
}
}
