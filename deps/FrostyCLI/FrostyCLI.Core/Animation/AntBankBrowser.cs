using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FrostySdk.IO;

namespace FrostyCLI.Core.Animation
{
    // Generic, schema-driven browser over a GD animation bank (the shared antstate bank and friends).
    // Every GD.DAT2/GD.DATA section is one object: header type id at body+16, field-data offset at body+28.
    // Top-level assets carry __key (8 bytes; the first half of the AntRef guid EBX uses) and __name.
    // Field type 35 is a key reference to another asset; Reference is a tagged pointer to a sub-object
    // (same header layout). This reads any asset by name or key, lists types, and finds who references
    // what - enough to walk a weapon's animation graph from its EBX AntRef down to the clips it plays.
    public sealed class AntBankBrowser : IDisposable
    {
        private readonly Stream _s;
        private readonly NativeReader _r;
        private readonly long _len;
        private readonly AntBankContainer _schema;
        private readonly Dictionary<long, AntBank> _bank = new Dictionary<long, AntBank>();
        private readonly Dictionary<long, int> _keyPos = new Dictionary<long, int>();
        private readonly Dictionary<long, int> _namePos = new Dictionary<long, int>();
        private const int KeyType = 35;

        public readonly List<long> Bodies = new List<long>();
        public readonly Dictionary<long, long> BodyByKey = new Dictionary<long, long>();

        public AntBankBrowser(Stream s)
        {
            _s = s; _r = new NativeReader(s); _len = s.Length;
            _schema = AntBankReader.ParseSchema(s);
            foreach (var kv in _schema.Banks)
            {
                long tv = (long)(uint)(int)kv.Key;
                _bank[tv] = kv.Value;
                int kp = -1, np = -1;
                foreach (var f in kv.Value.Fields)
                {
                    if (f.Name == "__key") kp = f.Position;
                    if (f.Name == "__name") np = f.Position;
                }
                _keyPos[tv] = kp; _namePos[tv] = np;
            }
            _r.Position = _schema.DataSectionStart;
            while (_r.Position + 12 < _len)
            {
                long hdr = _r.Position;
                string tag;
                try { tag = _r.ReadSizedString(7); } catch { break; }
                if (tag.Length < 3 || tag[0] != 'G' || tag[1] != 'D') break;
                Endian en = _r.ReadSizedString(1) == "b" ? Endian.Big : Endian.Little;
                int size = _r.ReadInt(en);
                if (size < 12 || hdr + size > _len) break;
                long body = _r.Position;
                if (tag == "GD.DAT2" || tag == "GD.DATA")
                {
                    Bodies.Add(body);
                    long k = Key(body);
                    if (k != 0 && !BodyByKey.ContainsKey(k)) BodyByKey[k] = body;
                }
                _r.Position = hdr + size;
            }
        }

        public void Dispose() { try { _s.Dispose(); } catch { } }

        // ---- object accessors ----
        public string TypeName(long body) { AntBank b; return _bank.TryGetValue(U32(body + 16), out b) ? b.Name : "?"; }
        public long Key(long body)
        {
            int kp; long tv = U32(body + 16);
            if (!_keyPos.TryGetValue(tv, out kp) || kp < 0) return 0;
            return I64(body + U32(body + 28) + kp);
        }
        public string Name(long body)
        {
            int np; long tv = U32(body + 16);
            if (!_namePos.TryGetValue(tv, out np) || np < 0) return null;
            return ReadStr(body + U32(body + 28) + np);
        }
        public static long KeyOf(Guid g) { return BitConverter.ToInt64(g.ToByteArray(), 0); }
        public static string KeyGuid(long key) { var b = new byte[16]; Array.Copy(BitConverter.GetBytes(key), b, 8); return new Guid(b).ToString(); }

        // Resolve "name", "guid" or "0x<hex key>" to a body.
        public List<long> Find(string what, bool exact)
        {
            var hits = new List<long>();
            Guid g; long body;
            if (Guid.TryParse(what, out g)) { if (BodyByKey.TryGetValue(KeyOf(g), out body)) hits.Add(body); return hits; }
            if (what.StartsWith("0x") && long.TryParse(what.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long k))
            { if (BodyByKey.TryGetValue(k, out body)) hits.Add(body); return hits; }
            foreach (long b in Bodies)
            {
                string n = Name(b); if (n == null) continue;
                if (exact ? string.Equals(n, what, StringComparison.OrdinalIgnoreCase) : n.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(b);
            }
            return hits;
        }

        public string Label(long body)
        {
            if (body < 0) return "null";
            string n = Name(body); long k = Key(body);
            return TypeName(body) + (n != null ? " '" + n + "'" : "") + (k != 0 ? " key=" + KeyGuid(k) : " @" + body.ToString("X"));
        }

        public Dictionary<string, int> TypeCounts()
        {
            var d = new Dictionary<string, int>();
            foreach (long b in Bodies) { string t = TypeName(b); d[t] = d.TryGetValue(t, out int c) ? c + 1 : 1; }
            return d;
        }

        // Every key/reference target an object points at (recursing into inline structs, not into sub-objects).
        public List<KeyValuePair<string, long>> Children(long body)
        {
            var list = new List<KeyValuePair<string, long>>();
            AntBank bank; if (!_bank.TryGetValue(U32(body + 16), out bank)) return list;
            CollectRefs(bank, body + U32(body + 28), "", list, 0);
            return list;
        }

        private void CollectRefs(AntBank bank, long data, string prefix, List<KeyValuePair<string, long>> list, int depth)
        {
            if (depth > 6) return;
            foreach (var f in bank.Fields)
            {
                if (f.Name == "__key" || f.Name == "__name") continue;
                long fp = data + f.Position;
                bool isKey = (int)f.TypeHash == KeyType, isRef = f.TypeHash == BankType.Reference;
                if (!f.IsArray)
                {
                    if (isKey) { long k = I64(fp); if (k != 0) list.Add(new KeyValuePair<string, long>(prefix + f.Name, BodyByKey.TryGetValue(k, out long b) ? b : -k)); }
                    else if (isRef) { long b = Resolve(fp); if (b > 0) list.Add(new KeyValuePair<string, long>(prefix + f.Name, b)); }
                    else { AntBank sb = SubBank(f); if (sb != null) CollectRefs(sb, fp, prefix + f.Name + ".", list, depth + 1); }
                }
                else
                {
                    long cnt, dp; ReadArr(fp, out cnt, out dp);
                    if (cnt <= 0 || cnt > 100000 || dp <= 0) continue;
                    int stride = ElemSize(f);
                    for (long i = 0; i < cnt; i++)
                    {
                        long ep = dp + i * stride;
                        string nm = prefix + f.Name + "[" + i + "]";
                        if (isKey) { long k = I64(ep); if (k != 0) list.Add(new KeyValuePair<string, long>(nm, BodyByKey.TryGetValue(k, out long b) ? b : -k)); }
                        else if (isRef) { long b = Resolve(ep); if (b > 0) list.Add(new KeyValuePair<string, long>(nm, b)); }
                        else { AntBank sb = SubBank(f); if (sb != null) CollectRefs(sb, ep, nm + ".", list, depth + 1); }
                    }
                }
            }
        }

        // Generic field dump. Sub-objects reached through Reference fields are dumped inline (depth-limited);
        // key references print the target's label.
        public List<string> Dump(long body, int maxDepth = 3, int maxArray = 64)
        {
            var lines = new List<string>();
            lines.Add(Label(body));
            DumpObject(body, "  ", lines, 0, maxDepth, maxArray, new HashSet<long>());
            return lines;
        }

        private void DumpObject(long body, string ind, List<string> lines, int depth, int maxDepth, int maxArray, HashSet<long> seen)
        {
            if (!seen.Add(body)) { lines.Add(ind + "(seen " + Label(body) + ")"); return; }
            AntBank bank; if (!_bank.TryGetValue(U32(body + 16), out bank)) { lines.Add(ind + "?type"); return; }
            DumpFields(bank, body + U32(body + 28), ind, lines, depth, maxDepth, maxArray, seen);
        }

        private void DumpFields(AntBank bank, long data, string ind, List<string> lines, int depth, int maxDepth, int maxArray, HashSet<long> seen)
        {
            var fields = (AntBankField[])bank.Fields.Clone();
            Array.Sort(fields, (a, b) => a.Position.CompareTo(b.Position));
            foreach (var f in fields)
            {
                if (f.Name == "__key") continue;
                long fp = data + f.Position;
                if (!f.IsArray)
                {
                    if (f.TypeHash == BankType.Reference && f.Name == "__base")
                    {
                        long b = Resolve(fp);
                        if (b > 0) { lines.Add(ind + "__base: " + TypeName(b)); if (depth < maxDepth + 2) DumpObject(b, ind + "  ", lines, depth, maxDepth, maxArray, seen); }
                        continue;
                    }
                    string v = Value(f, f.Name, fp, ind, lines, depth, maxDepth, maxArray, seen);
                    if (v != "\u0000") lines.Add(ind + f.Name + " = " + v);
                }
                else
                {
                    long cnt, dp; ReadArr(fp, out cnt, out dp);
                    lines.Add(ind + f.Name + " [" + cnt + "] " + ElemTypeName(f));
                    if (cnt <= 0 || cnt > 10000000 || dp <= 0) continue;
                    int stride = ElemSize(f);
                    AntBank sub = SubBank(f);
                    bool numeric = sub == null && f.TypeHash != BankType.Reference && (int)f.TypeHash != KeyType && f.TypeHash != BankType.String;
                    if (numeric)
                    {
                        var sb = new StringBuilder();
                        long n = Math.Min(cnt, maxArray);
                        for (long i = 0; i < n; i++) { if (i > 0) sb.Append(", "); sb.Append(Scalar(f.TypeHash, dp + i * stride, stride)); }
                        if (cnt > n) sb.Append(", ...");
                        lines.Add(ind + "  " + sb);
                        continue;
                    }
                    for (long i = 0; i < Math.Min(cnt, maxArray); i++)
                    {
                        long ep = dp + i * stride;
                        if (sub != null && f.TypeHash != BankType.Reference && (int)f.TypeHash != KeyType)
                        { lines.Add(ind + "  [" + i + "]"); if (depth < maxDepth) DumpFields(sub, ep, ind + "    ", lines, depth + 1, maxDepth, maxArray, seen); }
                        else { string v = Value(f, "[" + i + "]", ep, ind + "  ", lines, depth, maxDepth, maxArray, seen); if (v != "\u0000") lines.Add(ind + "  [" + i + "] " + v); }
                    }
                    if (cnt > maxArray) lines.Add(ind + "  ...");
                }
            }
        }

        private string Value(AntBankField f, string label, long fp, string ind, List<string> lines, int depth, int maxDepth, int maxArray, HashSet<long> seen)
        {
            if ((int)f.TypeHash == KeyType)
            {
                long k = I64(fp); if (k == 0) return "null";
                long b; return BodyByKey.TryGetValue(k, out b) ? "-> " + Label(b) : "-> (missing) " + KeyGuid(k);
            }
            if (f.TypeHash == BankType.Reference)
            {
                long b = Resolve(fp); if (b <= 0) return "null";
                if (Key(b) != 0 || depth >= maxDepth) return "-> " + Label(b);
                // inline sub-object
                int at = lines.Count; lines.Add("");
                DumpObject(b, ind + "  ", lines, depth + 1, maxDepth, maxArray, seen);
                lines.RemoveAt(at);
                lines.Insert(at, ind + f.Name + " = {" + TypeName(b) + "}");
                return "\u0000";
            }
            if (f.TypeHash == BankType.String) { string s = ReadStr(fp); return s == null ? "\"\"" : "\"" + s + "\""; }
            AntBank sb = SubBank(f);
            if (sb != null)
            {
                lines.Add(ind + label + " = {" + sb.Name + "}");
                if (depth < maxDepth) DumpFields(sb, fp, ind + "  ", lines, depth + 1, maxDepth, maxArray, seen);
                return "\u0000";
            }
            return Scalar(f.TypeHash, fp, f.Size);
        }

        private string Scalar(BankType t, long p, int size)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (t)
            {
                case BankType.Boolean: return (U8(p) != 0).ToString();
                case BankType.Int8: return ((sbyte)U8(p)).ToString(ci);
                case BankType.UInt8: return U8(p).ToString(ci);
                case BankType.Int16: return ((short)U16(p)).ToString(ci);
                case BankType.Uint16: return U16(p).ToString(ci);
                case BankType.Int32: return ((int)U32(p)).ToString(ci);
                case BankType.UInt32: return U32(p).ToString(ci);
                case BankType.Int64: return I64(p).ToString(ci);
                case BankType.UInt64: return ((ulong)I64(p)).ToString(ci);
                case BankType.Float: return F(p).ToString("0.#####", ci);
                case BankType.Double: _r.Position = p; return _r.ReadDouble().ToString("0.#####", ci);
                case BankType.Vector2: return "(" + F(p).ToString("0.####", ci) + "," + F(p + 4).ToString("0.####", ci) + ")";
                case BankType.Vector3: return "(" + F(p).ToString("0.####", ci) + "," + F(p + 4).ToString("0.####", ci) + "," + F(p + 8).ToString("0.####", ci) + ")";
                case BankType.Vector4:
                case BankType.Quaternion: return "(" + F(p).ToString("0.####", ci) + "," + F(p + 4).ToString("0.####", ci) + "," + F(p + 8).ToString("0.####", ci) + "," + F(p + 12).ToString("0.####", ci) + ")";
                case BankType.Guid: _r.Position = p; return new Guid(_r.ReadBytes(16)).ToString();
                default:
                    if (size == 1) return "0x" + U8(p).ToString("X2");
                    if (size == 2) return "0x" + U16(p).ToString("X4");
                    if (size == 4) return "0x" + U32(p).ToString("X8") + " (" + F(p).ToString("0.####", ci) + "f)";
                    if (size == 8) return "0x" + I64(p).ToString("X16");
                    return "<" + (int)t + ":" + size + "b>";
            }
        }

        private AntBank SubBank(AntBankField f)
        {
            if (f.Bank != null && f.Bank.Fields.Length > 0) return f.Bank;
            AntBank b; return _bank.TryGetValue((long)(uint)(int)f.TypeHash, out b) && b.Fields.Length > 0 ? b : null;
        }

        private static string ElemTypeName(AntBankField f) { return f.Bank != null ? f.Bank.Name : ((int)f.TypeHash == KeyType ? "key" : f.TypeHash.ToString()); }

        private int ElemSize(AntBankField f)
        {
            if (f.TypeHash == BankType.Reference || (int)f.TypeHash == KeyType) return 8;
            AntBank sb = SubBank(f);
            if (sb != null && sb.Size > 0) return Align(sb.Size, Math.Max(1, sb.Alignment));
            switch (f.TypeHash)
            {
                case BankType.Boolean: case BankType.Int8: case BankType.UInt8: return 1;
                case BankType.Int16: case BankType.Uint16: return 2;
                case BankType.Int32: case BankType.UInt32: case BankType.Float: return 4;
                case BankType.Int64: case BankType.UInt64: case BankType.Double: return 8;
                case BankType.Vector2: return 8;
                case BankType.Vector3: case BankType.Vector4: case BankType.Quaternion: case BankType.Guid: return 16;
                case BankType.Matrix: return 64;
                case BankType.String: return 16;
            }
            return f.Size > 0 ? f.Size : 4;
        }
        private static int Align(int v, int a) { return (v + a - 1) / a * a; }


        // ---- typed helpers ----
        // Address of a named field in an object's data block (searching the __base chain too), or -1.
        public long FieldAddr(long body, string name, out AntBankField field)
        {
            field = null;
            for (int guard = 0; body > 0 && guard < 8; guard++)
            {
                AntBank bank; if (!_bank.TryGetValue(U32(body + 16), out bank)) return -1;
                long data = body + U32(body + 28);
                AntBankField baseF = null;
                foreach (var f in bank.Fields)
                {
                    if (f.Name == name) { field = f; return data + f.Position; }
                    if (f.Name == "__base") baseF = f;
                }
                if (baseF == null) return -1;
                body = Resolve(data + baseF.Position);
            }
            return -1;
        }
        public long RefField(long body, string name)
        {
            AntBankField f; long a = FieldAddr(body, name, out f); if (a < 0) return -1;
            if ((int)f.TypeHash == KeyType) { long k = I64(a); long b; return k != 0 && BodyByKey.TryGetValue(k, out b) ? b : -1; }
            return Resolve(a);
        }
        public List<long> RefArray(long body, string name)
        {
            var list = new List<long>();
            AntBankField f; long a = FieldAddr(body, name, out f); if (a < 0) return list;
            long cnt, dp; ReadArr(a, out cnt, out dp);
            for (long i = 0; i < cnt && dp > 0; i++)
            {
                long ep = dp + i * 8;
                if ((int)f.TypeHash == KeyType) { long k = I64(ep); long b; list.Add(k != 0 && BodyByKey.TryGetValue(k, out b) ? b : -1); }
                else list.Add(Resolve(ep));
            }
            return list;
        }
        public float[] FloatArray(long body, string name)
        {
            AntBankField f; long a = FieldAddr(body, name, out f); if (a < 0) return new float[0];
            long cnt, dp; ReadArr(a, out cnt, out dp);
            var v = new float[Math.Max(0, cnt)];
            for (long i = 0; i < cnt; i++) v[i] = F(dp + i * 4);
            return v;
        }
        // Slot names of a LayoutAsset ("Bone.q", "Bone.t", float dofs).
        public List<string> LayoutSlots(long layout)
        {
            var names = new List<string>();
            AntBankField f; long a = FieldAddr(layout, "Slots", out f); if (a < 0) return names;
            AntBank sb = SubBank(f); if (sb == null) return names;
            int namePos = -1; foreach (var sf in sb.Fields) if (sf.Name == "Name") namePos = sf.Position;
            long cnt, dp; ReadArr(a, out cnt, out dp);
            int stride = ElemSize(f);
            for (long i = 0; i < cnt; i++) names.Add(namePos >= 0 ? ReadStr(dp + i * stride + namePos) : "?");
            return names;
        }
        // Resolved per-slot weights of a BlendMaskListAsset: DefaultDofSetKeys/DefaultValues give whole-layout
        // weights, MaskKeys (BlendMaskAssets) give per-slot weights for their layout.
        public List<KeyValuePair<string, float>> MaskWeights(long bml)
        {
            var res = new List<KeyValuePair<string, float>>();
            var dl = RefArray(bml, "DefaultDofSetKeys"); var dv = FloatArray(bml, "DefaultValues");
            for (int i = 0; i < dl.Count; i++)
                if (dl[i] > 0) res.Add(new KeyValuePair<string, float>("[" + Name(dl[i]) + "] (whole layout)", i < dv.Length ? dv[i] : float.NaN));
            foreach (long bm in RefArray(bml, "MaskKeys"))
            {
                if (bm <= 0) continue;
                long lay = RefField(bm, "DofSetKey"); var vals = FloatArray(bm, "Values");
                var slots = lay > 0 ? LayoutSlots(lay) : new List<string>();
                for (int i = 0; i < vals.Length; i++)
                    res.Add(new KeyValuePair<string, float>("[" + (lay > 0 ? Name(lay) : "?") + "] " + (i < slots.Count ? slots[i] : "#" + i), vals[i]));
            }
            return res;
        }


        // (joint name, parent index) for every joint of a SkeletonAsset, by skeleton name.
        public List<KeyValuePair<string, int>> SkeletonJoints(string skeletonName)
        {
            var res = new List<KeyValuePair<string, int>>();
            long ske = -1;
            foreach (long b in Find(skeletonName, true)) if (TypeName(b) == "SkeletonAsset") { ske = b; break; }
            if (ske < 0) return res;
            AntBankField f; long a = FieldAddr(ske, "Joints", out f); if (a < 0) return res;
            AntBank jb = SubBank(f); if (jb == null) return res;
            int np = -1, pp = -1; AntBankField pf = null;
            foreach (var jf in jb.Fields) { if (jf.Name == "JointName") np = jf.Position; if (jf.Name == "ParentIndex") { pp = jf.Position; pf = jf; } }
            long cnt, dp; ReadArr(a, out cnt, out dp);
            int stride = ElemSize(f);
            for (long i = 0; i < cnt; i++)
            {
                long ep = dp + i * stride;
                int parent = pf == null ? -1 : (pf.TypeHash == BankType.Int16 || pf.Size == 2 ? (short)U16(ep + pp) : (int)U32(ep + pp));
                res.Add(new KeyValuePair<string, int>(np >= 0 ? ReadStr(ep + np) : "?", parent));
            }
            return res;
        }

        // Every object reachable from `roots` through key/reference fields (sub-objects included).
        public HashSet<long> Reachable(IEnumerable<long> roots)
        {
            var seen = new HashSet<long>(); var stack = new Stack<long>();
            foreach (long r in roots) stack.Push(r);
            while (stack.Count > 0)
            {
                long b = stack.Pop(); if (b <= 0 || !seen.Add(b)) continue;
                foreach (var kv in Children(b)) if (kv.Value > 0 && !seen.Contains(kv.Value)) stack.Push(kv.Value);
            }
            return seen;
        }
        // File offset of element i of a key/reference array field (8 bytes each), or -1.
        public long ArrayElementAddr(long body, string name, int i)
        {
            AntBankField f; long a = FieldAddr(body, name, out f); if (a < 0) return -1;
            long cnt, dp; ReadArr(a, out cnt, out dp);
            if (i < 0 || i >= cnt || dp <= 0) return -1;
            return dp + (long)i * 8;
        }
        public long ReadKeyAt(long addr) { return I64(addr); }

        // ---- raw readers ----
        private long U32(long p) { _r.Position = p; return _r.ReadUInt(Endian.Little); }
        private int U16(long p) { _r.Position = p; return _r.ReadUShort(Endian.Little); }
        private int U8(long p) { _r.Position = p; return _r.ReadByte(); }
        private long I64(long p) { _r.Position = p; return _r.ReadLong(Endian.Little); }
        private float F(long p) { _r.Position = p; return _r.ReadFloat(); }
        private long Resolve(long ptrPos)
        {
            if (ptrPos < 0 || ptrPos + 8 > _len) return -1;
            long raw = I64(ptrPos);
            long low = raw & 0x0FFFFFFFFFFFFFFFL;
            int tag = (int)((ulong)raw >> 60);
            if (tag == 1) return ptrPos + low;
            if (tag == 0) return low == 0 ? -1 : low;
            return -1;
        }
        private void ReadArr(long fp, out long count, out long dataPos)
        {
            count = U32(fp);
            dataPos = Resolve(fp + 8);
        }
        private string ReadStr(long fp)
        {
            long len = U32(fp);
            if (len <= 0 || len > 4096) return null;
            long p = Resolve(fp + 8);
            if (p <= 0 || p + len > _len) return null;
            _r.Position = p;
            byte[] bytes = _r.ReadBytes((int)len);
            int n = bytes.Length; while (n > 0 && bytes[n - 1] == 0) n--;
            return Encoding.ASCII.GetString(bytes, 0, n);
        }
    }
}
