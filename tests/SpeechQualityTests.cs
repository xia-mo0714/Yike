using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Speech.AudioFormat;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WindowsTranslator {
public static partial class Tests {
	internal static void RunSpeechQualityMetricTests(List<string> lines)
	{
		Check(SpeechQualityMetrics.Normalize("你好，世界！ Hello.") == "你好世界 hello", "SpeechMetricsPreserveChinese");
		Check(SpeechQualityMetrics.CharacterErrorRate("你好世界", "你好") == 0.5, "Chinese CER wrong");
		Check(SpeechQualityMetrics.WordErrorRate("one two", "one too") == 0.5, "English WER wrong");
		Check(SpeechQualityMetrics.CharacterErrorRate("", "") == 0 && SpeechQualityMetrics.CharacterErrorRate("", "hello") == 1, "SpeechMetricsHandleEmptyReferences");
		Check(SpeechQualityMetrics.WordErrorRate("", "") == 0 && SpeechQualityMetrics.WordErrorRate("", "hello") == 1, "Empty WER wrong");
		Check(SpeechQualityMetrics.CharacterErrorRate("𠮷好", "好") == 0.5, "CER split a Unicode text element");
		Check(SpeechQualityTests.ValidateManifest(SpeechQualityTests.DefaultManifest), "SpeechCorpusCoversModes");
		lines.Add("PASS speech quality: Chinese/Unicode CER, English WER, empty references and corpus modes");
	}
}

internal static class SpeechQualityMetrics {
	internal static string Normalize(string value) {
		StringBuilder result = new StringBuilder();
		foreach (char c in (value ?? "").Normalize(NormalizationForm.FormC).ToLowerInvariant()) {
			if (char.IsWhiteSpace(c)) result.Append(' ');
			else if (!char.IsPunctuation(c) && !char.IsSymbol(c)) result.Append(c);
		}
		return Regex.Replace(result.ToString(), @"\s+", " ").Trim();
	}
	private static string[] Elements(string value) {
		List<string> list = new List<string>();
		TextElementEnumerator e = StringInfo.GetTextElementEnumerator(Normalize(value).Replace(" ", ""));
		while (e.MoveNext()) list.Add(e.GetTextElement());
		return list.ToArray();
	}
	private static double Error(string[] expected, string[] actual) {
		if (expected.Length == 0) return actual.Length == 0 ? 0 : 1;
		int[] previous = Enumerable.Range(0, actual.Length + 1).ToArray();
		for (int i = 1; i <= expected.Length; i++) {
			int[] current = new int[actual.Length + 1]; current[0] = i;
			for (int j = 1; j <= actual.Length; j++) current[j] = Math.Min(current[j-1]+1, Math.Min(previous[j]+1, previous[j-1]+(expected[i-1] == actual[j-1] ? 0 : 1)));
			previous = current;
		}
		return (double)previous[actual.Length] / expected.Length;
	}
	internal static double CharacterErrorRate(string expected, string actual) { return Error(Elements(expected), Elements(actual)); }
	internal static double WordErrorRate(string expected, string actual) { return Error(Normalize(expected).Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries), Normalize(actual).Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries)); }
}

internal static class SpeechQualityTests {
	internal sealed class Corpus { public Case[] cases; }
	internal sealed class Case { public string id; public string text; public string culture; public string variant; public string[] languages; public string audio; public string voice; }
	internal static string DefaultManifest { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "speech-quality-cases.json"); } }
	internal static bool ValidateManifest(string path) {
		Corpus corpus = Store.Json.Deserialize<Corpus>(File.ReadAllText(path, Encoding.UTF8));
		return corpus.cases != null && new[]{"normal", "low-volume", "fan-noise", "keyboard-spike", "sentence-pause"}.All(v => corpus.cases.Any(c => c.variant == v)) && new[]{"zh-CN", "en-US", "mixed"}.All(v => corpus.cases.Any(c => c.culture == v)) && corpus.cases.All(c => !string.IsNullOrEmpty(c.id) && !string.IsNullOrEmpty(c.text) && c.languages.Length > 0);
	}
	internal static string Hash(string path) { using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
	private static void Generate(Case c, string directory) {
		c.audio = Path.Combine(directory, c.id + ".wav");
		string identity = c.audio + ".voice";
		if (File.Exists(c.audio) && File.Exists(identity)) { c.voice = File.ReadAllText(identity); return; }
		using (SpeechSynthesizer synth = new SpeechSynthesizer()) {
			string culture = c.culture == "mixed" ? "zh-CN" : c.culture;
			VoiceInfo voice = synth.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.Name == culture).Select(v => v.VoiceInfo).OrderBy(v => v.Name, StringComparer.Ordinal).FirstOrDefault();
			if (voice == null) throw new InvalidOperationException("Missing local TTS voice: " + culture);
			c.voice = voice.Name; synth.SelectVoice(c.voice);
			synth.SetOutputToWaveFile(c.audio, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
			if (c.culture == "mixed") {
				PromptBuilder prompt = new PromptBuilder(new CultureInfo("zh-CN"));
				prompt.AppendText("今天我们测试"); prompt.StartVoice(new CultureInfo("en-US")); prompt.AppendText("voice input"); prompt.EndVoice(); prompt.AppendText("效果很好。"); synth.Speak(prompt);
			} else synth.Speak(c.text);
		}
		Transform(c.audio, c.variant);
		File.WriteAllText(identity, c.voice, Encoding.UTF8);
	}
	private static void Transform(string path, string variant) {
		byte[] bytes = File.ReadAllBytes(path); int data = -1, length = 0;
		for (int offset = 12; offset + 8 <= bytes.Length;) { int size = BitConverter.ToInt32(bytes, offset+4); if (size < 0 || offset + 8L + size > bytes.Length) throw new InvalidDataException("TTS WAV"); if (Encoding.ASCII.GetString(bytes,offset,4)=="data") { data=offset+8; length=size; break; } offset += 8+size+(size&1); }
		if (data < 0) throw new InvalidDataException("Missing TTS samples");
		Random random = new Random(714); int count = length / 2;
		for (int i=0;i<count;i++) {
			double sample=BitConverter.ToInt16(bytes,data+i*2);
			if (variant=="low-volume") sample*=0.035;
			if (variant=="fan-noise") sample=sample*0.10 + 180*Math.Sin(2*Math.PI*120*i/16000) + (random.NextDouble()-0.5)*220;
			if (variant=="keyboard-spike" && i%24000<320) sample+=(random.NextDouble()-0.5)*12000;
			short value=(short)Math.Max(-32768,Math.Min(32767,Math.Round(sample))); bytes[data+i*2]=(byte)(value&255); bytes[data+i*2+1]=(byte)(value>>8);
		}
		if (variant=="sentence-pause") { int insertion=data+(count/2)*2; byte[] paused=new byte[bytes.Length+16000]; Buffer.BlockCopy(bytes,0,paused,0,insertion); Buffer.BlockCopy(bytes,insertion,paused,insertion+16000,bytes.Length-insertion); Buffer.BlockCopy(BitConverter.GetBytes(length+16000),0,paused,data-4,4); Buffer.BlockCopy(BitConverter.GetBytes(paused.Length-8),0,paused,4,4); bytes=paused; }
		File.WriteAllBytes(path,bytes);
	}
	private static string LocalPath(string path) {
		Uri root=new Uri(Path.GetDirectoryName(WhisperSpeechInput.CliPath)+Path.DirectorySeparatorChar);
		return Uri.UnescapeDataString(root.MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString()).Replace('/',Path.DirectorySeparatorChar);
	}
	internal static void Run(string casesPath, string outputPath, string pipeline,bool confidenceRetry=false) {
		if (pipeline!="legacy" && pipeline!="phase1" && pipeline!="phase2") throw new ArgumentException("Unknown speech pipeline.");
		Corpus corpus=Store.Json.Deserialize<Corpus>(File.ReadAllText(casesPath,Encoding.UTF8));
		string frozenRoot=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","work","phase1-quality"));
		string scorer="";PhaseTwoQualityReport frozen=null;PhaseTwoFileSession native=null;
		if(pipeline=="phase2"){
			if(Hash(Path.Combine(frozenRoot,"candidate.json"))!="5590aeb9d7db652a487b2d7e1182da2ab98b9cd6c1522cc3f9866a19bd68017e"||Hash(casesPath)!="843cbe7f71a2ef5176712010b022846e8c8819e5a4f6e851bd27ad1782d8ccfa")throw new InvalidDataException("Frozen corpus/report changed");
			scorer=PhaseTwoAcceptance.VerifyScorer(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","tests","SpeechQualityTests.cs"));frozen=Store.Json.Deserialize<PhaseTwoQualityReport>(File.ReadAllText(Path.Combine(frozenRoot,"candidate.json")));native=new PhaseTwoFileSession();
		}
		string fixtureRoot=pipeline=="phase2"?Path.Combine(frozenRoot,"fixtures"):Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)),"fixtures");if(pipeline!="phase2")Directory.CreateDirectory(fixtureRoot);
		List<object> results=new List<object>();
		try{
		foreach (Case c in corpus.cases) {
			if(pipeline=="phase2"){c.audio=Path.Combine(fixtureRoot,c.id+".wav");if(!File.Exists(c.audio)||!File.Exists(c.audio+".voice"))throw new FileNotFoundException("Frozen audio missing");c.voice=File.ReadAllText(c.audio+".voice");}else Generate(c,fixtureRoot);
			foreach (string hint in c.languages) {
				if(pipeline=="phase2"){var old=frozen.cases.Single(x=>x.id==c.id&&x.language==hint);if(old.expected!=c.text||old.audioSha256!=Hash(c.audio)||old.modelSha256!=Hash(WhisperSpeechInput.ModelPath))throw new InvalidDataException("Frozen audio/model/reference changed");}
				Stopwatch clock=Stopwatch.StartNew(); string actual="", failure=""; int exit=-1; string enhanced=null;
				SpeechRefinementResult phase2Result=null;
				try {
					if(pipeline=="phase2"){phase2Result=native.Decode(c.audio,hint,confidenceRetry);actual=phase2Result.Text;exit=string.IsNullOrWhiteSpace(actual)?1:0;failure=phase2Result.FailureCode;}
					else if(pipeline=="phase1") {var refined=new SpeechRefinement().RunAsync(c.audio,hint,"",System.Threading.CancellationToken.None).GetAwaiter().GetResult();actual=refined.Text;exit=string.IsNullOrWhiteSpace(actual)?1:0;failure=exit==0?"":refined.FailureCode;}
					else {
					string audio=c.audio; if (WaveAudioEnhancer.TryEnhance(audio,out enhanced)) audio=enhanced;
					string arguments=WhisperSpeechInput.RefinementArguments(hint,audio).Replace("\""+WhisperSpeechInput.ModelPath+"\"","\""+LocalPath(WhisperSpeechInput.ModelPath)+"\"").Replace("\""+audio+"\"","\""+LocalPath(audio)+"\"");
					ProcessStartInfo info=new ProcessStartInfo { FileName=WhisperSpeechInput.CliPath, WorkingDirectory=Path.GetDirectoryName(WhisperSpeechInput.CliPath), Arguments=arguments, UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 };
					using (Process process=Process.Start(info)) using (ProcessJob job=ProcessJob.AttachOrTerminate(process)) {
						Task<string> stdout=process.StandardOutput.ReadToEndAsync(); Task<string> stderr=process.StandardError.ReadToEndAsync();
						if (!process.WaitForExit(300000)) { job.Dispose(); failure="timeout"; } else { Task.WaitAll(stdout,stderr); exit=process.ExitCode; actual=string.Join(" ",stdout.Result.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(WhisperSpeechInput.Clean).Where(x=>x.Length>0)).Trim(); if(exit!=0) failure="cli_failed"; }
					}
					}
				} catch (Exception) { failure="quality_execution_failed"; } finally { if(enhanced!=null && File.Exists(enhanced)) File.Delete(enhanced); }
				string normalized=SpeechQualityMetrics.Normalize(c.text), hypothesis=SpeechQualityMetrics.Normalize(actual);
				string[] tokens=c.culture=="en-US" ? normalized.Split(' ') : StringInfo.ParseCombiningCharacters(normalized).Select(i=>StringInfo.GetNextTextElement(normalized,i)).ToArray();
				results.Add(new { id=c.id,language=hint,variant=c.variant,culture=c.culture,expected=c.text,actual=actual,cer=SpeechQualityMetrics.CharacterErrorRate(c.text,actual),wer=SpeechQualityMetrics.WordErrorRate(c.text,actual),empty=string.IsNullOrWhiteSpace(actual),firstTokenRetained=tokens.Length>0 && hypothesis.StartsWith(tokens[0],StringComparison.Ordinal),lastTokenRetained=tokens.Length>0 && hypothesis.EndsWith(tokens[tokens.Length-1],StringComparison.Ordinal),elapsedMilliseconds=clock.ElapsedMilliseconds,exitCode=exit,failure=failure,voice=c.voice,audioSha256=Hash(c.audio),modelSha256=Hash(WhisperSpeechInput.ModelPath),runtimeSha256=Hash(pipeline=="phase2"?Path.Combine(Path.GetDirectoryName(WhisperSpeechInput.CliPath),"whisper.dll"):WhisperSpeechInput.CliPath),pipeline=pipeline,quality=phase2Result==null?"Unknown":phase2Result.Quality.ToString(),attempts=phase2Result==null?1:phase2Result.AttemptsUsed });
				File.WriteAllText(outputPath,Store.Json.Serialize(new { pipeline=pipeline,complete=false,cases=results,scorerSha256=scorer,confidenceRetry=confidenceRetry }),new UTF8Encoding(false));
			}
		}
		File.WriteAllText(outputPath,Store.Json.Serialize(new { pipeline=pipeline,complete=true,cases=results,scorerSha256=scorer,confidenceRetry=confidenceRetry }),new UTF8Encoding(false));
		}finally{if(native!=null)native.Dispose();}
	}
}
}
