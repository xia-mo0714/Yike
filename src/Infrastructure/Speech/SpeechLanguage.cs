namespace WindowsTranslator {
internal static class SpeechLanguage {
 internal static bool IsCode(string value){if(value=="auto")return true;if(value==null||value.Length<2||value.Length>3)return false;foreach(char c in value)if(c<'a'||c>'z')return false;return true;}
}
}
