namespace WindowsTranslator {
internal sealed class SpeechDecodeRequest {
 internal string AudioPath {get;private set;}
 internal string Language {get;private set;}
 internal bool UseVad {get;private set;}
 internal int TimeoutMilliseconds {get;private set;}
 internal SpeechDecodeRequest(string audioPath,string language,bool useVad,int timeoutMilliseconds){AudioPath=audioPath;Language=language;UseVad=useVad;TimeoutMilliseconds=timeoutMilliseconds;}
}
}
