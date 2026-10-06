using System;
namespace WindowsTranslator {
internal sealed class SpeechRecognitionCandidate {
 internal string Text {get;private set;}
 internal int ExitCode {get;private set;}
 internal string FailureCode {get;private set;}
 private readonly double[] probabilities;
 internal double[] OrdinaryTokenProbabilities {get{return probabilities==null?null:(double[])probabilities.Clone();}}
 internal SpeechRecognitionCandidate(string text,int exitCode,string failureCode,double[] ordinaryTokenProbabilities){Text=text??"";ExitCode=exitCode;FailureCode=failureCode??"";probabilities=ordinaryTokenProbabilities==null?null:(double[])ordinaryTokenProbabilities.Clone();}
}
}
