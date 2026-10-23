using System;
using System.Collections.Generic;
using System.IO;
using FrostySdk.IO;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Read-only parser for a Frosty .fbproject (FrostyProject v9-14). Extracts the NAMES of the
    /// added/modified EBX, Res and Chunk assets WITHOUT touching App.AssetManager — it mirrors
    /// FrostyProject.InternalLoad's exact NativeReader read sequence, only keeping names and skipping
    /// blobs by their length prefixes. Returns null if the file isn't a supported v9-14 project (or
    /// the read misaligns), so callers can fall back gracefully.
    /// </summary>
    public static class FbProjectManifestReader
    {
        public sealed class Item { public string Name; public string Kind; public string Status; }

        private const ulong Magic = 98218709832262uL;

        public static List<Item> Read(string path)
        {
            try { return ReadInner(path); }
            catch { return null; } // any misalignment/EOF -> unparseable
        }

        private static List<Item> ReadInner(string path)
        {
            using (var r = new NativeReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
            {
                if (r.ReadULong() != Magic) return null;     // legacy/unknown format
                uint v = r.ReadUInt();
                if (v < 9 || v > 14) return null;

                r.ReadNullTerminatedString();                // profile name
                r.ReadLong(); r.ReadLong();                  // creation/modified ticks
                r.ReadUInt();                                // gameVersion
                for (int i = 0; i < 5; i++) r.ReadNullTerminatedString(); // Title/Author/Category/Version/Description
                int n = r.ReadInt(); if (n > 0) r.ReadBytes(n);          // icon
                for (int i = 0; i < 4; i++) { n = r.ReadInt(); if (n > 0) r.ReadBytes(n); } // 4 screenshots
                r.ReadInt();                                 // trailing/legacy int

                var items = new List<Item>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Action<string, string, string> add = (name, kind, status) =>
                {
                    if (!string.IsNullOrEmpty(name) && seen.Add(kind + "|" + name))
                        items.Add(new Item { Name = name, Kind = kind, Status = status });
                };

                // --- added bundles (not assets) ---
                int c = r.ReadInt();
                for (int i = 0; i < c; i++) { r.ReadNullTerminatedString(); r.ReadNullTerminatedString(); r.ReadInt(); }

                // --- added ebx ---
                c = r.ReadInt();
                for (int i = 0; i < c; i++) { string name = r.ReadNullTerminatedString(); r.ReadGuid(); add(name, "ebx", "added"); }

                // --- added res ---
                c = r.ReadInt();
                for (int i = 0; i < c; i++) { string name = r.ReadNullTerminatedString(); r.ReadULong(); r.ReadUInt(); r.ReadBytes(16); add(name, "res", "added"); }

                // --- added chunk ---
                c = r.ReadInt();
                for (int i = 0; i < c; i++) { Guid id = r.ReadGuid(); r.ReadInt(); add(id.ToString(), "chunk", "added"); }

                // --- modified ebx ---
                c = r.ReadInt();
                for (int i = 0; i < c; i++)
                {
                    string name = r.ReadNullTerminatedString();
                    SkipLinkedAssets(r);
                    if (v >= 13) SkipStringList(r);
                    bool hasMod = r.ReadBoolean();
                    if (hasMod)
                    {
                        r.ReadBoolean();                     // isTransientModified
                        if (v >= 12) r.ReadNullTerminatedString();  // userData
                        if (v < 13) SkipStringList(r);
                        if (v >= 13) r.ReadBoolean();        // is-ModifiedResource flag
                        SkipBlob(r);
                    }
                    add(name, "ebx", "modified");
                }

                // --- modified res ---
                c = r.ReadInt();
                for (int i = 0; i < c; i++)
                {
                    string name = r.ReadNullTerminatedString();
                    SkipLinkedAssets(r);
                    if (v >= 13) SkipStringList(r);
                    bool hasMod = r.ReadBoolean();
                    if (hasMod)
                    {
                        r.ReadBytes(20);                     // sha1
                        r.ReadLong();                        // originalSize
                        int metaLen = r.ReadInt(); if (metaLen > 0) r.ReadBytes(metaLen);
                        if (v >= 12) r.ReadNullTerminatedString();
                        if (v < 13) SkipStringList(r);
                        SkipBlob(r);
                    }
                    add(name, "res", "modified");
                }

                // --- modified chunk ---
                c = r.ReadInt();
                for (int i = 0; i < c; i++)
                {
                    Guid id = r.ReadGuid();
                    if (v >= 13) SkipStringList(r);
                    if (v > 13) { r.ReadInt(); r.ReadInt(); }     // firstMip, h32
                    bool hasMod = true;
                    if (v >= 13) hasMod = r.ReadBoolean();
                    if (hasMod)
                    {
                        r.ReadBytes(20);                     // sha1
                        r.ReadUInt(); r.ReadUInt(); r.ReadUInt(); r.ReadUInt(); // logicalOffset/Size/rangeStart/End
                        if (v < 14) { r.ReadInt(); r.ReadInt(); }
                        r.ReadBoolean();                     // addToChunkBundle
                        if (v >= 12) r.ReadNullTerminatedString();
                        if (v < 13) SkipStringList(r);
                        SkipBlob(r);
                        add(id.ToString(), "chunk", "modified");
                    }
                }

                return items;
            }
        }

        private static void SkipLinkedAssets(NativeReader r)
        {
            int n = r.ReadInt();
            for (int i = 0; i < n; i++)
            {
                string t = r.ReadNullTerminatedString();
                if (t == "chunk") r.ReadGuid();
                else r.ReadNullTerminatedString();           // ebx/res/default all store a name string
            }
        }

        private static void SkipStringList(NativeReader r)
        {
            int n = r.ReadInt();
            for (int i = 0; i < n; i++) r.ReadNullTerminatedString();
        }

        private static void SkipBlob(NativeReader r)
        {
            int len = r.ReadInt();
            if (len > 0) r.ReadBytes(len);
        }
    }
}
