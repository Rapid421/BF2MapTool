using System.Collections.Generic;

namespace FrostyCLI.Core
{
    public class ModSettings
    {
        public string Name;
        public string Author;
        public string Version;
        public string Description;
        public string Category;
        // extra embedded resources written into the .fbmod beside the icon and screenshots (name -> bytes), e.g.
        // KyberLevelDeclarationData
        public Dictionary<string, byte[]> Embedded = new Dictionary<string, byte[]>();
    }
}
