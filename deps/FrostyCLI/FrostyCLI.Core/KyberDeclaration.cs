using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core
{
    // Kyber's custom map / mode declaration, the format of ArmchairDevelopers/KyberLevelDeclaratorPlugin
    // (LevelDeclaration.cs): {"version":1, "maps":[{name, id, image, supportedModes}], "modes":[{name, id, maxPlayers,
    // image}], "modeMappings":{}, "modeNameOverrides":{}}. `id` is the level path Kyber launches (LevelSetup.Name);
    // `image` a base64 JPEG (the plugin scales to 1024 px and saves at quality 80).
    //
    // The plugin keeps it in the project as chunk BEBEBEBE-BEBE-BEBE-BEBE-BEBEBEBEBEBE ("U" = not yet written) and, when it
    // writes the .fbmod, leaves that chunk out and ships the JSON as the embedded resource "KyberLevelDeclarationData"
    // (Overrides.cs: FrostyModWriter.AddResource postfix + WriteProject prefix/postfix). The CLI does the same, so a project
    // opens and exports the same way in Frosty with the plugin.
    public static class KyberDeclaration
    {
        public static readonly Guid ChunkId = new Guid("BEBEBEBE-BEBE-BEBE-BEBE-BEBEBEBEBEBE");
        public const string Uninitialized = "U";
        public const string EmbeddedName = "KyberLevelDeclarationData";

        public static JObject Empty() => new JObject
        {
            ["version"] = 1, ["maps"] = new JArray(), ["modes"] = new JArray(),
            ["modeMappings"] = new JObject(), ["modeNameOverrides"] = new JObject(),
        };

        // the declaration a chunk holds; null / empty / "U" = a new one
        public static JObject Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == Uninitialized) return Empty();
            JObject o = JObject.Parse(json);
            JObject d = Empty();
            foreach (var p in o.Properties()) d[p.Name] = p.Value;
            foreach (string list in new[] { "maps", "modes" }) if (!(d[list] is JArray)) d[list] = new JArray();
            foreach (string map in new[] { "modeMappings", "modeNameOverrides" }) if (!(d[map] is JObject)) d[map] = new JObject();
            return d;
        }

        public static JObject Parse(byte[] data) => Parse(data == null ? null : Encoding.UTF8.GetString(data));

        // add or replace the map whose id is <id> (case-insensitive)
        public static void UpsertMap(JObject d, string name, string id, IEnumerable<string> modes, string imageBase64)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("a Kyber map needs the level path it launches (id)");
            var maps = (JArray)d["maps"];
            JObject old = maps.OfType<JObject>().FirstOrDefault(m => string.Equals((string)m["id"], id, StringComparison.OrdinalIgnoreCase));
            var entry = new JObject
            {
                ["name"] = !string.IsNullOrWhiteSpace(name) ? name : (string)old?["name"] ?? id.Substring(id.LastIndexOf('/') + 1),   // a redeclare without --name keeps the declared name
                ["id"] = id,
                ["image"] = imageBase64 ?? (string)old?["image"] ?? "",
                ["supportedModes"] = new JArray((modes ?? new string[0]).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct()),
            };
            if (old != null) old.Replace(entry); else maps.Add(entry);
        }

        public static bool RemoveMap(JObject d, string id)
        {
            var maps = (JArray)d["maps"];
            JObject old = maps.OfType<JObject>().FirstOrDefault(m => string.Equals((string)m["id"], id, StringComparison.OrdinalIgnoreCase));
            if (old == null) return false;
            old.Remove();
            return true;
        }

        public static bool IsEmpty(JObject d) =>
            !((JArray)d["maps"]).Any() && !((JArray)d["modes"]).Any() && !((JObject)d["modeMappings"]).Properties().Any()
            && !((JObject)d["modeNameOverrides"]).Properties().Any();

        public static byte[] ToBytes(JObject d) => Encoding.UTF8.GetBytes(d.ToString(Formatting.None));
    }
}
