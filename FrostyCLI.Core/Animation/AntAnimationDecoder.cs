using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FrostySdk.IO;

namespace FrostyCLI.Core.Animation
{
    // Decodes Frostbite "Ant" hero-strike clips from an antstate resource bank (the GD container)
    // into per-bone keyframes. Reverse-engineered format; see memory/anim-export-architecture.md.
    //
    // Pipeline: strike AntRef guid -> ActorControllerAsset instance (by __key) -> Tracks ->
    // ClipControllerAsset.Anim -> RawAnimationAsset (uncompressed float channels) -> __base
    // AnimationAsset (Additive/EndFrame/ChannelToDofAsset.DofIds) -> map each channel's DofId to a
    // bone+component via the RigAsset dof registry (Default{Vector4,Vector3}Values paired with the
    // rig layout's .q/.t slot names) -> emit SEAnim.
    //
    // Only the uncompressed RawAnimationAsset/FrameAnimationAsset codecs are handled (148/152 hero
    // strikes are Raw). Dct/Vbr/Curve clips return false (caller writes a keyframe-empty file).
    public sealed class AntAnimationDecoder : IDisposable
    {
        private readonly Stream _stream;
        private readonly NativeReader _r;
        private readonly AntBankContainer _schema;
        private readonly long _streamLen;

        private readonly Dictionary<long, long> _bodyByKey = new Dictionary<long, long>();
        private readonly Dictionary<long, AntBank> _bankByType = new Dictionary<long, AntBank>();
        private readonly Dictionary<long, string> _typeName = new Dictionary<long, string>();
        private readonly Dictionary<long, int> _keyPosByType = new Dictionary<long, int>();
        private readonly Dictionary<string, int> _fieldPos = new Dictionary<string, int>();

        // DofId -> (boneName, isRotation). Built from the humanoid rig.
        private readonly Dictionary<uint, DofTarget> _rigDof = new Dictionary<uint, DofTarget>();

        private static readonly HashSet<string> CodecNames = new HashSet<string>(StringComparer.Ordinal)
            { "RawAnimationAsset", "FrameAnimationAsset", "DctAnimationAsset", "VbrAnimationAsset", "CurveAnimationAsset" };

        private struct DofTarget { public string Bone; public bool Rotation; }

        public int RigDofCount { get { return _rigDof.Count; } }

        public AntAnimationDecoder(Stream bankStream, string rigName = "Walrus_HM_Rig")
        {
            _stream = bankStream;
            _r = new NativeReader(bankStream);
            _schema = AntBankReader.ParseSchema(bankStream);
            _streamLen = bankStream.Length;
            foreach (var kv in _schema.Banks)
            {
                long tv = (long)(uint)(int)kv.Key;
                _bankByType[tv] = kv.Value;
                _typeName[tv] = kv.Value.Name;
                int kp = -1;
                foreach (var f in kv.Value.Fields) if (f.Name == "__key") { kp = f.Position; break; }
                _keyPosByType[tv] = kp;
            }
            var rigBodies = BuildIndex();
            BuildRigDofMap(rigBodies, rigName);
        }

        // ---- public API -----------------------------------------------------

        // Decodes the clip for a strike's controller AntRef guid into `anim` (whose Bones are
        // pre-created in skeleton order). Returns true if keyframes were produced.
        public bool TryDecodeClip(Guid controllerGuid, SeAnim anim)
        {
            long key = BitConverter.ToInt64(controllerGuid.ToByteArray(), 0);
            long ctrlBody;
            if (!_bodyByKey.TryGetValue(key, out ctrlBody)) return false;

            long codecBody;
            string codec = FindCodec(ctrlBody, out codecBody);
            if (codec != "RawAnimationAsset") return false; // only uncompressed Raw for now

            return DecodeRaw(codecBody, anim);
        }

        // ---- FacePoser (FACS facial) — see memory/faceposer-facs-decode.md ----
        // The facial layer stores per-frame FACS action-unit weights; a quantized pose library
        // (FacePoseLibraryAsset) holds, per pose, sparse joint q/t targets; the engine blends them
        // onto the FACIAL_LOD1_* joints. This reader loads the library so we can run that blend.
        public sealed class FacePoseLib
        {
            public int NumPoses;
            public float WeightScale;
            public string[] QuatBone;     // quat-dof index -> FACIAL bone name
            public string[] TransBone;    // trans-dof index -> FACIAL bone name
            public int[][] QDof;          // [pose] -> quat-dof indices touched
            public float[][] QVal;        // [pose] -> dequant x,y,z,w per touched dof (len = 4*QDof)
            public int[][] TDof;
            public float[][] TVal;        // [pose] -> dequant x,y,z (len = 3*TDof)
            public Dictionary<uint, int> PoseByDof = new Dictionary<uint, int>(); // clip scalar dofId -> pose idx
        }

        public FacePoseLib BuildFacePoseLib(string libName = null)
        {
            long libType = -1; foreach (var kv in _bankByType) if (kv.Value.Name == "FacePoseLibraryAsset") { libType = kv.Key; break; }
            if (libType < 0) return null;
            long lib = -1;
            foreach (long b in ScanType(libType)) { if (libName == null || ReadName(b) == libName) { lib = b; break; } }
            if (lib < 0) return null;
            uint dof = U32(lib + 28); long bi = lib + dof;

            var L = new FacePoseLib();
            L.NumPoses = (int)U32(bi + FieldPos("FacePoseLibraryAsset", "FacePoseNumPoses"));
            _r.Position = bi + FieldPos("FacePoseLibraryAsset", "WeightScaleFactor"); L.WeightScale = _r.ReadFloat();

            float[] qMin = ReadFloats(bi + FieldPos("FacePoseLibraryAsset", "QuaternionMin"), 4);
            float[] qDiff = ReadFloats(bi + FieldPos("FacePoseLibraryAsset", "QuaternionDiff"), 4);
            float[] tMin = ReadFloats(bi + FieldPos("FacePoseLibraryAsset", "TranslationMin"), 3);
            float[] tDiff = ReadFloats(bi + FieldPos("FacePoseLibraryAsset", "TranslationDiff"), 3);

            // dof index -> FACIAL bone, from FacePoseJointDofsAsset.LayoutAssets[0] slots (.q / .t).
            long jdKey = I64(bi + FieldPos("FacePoseLibraryAsset", "FacePoseJointDofsAsset")); long jdBody;
            var qBones = new List<string>(); var tBones = new List<string>();
            if (_bodyByKey.TryGetValue(jdKey, out jdBody))
            {
                uint jdof = U32(jdBody + 28); long ji = jdBody + jdof;
                long lac, lap; ReadArr(ji + FieldPos("FacePoseJointDofsAsset", "LayoutAssets"), out lac, out lap);
                if (lac > 0)
                {
                    long lk = I64(lap); long lb;
                    if (_bodyByKey.TryGetValue(lk, out lb))
                    {
                        uint ld = U32(lb + 28); long sc, sp; ReadArr(lb + ld + FieldPos("LayoutAsset", "Slots"), out sc, out sp);
                        int leName = FieldPos("LayoutEntry", "Name");
                        for (long j = 0; j < sc; j++)
                        {
                            string nm = ReadStr(sp + j * 24 + leName); if (nm == null) continue;
                            if (nm.EndsWith(".q", StringComparison.Ordinal)) qBones.Add(nm.Substring(0, nm.Length - 2));
                            else if (nm.EndsWith(".t", StringComparison.Ordinal)) tBones.Add(nm.Substring(0, nm.Length - 2));
                        }
                    }
                }
            }
            L.QuatBone = qBones.ToArray(); L.TransBone = tBones.ToArray();

            // Sparse per-pose data: PoseOffsetList = per-pose entry COUNT; build prefix offsets.
            ushort[] qList = ReadU16Array(bi + FieldPos("FacePoseLibraryAsset", "FacePoseQuaternionList"));
            byte[] qDofIdx = ReadU8Array(bi + FieldPos("FacePoseLibraryAsset", "FacePoseQuaternionDofIndexList"));
            byte[] qPoseCnt = ReadU8Array(bi + FieldPos("FacePoseLibraryAsset", "FacePoseQuaternionPoseOffsetList"));
            ushort[] tList = ReadU16Array(bi + FieldPos("FacePoseLibraryAsset", "FacePoseTranslationList"));
            byte[] tDofIdx = ReadU8Array(bi + FieldPos("FacePoseLibraryAsset", "FacePoseTranslationDofIndexList"));
            byte[] tPoseCnt = ReadU8Array(bi + FieldPos("FacePoseLibraryAsset", "FacePoseTranslationPoseOffsetList"));

            L.QDof = new int[L.NumPoses][]; L.QVal = new float[L.NumPoses][];
            L.TDof = new int[L.NumPoses][]; L.TVal = new float[L.NumPoses][];
            int qoff = 0, toff = 0;
            for (int p = 0; p < L.NumPoses; p++)
            {
                int qn = p < qPoseCnt.Length ? qPoseCnt[p] : 0;
                L.QDof[p] = new int[qn]; L.QVal[p] = new float[qn * 4];
                for (int k = 0; k < qn; k++)
                {
                    L.QDof[p][k] = (qoff + k) < qDofIdx.Length ? qDofIdx[qoff + k] : 0;
                    for (int c = 0; c < 4; c++) { int idx = (qoff + k) * 4 + c; L.QVal[p][k * 4 + c] = idx < qList.Length ? qMin[c] + qDiff[c] * (qList[idx] / 65535f) : (c == 3 ? 1f : 0f); }
                }
                qoff += qn;
                int tn = p < tPoseCnt.Length ? tPoseCnt[p] : 0;
                L.TDof[p] = new int[tn]; L.TVal[p] = new float[tn * 3];
                for (int k = 0; k < tn; k++)
                {
                    L.TDof[p][k] = (toff + k) < tDofIdx.Length ? tDofIdx[toff + k] : 0;
                    for (int c = 0; c < 3; c++) { int idx = (toff + k) * 3 + c; L.TVal[p][k * 3 + c] = idx < tList.Length ? tMin[c] + tDiff[c] * (tList[idx] / 65535f) : 0f; }
                }
                toff += tn;
            }

            // clip scalar dofId -> pose index: Walrus scalar slot name's trailing _NN = pose NN-1.
            foreach (var kv in BuildScalarSlotNames())
            {
                int us = kv.Value.LastIndexOf('_');
                if (us >= 0 && us + 1 < kv.Value.Length && int.TryParse(kv.Value.Substring(us + 1), out int nn) && nn >= 1 && nn <= L.NumPoses)
                    L.PoseByDof[kv.Key] = nn - 1;
            }
            return L;
        }

        // Walrus rig scalar (non-.q/.t) slot dofId -> slot name.
        private Dictionary<uint, string> BuildScalarSlotNames()
        {
            var map = new Dictionary<uint, string>();
            long rigType = -1; foreach (var kv in _bankByType) if (kv.Value.Name == "RigAsset") { rigType = kv.Key; break; }
            int rigNamePos = FieldPos("RigAsset", "__name");
            int slotsPos = FieldPos("LayoutAsset", "Slots"); int leName = FieldPos("LayoutEntry", "Name");
            foreach (long rb in ScanType(rigType))
            {
                uint rd = U32(rb + 28); if (ReadStr(rb + rd + rigNamePos) != "Walrus_HM_Rig") continue;
                long ri = rb + rd;
                uint[] dofIds = ReadU32Array(ri + FieldPos("RigAsset", "DofIds"));
                uint[] setStart = ReadU32Array(ri + FieldPos("RigAsset", "DofSetIdIndices"));
                long rdsC, rdsP; ReadArr(ri + FieldPos("RigAsset", "RigDofSets"), out rdsC, out rdsP);
                for (long s = 0; s < rdsC; s++)
                {
                    long lk = I64(rdsP + s * 8); long lb; if (!_bodyByKey.TryGetValue(lk, out lb)) continue;
                    uint ld = U32(lb + 28); long sc, sp; ReadArr(lb + ld + slotsPos, out sc, out sp);
                    long baseIdx = s < setStart.Length ? setStart[s] : 0;
                    for (long j = 0; j < sc; j++)
                    {
                        long di = baseIdx + j; if (di < 0 || di >= dofIds.Length) break;
                        string nm = ReadStr(sp + j * 24 + leName); if (nm == null) continue;
                        if (!nm.EndsWith(".q", StringComparison.Ordinal) && !nm.EndsWith(".t", StringComparison.Ordinal) && !map.ContainsKey(dofIds[di])) map[dofIds[di]] = nm;
                    }
                }
                break;
            }
            return map;
        }

        // Decodes a facial-layer clip by name (e.g. "Face_Idle_01"): reads its per-frame FACS pose
        // weights and blends the pose library onto FACIAL_LOD1_* joints, filling rot+pos keys on the
        // matching bones in `anim`. bindRot/bindTrans = the skeleton's neutral facial LocalPose (the
        // zero point; pose values are deltas). Returns true if facial keys were produced.
        public bool DecodeFaceClip(string clipName, SeAnim anim, FacePoseLib L,
            IDictionary<string, SeQuat> bindRot, IDictionary<string, SeVec3> bindTrans)
        {
            if (L == null) return false;
            long rawBody = FindClipRawByName(clipName);
            if (rawBody < 0) return false;
            uint rawDof = U32(rawBody + 28); long ri = rawBody + rawDof;
            int quatCount = U16(ri + FieldPos("RawAnimationAsset", "QuatCount"));
            int vec3Count = U16(ri + FieldPos("RawAnimationAsset", "Vec3Count"));
            int floatCount = U16(ri + FieldPos("RawAnimationAsset", "FloatCount"));
            long ktCount, ktPos; ReadArr(ri + FieldPos("RawAnimationAsset", "KeyTimes"), out ktCount, out ktPos);
            long dCount, dPos; ReadArr(ri + FieldPos("RawAnimationAsset", "Data"), out dCount, out dPos);
            long constCount, constPos; ReadArr(ri + FieldPos("RawAnimationAsset", "ConstData"), out constCount, out constPos);
            if (ktCount <= 0) return false;
            int numKeys = (int)ktCount;
            int[] frames = new int[numKeys]; _r.Position = ktPos; for (int i = 0; i < numKeys; i++) frames[i] = _r.ReadShort(Endian.Little);
            int perKeySum = quatCount * 4 + vec3Count * 4 + floatCount;
            int perKey = (numKeys > 0 && dCount > 0 && dCount % numKeys == 0 && (dCount / numKeys) >= perKeySum) ? (int)(dCount / numKeys) : perKeySum;

            long baseRef = Resolve(rawBody + rawDof + FieldPos("RawAnimationAsset", "__base"));
            int frameCount = numKeys > 0 ? frames[numKeys - 1] + 1 : numKeys;
            uint[] dofIds = null;
            if (baseRef > 0)
            {
                uint aDof = U32(baseRef + 28); long ai = baseRef + aDof;
                int endFrame = U16(ai + FieldPos("AnimationAsset", "EndFrame")); if (endFrame + 1 > frameCount) frameCount = endFrame + 1;
                long c2dKey = I64(ai + FieldPos("AnimationAsset", "ChannelToDofAsset")); long c2dBody;
                if (_bodyByKey.TryGetValue(c2dKey, out c2dBody)) dofIds = ReadU32Array(c2dBody + U32(c2dBody + 28) + FieldPos("ChannelToDofAsset", "DofIds"));
            }
            if (dofIds == null) return false;
            int[] map = ReadShortArrayAsInt(ri + FieldPos("RawAnimationAsset", "MappingIndices"));
            anim.FrameCount = Math.Max(anim.FrameCount, frameCount);

            int animatedCount = quatCount + vec3Count + floatCount;
            // stored float channel f (c = quatCount+vec3Count+f) -> pose index (via dof). Animated weights.
            var poseOfFloat = new int[floatCount]; for (int f = 0; f < floatCount; f++) poseOfFloat[f] = -1;
            for (int f = 0; f < floatCount; f++) { uint d = DofForChannel(dofIds, map, quatCount + vec3Count + f); if (L.PoseByDof.TryGetValue(d, out int pi)) poseOfFloat[f] = pi; }
            // constant pose weights (rare): channel animatedCount+ci -> pose, held value = first const float.
            var constPoseWeight = new Dictionary<int, float>();
            int constChannels = (int)(constCount / 4);
            for (int ci = 0; ci < constChannels; ci++) { uint d = DofForChannel(dofIds, map, animatedCount + ci); if (L.PoseByDof.TryGetValue(d, out int pi)) { _r.Position = constPos + (long)ci * 16; constPoseWeight[pi] = _r.ReadFloat(); } }

            var trackByName = new Dictionary<string, SeAnimBone>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < anim.Bones.Count; i++) trackByName[anim.Bones[i].Name] = anim.Bones[i];

            int nq = L.QuatBone.Length, nt = L.TransBone.Length;
            var weights = new float[L.NumPoses];
            bool any = false;
            for (int k = 0; k < numKeys; k++)
            {
                Array.Clear(weights, 0, weights.Length);
                foreach (var kv in constPoseWeight) if (kv.Key < weights.Length) weights[kv.Key] = kv.Value * L.WeightScale;
                int floatBase = quatCount * 4 + vec3Count * 4;
                for (int f = 0; f < floatCount; f++)
                {
                    int p = poseOfFloat[f]; if (p < 0) continue;
                    _r.Position = dPos + ((long)k * perKey + floatBase + f) * 4;
                    weights[p] = _r.ReadFloat() * L.WeightScale;
                }
                // accumulate delta quats (identity-centered) and trans deltas
                var qx = new float[nq]; var qy = new float[nq]; var qz = new float[nq]; var qw = new float[nq];
                for (int d = 0; d < nq; d++) qw[d] = 1f; // identity baseline
                var tx = new float[nt]; var ty = new float[nt]; var tz = new float[nt];
                for (int p = 0; p < L.NumPoses; p++)
                {
                    float w = weights[p]; if (w == 0f) continue;
                    int[] qd = L.QDof[p]; float[] qv = L.QVal[p];
                    for (int kk = 0; kk < qd.Length; kk++) { int d = qd[kk]; if (d >= nq) continue; qx[d] += w * qv[kk * 4]; qy[d] += w * qv[kk * 4 + 1]; qz[d] += w * qv[kk * 4 + 2]; qw[d] += w * (qv[kk * 4 + 3] - 1f); }
                    int[] td = L.TDof[p]; float[] tv = L.TVal[p];
                    for (int kk = 0; kk < td.Length; kk++) { int d = td[kk]; if (d >= nt) continue; tx[d] += w * tv[kk * 3]; ty[d] += w * tv[kk * 3 + 1]; tz[d] += w * tv[kk * 3 + 2]; }
                }
                // emit rotation: bind_q (x) normalized delta
                for (int d = 0; d < nq; d++)
                {
                    string bone = L.QuatBone[d]; SeAnimBone trk;
                    if (!trackByName.TryGetValue(bone, out trk)) continue;
                    float dl = (float)Math.Sqrt(qx[d] * qx[d] + qy[d] * qy[d] + qz[d] * qz[d] + qw[d] * qw[d]); if (dl < 1e-8f) dl = 1f;
                    float dx = qx[d] / dl, dy = qy[d] / dl, dz = qz[d] / dl, dw = qw[d] / dl;
                    SeQuat bq; if (!bindRot.TryGetValue(bone, out bq)) bq = SeQuat.Identity;
                    // emit = bind (x) delta  (Hamilton product, xyzw)
                    float ex = bq.W * dx + bq.X * dw + bq.Y * dz - bq.Z * dy;
                    float ey = bq.W * dy - bq.X * dz + bq.Y * dw + bq.Z * dx;
                    float ez = bq.W * dz + bq.X * dy - bq.Y * dx + bq.Z * dw;
                    float ew = bq.W * dw - bq.X * dx - bq.Y * dy - bq.Z * dz;
                    trk.RotKeys.Add(new SeRotKey { Frame = frames[k], Rotation = new SeQuat(ex, ey, ez, ew) });
                    any = true;
                }
                // emit translation: bind_t + delta
                for (int d = 0; d < nt; d++)
                {
                    string bone = L.TransBone[d]; SeAnimBone trk;
                    if (!trackByName.TryGetValue(bone, out trk)) continue;
                    SeVec3 bt; if (bindTrans == null || !bindTrans.TryGetValue(bone, out bt)) bt = new SeVec3(0, 0, 0);
                    trk.PosKeys.Add(new SePosKey { Frame = frames[k], Position = new SeVec3(bt.X + tx[d], bt.Y + ty[d], bt.Z + tz[d]) });
                }
            }
            return any;
        }

        // Debug/inspection: every ClipControllerAsset whose __name contains `filter` (case-insensitive),
        // with its codec (resolved through Anim) and its controller key as a guid-style string.
        public List<string> DescribeClipsByName(string filter)
        {
            var lines = new List<string>();
            long clipType = -1; foreach (var kv in _bankByType) if (kv.Value.Name == "ClipControllerAsset") { clipType = kv.Key; break; }
            if (clipType < 0) { lines.Add("no ClipControllerAsset type in bank"); return lines; }
            int namePos = FieldPos("ClipControllerAsset", "__name");
            int fpsPos = FieldPos("ClipControllerAsset", "Fps");
            int keyPos; _keyPosByType.TryGetValue(clipType, out keyPos);
            foreach (long body in ScanType(clipType))
            {
                uint dof = U32(body + 28);
                string name = ReadStr(body + dof + namePos) ?? "";
                if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                long codecBody; string codec = FindCodec(body, out codecBody);
                long key = keyPos >= 0 ? I64(body + dof + keyPos) : 0;
                byte[] kb = new byte[16]; Array.Copy(BitConverter.GetBytes(key), kb, 8);
                // Frame count and rate, so a state-machine CharacterStateClipControllerData pointed at this clip
                // can be given a Length that plays the whole thing (frames = endFrame + 1).
                string extra = "";
                try
                {
                    if (codecBody >= 0)
                    {
                        long baseRef = Resolve(codecBody + U32(codecBody + 28) + FieldPos(codec, "__base"));
                        if (baseRef > 0) extra += "  endFrame=" + U16(baseRef + U32(baseRef + 28) + FieldPos("AnimationAsset", "EndFrame"));
                    }
                    if (fpsPos >= 0) extra += "  fps=" + ReadFloats(body + dof + fpsPos, 1)[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                catch { }
                lines.Add(name + "  codec=" + (codec ?? "?") + "  key=" + new Guid(kb) + extra);
            }
            return lines;
        }

        // Debug: dump a clip (by exact controller name) whose Anim is a FrameAnimationAsset or RawAnimationAsset:
        // the codec record's schema fields, counts, and every channel as dofId -> rig bone -> value(s) at key 0.
        public List<string> DumpClipByName(string clipName)
        {
            var lines = new List<string>();
            long clipType = -1; foreach (var kv in _bankByType) if (kv.Value.Name == "ClipControllerAsset") { clipType = kv.Key; break; }
            int namePos = FieldPos("ClipControllerAsset", "__name");
            long ctrl = -1;
            foreach (long body in ScanType(clipType))
                if (string.Equals(ReadStr(body + U32(body + 28) + namePos), clipName, StringComparison.OrdinalIgnoreCase)) { ctrl = body; break; }
            if (ctrl < 0) { lines.Add("clip not found"); return lines; }
            long codecBody; string codec = FindCodec(ctrl, out codecBody);
            lines.Add("codec " + codec);
            if (codecBody < 0 || (codec != "FrameAnimationAsset" && codec != "RawAnimationAsset")) return lines;
            AntBank bank = _bankByType[U32(codecBody + 16)];
            foreach (var f in bank.Fields) lines.Add("  field " + f.Name + " pos " + f.Position + " type " + f.TypeHash + (f.IsArray ? " []" : ""));
            uint cdof = U32(codecBody + 28); long ci = codecBody + cdof;
            int qc = U16(ci + FieldPos(codec, "QuatCount")), vc = U16(ci + FieldPos(codec, "Vec3Count")), fc = U16(ci + FieldPos(codec, "FloatCount"));
            long dCount, dPos; ReadArr(ci + FieldPos(codec, "Data"), out dCount, out dPos);
            lines.Add("QuatCount " + qc + " Vec3Count " + vc + " FloatCount " + fc + " Data " + dCount);
            long baseRef = Resolve(codecBody + cdof + FieldPos(codec, "__base"));
            uint[] dofIds = new uint[0];
            if (baseRef > 0)
            {
                long ai = baseRef + U32(baseRef + 28);
                bool additive = (U32(ai + FieldPos("AnimationAsset", "Additive")) & 0xFF) != 0;
                lines.Add("additive " + additive + " endFrame " + U16(ai + FieldPos("AnimationAsset", "EndFrame")));
                long c2dBody;
                if (_bodyByKey.TryGetValue(I64(ai + FieldPos("AnimationAsset", "ChannelToDofAsset")), out c2dBody))
                {
                    long idCount, idPos; ReadArr(c2dBody + U32(c2dBody + 28) + FieldPos("ChannelToDofAsset", "DofIds"), out idCount, out idPos);
                    dofIds = new uint[idCount]; _r.Position = idPos;
                    for (int i = 0; i < idCount; i++) dofIds[i] = _r.ReadUInt(Endian.Little);
                }
            }
            lines.Add("dofIds " + dofIds.Length);
            int[] map = null;
            try { int mp = FieldPos(codec, "MappingIndices"); if (mp >= 0) map = ReadShortArrayAsInt(ci + mp); } catch { }
            lines.Add("mapping " + (map == null ? "none" : map.Length.ToString()));
            for (int c = 0; c < qc + vc + fc && c < dofIds.Length; c++)
            {
                uint dof = DofForChannel(dofIds, map, c);
                DofTarget t; string bone = _rigDof.TryGetValue(dof, out t) ? t.Bone + (t.Rotation ? ".q" : ".t") : "?";
                string kind = c < qc ? "quat" : c < qc + vc ? "vec3" : "float";
                int col = c < qc + vc ? c * 4 : (qc + vc) * 4 + (c - qc - vc);
                string val;
                if (kind == "float") val = ReadFloats(dPos + col * 4, 1)[0].ToString("0.####");
                else { float[] v = ReadFloats(dPos + col * 4, 4); val = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.####},{1:0.####},{2:0.####},{3:0.####}", v[0], v[1], v[2], v[3]); }
                lines.Add("  ch" + c + " " + kind + " dof " + dof.ToString("X8") + " " + bone + " = " + val);
            }
            return lines;
        }

        // Adds (dx,dy,dz) to one bone's translation channel in a FrameAnimationAsset pose (found by exact clip
        // controller name), in place: the Frame codec's Data is a flat float array (4-float lanes for quat/vec3
        // channels), so this changes three floats and nothing structural. Returns the patched bank copy, or null
        // with `error` set. `before`/`after` report the channel value.
        public byte[] PatchFrameTranslation(byte[] bank, string clipName, string bone, float dx, float dy, float dz,
                                            out string error, out float[] before, out float[] after)
        {
            error = null; before = after = null;
            long clipType = -1; foreach (var kv in _bankByType) if (kv.Value.Name == "ClipControllerAsset") { clipType = kv.Key; break; }
            int namePos = FieldPos("ClipControllerAsset", "__name");
            long ctrl = -1;
            foreach (long body in ScanType(clipType))
                if (string.Equals(ReadStr(body + U32(body + 28) + namePos), clipName, StringComparison.OrdinalIgnoreCase)) { ctrl = body; break; }
            if (ctrl < 0) { error = "clip not found: " + clipName; return null; }
            long fb; string codec = FindCodec(ctrl, out fb);
            if (codec != "FrameAnimationAsset") { error = "clip codec is " + codec + ", not FrameAnimationAsset"; return null; }
            long fi = fb + U32(fb + 28);
            int qc = (int)U32(fi + FieldPos(codec, "QuatCount")), vc = (int)U32(fi + FieldPos(codec, "Vec3Count"));
            long dCount, dPos; ReadArr(fi + FieldPos(codec, "Data"), out dCount, out dPos);
            long baseRef = Resolve(fb + U32(fb + 28) + FieldPos(codec, "__base"));
            long ai = baseRef + U32(baseRef + 28);
            long c2dBody;
            if (!_bodyByKey.TryGetValue(I64(ai + FieldPos("AnimationAsset", "ChannelToDofAsset")), out c2dBody)) { error = "no ChannelToDofAsset"; return null; }
            long idCount, idPos; ReadArr(c2dBody + U32(c2dBody + 28) + FieldPos("ChannelToDofAsset", "DofIds"), out idCount, out idPos);
            // Match through the union dof map (dofId -> bone), which covers the first-person rig too (SpineX etc.).
            int chan = -1;
            for (int c = qc; c < qc + vc && c < idCount; c++)
            {
                _r.Position = idPos + c * 4;
                DofTarget t;
                if (_rigDof.TryGetValue(_r.ReadUInt(Endian.Little), out t) && !t.Rotation && string.Equals(t.Bone, bone, StringComparison.OrdinalIgnoreCase)) { chan = c; break; }
            }
            if (chan < 0) { error = bone + ".t is not a channel of " + clipName; return null; }
            long off = dPos + (long)chan * 4 * 4;
            if (off + 12 > bank.Length) { error = "channel offset outside the bank"; return null; }
            byte[] outBank = (byte[])bank.Clone();
            before = new[] { BitConverter.ToSingle(bank, (int)off), BitConverter.ToSingle(bank, (int)off + 4), BitConverter.ToSingle(bank, (int)off + 8) };
            after = new[] { before[0] + dx, before[1] + dy, before[2] + dz };
            for (int k = 0; k < 3; k++) PutF(outBank, off + k * 4, after[k]);
            // Vec3 lanes are 4 floats; the exporter splats x into the 4th (w == x). Keep that convention.
            if (BitConverter.ToSingle(bank, (int)off + 12) == before[0]) PutF(outBank, off + 12, after[0]);
            return outBank;
        }


        // Copies one FrameAnimationAsset pose's Data over another's, in place. Both clips must map their channels
        // through the same ChannelToDofAsset with the same counts, so the float layout is identical.
        // With different channel tables, channels are matched by dof id (quat to quat, vec3 to vec3); target
        // channels with no source counterpart keep their values and are counted in `unmatched`.
        public byte[] CopyFramePose(byte[] bank, string fromClip, string toClip, out string error, out int copied, out int unmatched)
        {
            error = null; copied = unmatched = 0;
            long fd, fc, fdof, td, tc, tdof; int fq, fv, tq, tv;
            if (!FrameDataInfo(fromClip, out fd, out fc, out fdof, out fq, out fv, out error)) return null;
            if (!FrameDataInfo(toClip, out td, out tc, out tdof, out tq, out tv, out error)) return null;
            byte[] outBank = (byte[])bank.Clone();
            if (fdof == tdof && fc == tc) { Array.Copy(bank, fd, outBank, td, fc * 4); copied = fq + fv; return outBank; }
            uint[] fids = DofIdsOf(fdof), tids = DofIdsOf(tdof);
            if (fids == null || tids == null) { error = "ChannelToDofAsset not found"; return null; }
            var src = new Dictionary<ulong, int>();
            for (int c = 0; c < fq + fv && c < fids.Length; c++) src[((ulong)(c < fq ? 1u : 2u) << 32) | fids[c]] = c;
            for (int c = 0; c < tq + tv && c < tids.Length; c++)
            {
                int s;
                if (src.TryGetValue(((ulong)(c < tq ? 1u : 2u) << 32) | tids[c], out s)) { Array.Copy(bank, fd + (long)s * 16, outBank, td + (long)c * 16, 16); copied++; }
                else unmatched++;
            }
            return outBank;
        }

        private uint[] DofIdsOf(long c2dKey)
        {
            long body; if (!_bodyByKey.TryGetValue(c2dKey, out body)) return null;
            long n, p; ReadArr(body + U32(body + 28) + FieldPos("ChannelToDofAsset", "DofIds"), out n, out p);
            var ids = new uint[n]; _r.Position = p;
            for (long i = 0; i < n; i++) ids[i] = _r.ReadUInt(Endian.Little);
            return ids;
        }

        private bool FrameDataInfo(string clipName, out long dataPos, out long count, out long c2dKey, out int quats, out int vec3s, out string error)
        {
            dataPos = count = c2dKey = 0; quats = vec3s = 0; error = null;
            long clipType = -1; foreach (var kv in _bankByType) if (kv.Value.Name == "ClipControllerAsset") { clipType = kv.Key; break; }
            int namePos = FieldPos("ClipControllerAsset", "__name");
            long ctrl = -1;
            foreach (long body in ScanType(clipType))
                if (string.Equals(ReadStr(body + U32(body + 28) + namePos), clipName, StringComparison.OrdinalIgnoreCase)) { ctrl = body; break; }
            if (ctrl < 0) { error = "clip not found: " + clipName; return false; }
            long fb; string codec = FindCodec(ctrl, out fb);
            if (codec != "FrameAnimationAsset") { error = clipName + " codec is " + codec + ", not FrameAnimationAsset"; return false; }
            long fi = fb + U32(fb + 28);
            quats = (int)U32(fi + FieldPos(codec, "QuatCount")); vec3s = (int)U32(fi + FieldPos(codec, "Vec3Count"));
            ReadArr(fi + FieldPos(codec, "Data"), out count, out dataPos);
            long baseRef = Resolve(fi + FieldPos(codec, "__base"));
            long ai = baseRef + U32(baseRef + 28);
            c2dKey = I64(ai + FieldPos("AnimationAsset", "ChannelToDofAsset"));
            return true;
        }

        // Reads one bone's channel at key 0 of a FrameAnimationAsset pose (by exact clip name): 4 floats for a
        // quaternion (x,y,z,w), 3 for a translation. Returns null when the clip or channel is missing.
        public float[] ReadFrameChannel(string clipName, string bone, bool rotation)
        {
            long clipType = -1; foreach (var kv in _bankByType) if (kv.Value.Name == "ClipControllerAsset") { clipType = kv.Key; break; }
            int namePos = FieldPos("ClipControllerAsset", "__name");
            long ctrl = -1;
            foreach (long body in ScanType(clipType))
                if (string.Equals(ReadStr(body + U32(body + 28) + namePos), clipName, StringComparison.OrdinalIgnoreCase)) { ctrl = body; break; }
            if (ctrl < 0) return null;
            long fb; string codec = FindCodec(ctrl, out fb);
            if (codec != "FrameAnimationAsset") return null;
            long fi = fb + U32(fb + 28);
            int qc = (int)U32(fi + FieldPos(codec, "QuatCount")), vc = (int)U32(fi + FieldPos(codec, "Vec3Count"));
            long dCount, dPos; ReadArr(fi + FieldPos(codec, "Data"), out dCount, out dPos);
            long baseRef = Resolve(fb + U32(fb + 28) + FieldPos(codec, "__base"));
            long ai = baseRef + U32(baseRef + 28);
            long c2dBody;
            if (!_bodyByKey.TryGetValue(I64(ai + FieldPos("AnimationAsset", "ChannelToDofAsset")), out c2dBody)) return null;
            long idCount, idPos; ReadArr(c2dBody + U32(c2dBody + 28) + FieldPos("ChannelToDofAsset", "DofIds"), out idCount, out idPos);
            int from = rotation ? 0 : qc, to = rotation ? qc : qc + vc;
            for (int c = from; c < to && c < idCount; c++)
            {
                _r.Position = idPos + c * 4;
                DofTarget t;
                if (_rigDof.TryGetValue(_r.ReadUInt(Endian.Little), out t) && t.Rotation == rotation && string.Equals(t.Bone, bone, StringComparison.OrdinalIgnoreCase))
                    return ReadFloats(dPos + (long)c * 16, rotation ? 4 : 3);
            }
            return null;
        }

        // Finds a ClipControllerAsset by exact __name and returns its Anim's RawAnimationAsset body (or -1).
        private long FindClipRawByName(string clipName)
        {
            long clipType = -1; foreach (var kv in _bankByType) if (kv.Value.Name == "ClipControllerAsset") { clipType = kv.Key; break; }
            int namePos = FieldPos("ClipControllerAsset", "__name");
            foreach (long body in ScanType(clipType))
            {
                uint dof = U32(body + 28);
                if (!string.Equals(ReadStr(body + dof + namePos), clipName, StringComparison.OrdinalIgnoreCase)) continue;
                long animKey = I64(body + dof + FieldPos("ClipControllerAsset", "Anim")); long animBody;
                if (_bodyByKey.TryGetValue(animKey, out animBody) && _bankByType.TryGetValue(U32(animBody + 16), out var ab) && ab.Name == "RawAnimationAsset") return animBody;
            }
            return -1;
        }

        private float[] ReadFloats(long pos, int n) { _r.Position = pos; var a = new float[n]; for (int i = 0; i < n; i++) a[i] = _r.ReadFloat(); return a; }
        private ushort[] ReadU16Array(long fieldPos)
        {
            long c, d; ReadArr(fieldPos, out c, out d);
            if (c <= 0 || c > 50000000 || d <= 0 || d + c * 2 > _streamLen) return new ushort[0];
            var a = new ushort[c]; _r.Position = d; for (long i = 0; i < c; i++) a[i] = _r.ReadUShort(Endian.Little); return a;
        }
        private byte[] ReadU8Array(long fieldPos)
        {
            long c, d; ReadArr(fieldPos, out c, out d);
            if (c <= 0 || c > 50000000 || d <= 0 || d + c > _streamLen) return new byte[0];
            _r.Position = d; return _r.ReadBytes((int)c);
        }

        // One animated/constant channel's writable byte layout within the bank (for in-place edit).
        public sealed class ClipChannel
        {
            public string Bone;       // skeleton bone name
            public bool Rotation;     // true = quaternion (.q), false = translation vec3 (.t)
            public bool Animated;     // true = per-key in Data; false = single value in ConstData
            public long ByteOffset;   // file offset of frame 0 (or the constant value)
            public int Stride;        // bytes between consecutive frames (0 for constant)
            public int Components;    // floats per value: 4 (quat) or 3 (vec3)
        }

        public sealed class ClipLayout
        {
            public bool Found;
            public int NumKeys;
            public int[] Frames;
            public int FrameCount;
            public bool Additive;
            public readonly List<ClipChannel> Channels = new List<ClipChannel>();
        }

        // Returns the byte layout of a Raw clip so an encoder can overwrite keyframe values in place
        // (same shape). The byte offset of stored channel c is fixed by its storage region; its dof
        // (hence bone) comes through the MappingIndices permutation, just like DecodeRaw. Returns
        // Found=false for non-Raw clips.
        public bool TryGetRawClipLayout(Guid controllerGuid, out ClipLayout layout)
        {
            layout = new ClipLayout();
            long key = BitConverter.ToInt64(controllerGuid.ToByteArray(), 0);
            long ctrlBody;
            if (!_bodyByKey.TryGetValue(key, out ctrlBody)) return false;
            long rawBody;
            if (FindCodec(ctrlBody, out rawBody) != "RawAnimationAsset") return false;

            uint rawDof = U32(rawBody + 28); long ri = rawBody + rawDof;
            int quatCount = U16(ri + FieldPos("RawAnimationAsset", "QuatCount"));
            int vec3Count = U16(ri + FieldPos("RawAnimationAsset", "Vec3Count"));
            int floatCount = U16(ri + FieldPos("RawAnimationAsset", "FloatCount"));
            long ktCount, ktPos; ReadArr(ri + FieldPos("RawAnimationAsset", "KeyTimes"), out ktCount, out ktPos);
            long dCount, dPos; ReadArr(ri + FieldPos("RawAnimationAsset", "Data"), out dCount, out dPos);
            long constCount, constPos; ReadArr(ri + FieldPos("RawAnimationAsset", "ConstData"), out constCount, out constPos);
            if (ktCount <= 0) return false;

            int numKeys = (int)ktCount;
            layout.NumKeys = numKeys;
            layout.Frames = new int[numKeys];
            _r.Position = ktPos;
            for (int i = 0; i < numKeys; i++) layout.Frames[i] = _r.ReadShort(Endian.Little);
            int perKey = quatCount * 4 + vec3Count * 4 + floatCount;
            int frameCount = numKeys > 0 ? layout.Frames[numKeys - 1] + 1 : numKeys;

            uint[] dofIds = null;
            long baseRef = Resolve(rawBody + rawDof + FieldPos("RawAnimationAsset", "__base"));
            if (baseRef > 0)
            {
                uint aDof = U32(baseRef + 28); long ai = baseRef + aDof;
                layout.Additive = (U32(ai + FieldPos("AnimationAsset", "Additive")) & 0xFF) != 0;
                int endFrame = U16(ai + FieldPos("AnimationAsset", "EndFrame"));
                if (endFrame + 1 > frameCount) frameCount = endFrame + 1;
                long c2dKey = I64(ai + FieldPos("AnimationAsset", "ChannelToDofAsset"));
                long c2dBody;
                if (_bodyByKey.TryGetValue(c2dKey, out c2dBody))
                {
                    uint cDof = U32(c2dBody + 28);
                    long idCount, idPos; ReadArr(c2dBody + cDof + FieldPos("ChannelToDofAsset", "DofIds"), out idCount, out idPos);
                    dofIds = new uint[idCount]; _r.Position = idPos;
                    for (int i = 0; i < idCount; i++) dofIds[i] = _r.ReadUInt(Endian.Little);
                }
            }
            if (dofIds == null) return false;
            layout.FrameCount = frameCount;
            int[] map = ReadShortArrayAsInt(ri + FieldPos("RawAnimationAsset", "MappingIndices"));

            int animatedCount = quatCount + vec3Count + floatCount;
            for (int c = 0; c < animatedCount && c < dofIds.Length; c++)
            {
                DofTarget tgt;
                if (!_rigDof.TryGetValue(DofForChannel(dofIds, map, c), out tgt)) continue;
                if (c < quatCount)
                    layout.Channels.Add(new ClipChannel { Bone = tgt.Bone, Rotation = true, Animated = true,
                        ByteOffset = dPos + (long)(c * 4) * 4, Stride = perKey * 4, Components = 4 });
                else if (c < quatCount + vec3Count)
                    layout.Channels.Add(new ClipChannel { Bone = tgt.Bone, Rotation = false, Animated = true,
                        ByteOffset = dPos + (long)(quatCount * 4 + (c - quatCount) * 4) * 4, Stride = perKey * 4, Components = 3 });
            }
            int constChannels = (int)(constCount / 4);
            for (int ci = 0; ci < constChannels; ci++)
            {
                int c = animatedCount + ci;
                if (c >= dofIds.Length) break;
                DofTarget tgt;
                if (!_rigDof.TryGetValue(DofForChannel(dofIds, map, c), out tgt)) continue;
                layout.Channels.Add(new ClipChannel { Bone = tgt.Bone, Rotation = tgt.Rotation, Animated = false,
                    ByteOffset = constPos + (long)ci * 16, Stride = 0, Components = tgt.Rotation ? 4 : 3 });
            }
            layout.Found = true;
            return true;
        }

        // ---- encode (arbitrary clip import) ---------------------------------

        public sealed class EncodeResult { public byte[] Bank; public int Channels; public int Frames; public int Unmapped; }

        // Re-encodes `anim` into the target Raw clip and returns a NEW bank byte[]: the new
        // KeyTimes/Data/DofIds/index arrays are appended at the end and the clip's instance fields
        // are repointed to them (tagged pointers are relative, so other clips are untouched).
        // Dense keys (0..FrameCount-1); rotation bones -> quat channels, translation bones -> vec3.
        // Bones with no rig DofId are skipped (counted in Unmapped). Throws if the clip isn't Raw.
        public EncodeResult EncodeClip(Guid controllerGuid, SeAnim anim, byte[] originalBank)
        {
            long key = BitConverter.ToInt64(controllerGuid.ToByteArray(), 0);
            long ctrlBody;
            if (!_bodyByKey.TryGetValue(key, out ctrlBody)) throw new InvalidOperationException("clip controller not found");
            long rawBody;
            if (FindCodec(ctrlBody, out rawBody) != "RawAnimationAsset") throw new InvalidOperationException("target clip is not RawAnimationAsset");

            uint rawDof = U32(rawBody + 28); long ri = rawBody + rawDof;
            long baseRef = Resolve(rawBody + rawDof + FieldPos("RawAnimationAsset", "__base"));
            uint aDof = U32(baseRef + 28); long ai = baseRef + aDof;
            long c2dKey = I64(ai + FieldPos("AnimationAsset", "ChannelToDofAsset"));
            long c2dBody = _bodyByKey[c2dKey]; uint cDof = U32(c2dBody + 28); long c2di = c2dBody + cDof;

            // Build channel lists: rotations first (quats), then translations (vec3).
            var rot = new List<SeAnimBone>(); var rotIds = new List<uint>();
            var pos = new List<SeAnimBone>(); var posIds = new List<uint>();
            int unmapped = 0;
            foreach (var b in anim.Bones)
            {
                if (b.RotKeys.Count > 0) { uint id; if (TryDof(b.Name, true, out id)) { rot.Add(b); rotIds.Add(id); } else unmapped++; }
                if (b.PosKeys.Count > 0) { uint id; if (TryDof(b.Name, false, out id)) { pos.Add(b); posIds.Add(id); } else unmapped++; }
            }
            int quatCount = rot.Count, vec3Count = pos.Count, channelCount = quatCount + vec3Count;
            int numKeys = Math.Max(1, anim.FrameCount);
            int perKey = quatCount * 4 + vec3Count * 4;

            // Build the data blobs.
            byte[] keyTimes = new byte[numKeys * 2];
            for (int k = 0; k < numKeys; k++) PutI16(keyTimes, k * 2, (short)k);
            byte[] data = new byte[(long)numKeys * perKey * 4 > int.MaxValue ? 0 : numKeys * perKey * 4];
            for (int k = 0; k < numKeys; k++)
            {
                int baseF = k * perKey;
                for (int qi = 0; qi < quatCount; qi++)
                {
                    SeQuat q = SampleRot(rot[qi], k);
                    int o = (baseF + qi * 4) * 4;
                    PutF(data, o, q.X); PutF(data, o + 4, q.Y); PutF(data, o + 8, q.Z); PutF(data, o + 12, q.W);
                }
                for (int vi = 0; vi < vec3Count; vi++)
                {
                    SeVec3 v = SamplePos(pos[vi], k);
                    int o = (baseF + quatCount * 4 + vi * 4) * 4;
                    PutF(data, o, v.X); PutF(data, o + 4, v.Y); PutF(data, o + 8, v.Z); PutF(data, o + 12, 0f);
                }
            }
            byte[] dofIds = new byte[channelCount * 4];
            for (int i = 0; i < quatCount; i++) PutU32(dofIds, i * 4, rotIds[i]);
            for (int i = 0; i < vec3Count; i++) PutU32(dofIds, (quatCount + i) * 4, posIds[i]);
            byte[] mapIdx = new byte[channelCount * 2];
            for (int i = 0; i < channelCount; i++) PutI16(mapIdx, i * 2, (short)i);
            byte[] chanIdx = (byte[])mapIdx.Clone();

            // Assemble new bank: original + appended (16-aligned) blobs.
            var app = new List<byte>();
            long Append(byte[] blob)
            {
                while ((originalBank.Length + app.Count) % 16 != 0) app.Add(0);
                long p = originalBank.Length + app.Count; app.AddRange(blob); return p;
            }
            long ktPos = Append(keyTimes);
            long dataPos = Append(data);
            long dofPos = Append(dofIds);
            long mapPos = Append(mapIdx);
            long chPos = Append(chanIdx);

            byte[] outBank = new byte[originalBank.Length + app.Count];
            Buffer.BlockCopy(originalBank, 0, outBank, 0, originalBank.Length);
            app.CopyTo(outBank, originalBank.Length);

            // Repoint the clip's instance fields.
            PutU16(outBank, ri + FieldPos("RawAnimationAsset", "QuatCount"), (ushort)quatCount);
            PutU16(outBank, ri + FieldPos("RawAnimationAsset", "Vec3Count"), (ushort)vec3Count);
            PutU16(outBank, ri + FieldPos("RawAnimationAsset", "FloatCount"), 0);
            PutU16(outBank, ri + FieldPos("RawAnimationAsset", "ConstFloatCount"), 0);
            WriteArrayField(outBank, ri + FieldPos("RawAnimationAsset", "KeyTimes"), ktPos, numKeys);
            WriteArrayField(outBank, ri + FieldPos("RawAnimationAsset", "Data"), dataPos, numKeys * perKey);
            WriteArrayField(outBank, ri + FieldPos("RawAnimationAsset", "ConstData"), 0, 0);
            WriteArrayField(outBank, ri + FieldPos("RawAnimationAsset", "MappingIndices"), mapPos, channelCount);
            WriteArrayField(outBank, ri + FieldPos("RawAnimationAsset", "ChannelIndices"), chPos, channelCount);
            WriteArrayField(outBank, c2di + FieldPos("ChannelToDofAsset", "DofIds"), dofPos, channelCount);
            PutU16(outBank, ai + FieldPos("AnimationAsset", "EndFrame"), (ushort)(numKeys - 1));
            outBank[ai + FieldPos("AnimationAsset", "Additive")] = (byte)(anim.AnimType != SeAnimType.Absolute ? 1 : 0);

            return new EncodeResult { Bank = outBank, Channels = channelCount, Frames = numKeys, Unmapped = unmapped };
        }

        private static SeQuat SampleRot(SeAnimBone b, int frame)
        {
            var ks = b.RotKeys; if (ks.Count == 0) return SeQuat.Identity;
            for (int i = 0; i < ks.Count; i++) if (ks[i].Frame == frame) return ks[i].Rotation;
            if (frame <= ks[0].Frame) return ks[0].Rotation;
            if (frame >= ks[ks.Count - 1].Frame) return ks[ks.Count - 1].Rotation;
            for (int i = 0; i < ks.Count - 1; i++)
                if (frame > ks[i].Frame && frame < ks[i + 1].Frame)
                {
                    float t = (frame - ks[i].Frame) / (float)(ks[i + 1].Frame - ks[i].Frame);
                    return Nlerp(ks[i].Rotation, ks[i + 1].Rotation, t);
                }
            return ks[ks.Count - 1].Rotation;
        }

        private static SeVec3 SamplePos(SeAnimBone b, int frame)
        {
            var ks = b.PosKeys; if (ks.Count == 0) return new SeVec3(0, 0, 0);
            for (int i = 0; i < ks.Count; i++) if (ks[i].Frame == frame) return ks[i].Position;
            if (frame <= ks[0].Frame) return ks[0].Position;
            if (frame >= ks[ks.Count - 1].Frame) return ks[ks.Count - 1].Position;
            for (int i = 0; i < ks.Count - 1; i++)
                if (frame > ks[i].Frame && frame < ks[i + 1].Frame)
                {
                    float t = (frame - ks[i].Frame) / (float)(ks[i + 1].Frame - ks[i].Frame);
                    var a = ks[i].Position; var c = ks[i + 1].Position;
                    return new SeVec3(a.X + (c.X - a.X) * t, a.Y + (c.Y - a.Y) * t, a.Z + (c.Z - a.Z) * t);
                }
            return ks[ks.Count - 1].Position;
        }

        private static SeQuat Nlerp(SeQuat a, SeQuat b, float t)
        {
            float d = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W; float s = d < 0 ? -1 : 1;
            float x = a.X + (b.X * s - a.X) * t, y = a.Y + (b.Y * s - a.Y) * t, z = a.Z + (b.Z * s - a.Z) * t, w = a.W + (b.W * s - a.W) * t;
            float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w); if (len < 1e-8f) return SeQuat.Identity;
            return new SeQuat(x / len, y / len, z / len, w / len);
        }

        private static void WriteArrayField(byte[] buf, long fieldPos, long dataFilePos, int count)
        {
            PutU32(buf, fieldPos, (uint)count);
            PutU32(buf, fieldPos + 4, (uint)count);
            long ptr = dataFilePos == 0 ? 0L : ((1L << 60) | (dataFilePos - (fieldPos + 8)));
            PutI64(buf, fieldPos + 8, ptr);
        }

        private static void PutU16(byte[] b, long o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        private static void PutI16(byte[] b, long o, short v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        private static void PutU32(byte[] b, long o, uint v) { for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }
        private static void PutI64(byte[] b, long o, long v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i)); }
        private static void PutF(byte[] b, long o, float v) { byte[] t = BitConverter.GetBytes(v); b[o] = t[0]; b[o + 1] = t[1]; b[o + 2] = t[2]; b[o + 3] = t[3]; }

        // ---- rig dof map -----------------------------------------------------

        private void BuildRigDofMap(List<long> rigBodies, string rigName)
        {
            // DECODE map: union of EVERY rig's DofId->bone. DofIds are globally unique across the
            // bank's rigs (verified collision-free), so one map resolves any hero's channels to the
            // correct bone names; the chosen export skeleton then decides which bones actually fill.
            // This lets export-anim --skeleton <hero rig> pose Yoda/Grievous on their own skeletons.
            foreach (long rb in rigBodies) BuildDofMapForRig(rb, _rigDof);

            // ENCODE inverse ("<bone>|q"/"|t" -> DofId): built from the named humanoid rig ONLY, so
            // arbitrary-clip import targets that rig's dof ids (a union inverse could pick another
            // rig's id for a shared bone name like "Hips").
            long named = -1;
            int rigNamePos = FieldPos("RigAsset", "__name");
            foreach (long rb in rigBodies)
            {
                uint rd = U32(rb + 28);
                if (ReadStr(rb + rd + rigNamePos) == rigName) { named = rb; break; }
            }
            if (named >= 0)
            {
                var namedMap = new Dictionary<uint, DofTarget>();
                BuildDofMapForRig(named, namedMap);
                foreach (var kv in namedMap)
                {
                    string ik = kv.Value.Bone.ToLowerInvariant() + (kv.Value.Rotation ? "|q" : "|t");
                    if (!_dofByBone.ContainsKey(ik)) _dofByBone[ik] = kv.Key;
                }
            }
        }

        // Reads one RigAsset's DofId->(bone,component) map into dst (first-wins on duplicate DofId).
        // The rig defines RigDofSets (LayoutAssets of ordered named slots "<bone>.q"/".t"/scalars) and
        // a master DofIds array; slot j of dof set s is assigned DofIds[DofSetIdIndices[s] + j].
        private void BuildDofMapForRig(long rig, Dictionary<uint, DofTarget> dst)
        {
            uint rdof = U32(rig + 28); long ri = rig + rdof;
            uint[] dofIds = ReadU32Array(ri + FieldPos("RigAsset", "DofIds"));
            uint[] setStart = ReadU32Array(ri + FieldPos("RigAsset", "DofSetIdIndices"));
            int slotsPos = FieldPos("LayoutAsset", "Slots");
            int leName = FieldPos("LayoutEntry", "Name");
            long rdsC, rdsP; ReadArr(ri + FieldPos("RigAsset", "RigDofSets"), out rdsC, out rdsP);
            for (long s = 0; s < rdsC; s++)
            {
                long lk = I64(rdsP + s * 8); long lb;
                if (!_bodyByKey.TryGetValue(lk, out lb)) continue;
                uint ld = U32(lb + 28); long lai = lb + ld;
                long sc, sp; ReadArr(lai + slotsPos, out sc, out sp);
                long baseIdx = s < setStart.Length ? setStart[s] : 0;
                for (long j = 0; j < sc; j++)
                {
                    long di = baseIdx + j;
                    if (di < 0 || di >= dofIds.Length) break;
                    uint dofId = dofIds[di];
                    string nm = ReadStr(sp + j * 24 + leName);
                    if (nm == null) continue;
                    if (nm.EndsWith(".q", StringComparison.Ordinal))
                    { if (!dst.ContainsKey(dofId)) dst[dofId] = new DofTarget { Bone = nm.Substring(0, nm.Length - 2), Rotation = true }; }
                    else if (nm.EndsWith(".t", StringComparison.Ordinal))
                    { if (!dst.ContainsKey(dofId)) dst[dofId] = new DofTarget { Bone = nm.Substring(0, nm.Length - 2), Rotation = false }; }
                    // scalar/facial dofs (no .q/.t suffix) are not skeletal - skipped.
                }
            }
        }

        private uint[] ReadU32Array(long fieldPos)
        {
            long count, dp; ReadArr(fieldPos, out count, out dp);
            if (count <= 0 || count > 10000000 || dp <= 0 || dp + count * 4 > _streamLen) return new uint[0];
            uint[] a = new uint[count];
            for (long i = 0; i < count; i++) a[i] = U32(dp + i * 4);
            return a;
        }

        private readonly Dictionary<string, uint> _dofByBone = new Dictionary<string, uint>();
        private bool TryDof(string bone, bool rotation, out uint dofId)
        {
            return _dofByBone.TryGetValue(bone.ToLowerInvariant() + (rotation ? "|q" : "|t"), out dofId);
        }

        // ---- raw clip decode -------------------------------------------------

        private bool DecodeRaw(long rawBody, SeAnim anim)
        {
            uint rawDof = U32(rawBody + 28); long ri = rawBody + rawDof;

            int quatCount = U16(ri + FieldPos("RawAnimationAsset", "QuatCount"));
            int vec3Count = U16(ri + FieldPos("RawAnimationAsset", "Vec3Count"));
            int floatCount = U16(ri + FieldPos("RawAnimationAsset", "FloatCount"));

            long ktCount, ktPos; ReadArr(ri + FieldPos("RawAnimationAsset", "KeyTimes"), out ktCount, out ktPos);
            long dCount, dPos; ReadArr(ri + FieldPos("RawAnimationAsset", "Data"), out dCount, out dPos);
            long constCount, constPos; ReadArr(ri + FieldPos("RawAnimationAsset", "ConstData"), out constCount, out constPos);

            if (ktCount <= 0) return false;
            int numKeys = (int)ktCount;
            int[] frames = new int[numKeys];
            _r.Position = ktPos;
            for (int i = 0; i < numKeys; i++) frames[i] = _r.ReadShort(Endian.Little);

            // Per-key stride in floats. QuatCount*4 + Vec3Count*4 + FloatCount undercounts some clips
            // (extra trailing per-key floats: scale or padding), which drifts the read offset by
            // delta*key and corrupts later frames (coherent then exploding). The Data array length is
            // the ground truth: use dCount/numKeys when it divides evenly, else fall back to the sum.
            int perKeySum = quatCount * 4 + vec3Count * 4 + floatCount;
            int perKey = (numKeys > 0 && dCount > 0 && dCount % numKeys == 0 && (dCount / numKeys) >= perKeySum)
                ? (int)(dCount / numKeys) : perKeySum;

            // AnimationAsset base: Additive + frame count.
            long baseRef = Resolve(rawBody + rawDof + FieldPos("RawAnimationAsset", "__base"));
            int frameCount = (numKeys > 0 ? frames[numKeys - 1] + 1 : numKeys);
            uint[] dofIds = null;
            if (baseRef > 0)
            {
                uint aDof = U32(baseRef + 28); long ai = baseRef + aDof;
                bool additive = (U32(ai + FieldPos("AnimationAsset", "Additive")) & 0xFF) != 0;
                anim.AnimType = additive ? SeAnimType.Additive : SeAnimType.Absolute;
                int endFrame = U16(ai + FieldPos("AnimationAsset", "EndFrame"));
                if (endFrame + 1 > frameCount) frameCount = endFrame + 1;
                long c2dKey = I64(ai + FieldPos("AnimationAsset", "ChannelToDofAsset"));
                long c2dBody;
                if (_bodyByKey.TryGetValue(c2dKey, out c2dBody))
                {
                    uint cDof = U32(c2dBody + 28);
                    long idCount, idPos; ReadArr(c2dBody + cDof + FieldPos("ChannelToDofAsset", "DofIds"), out idCount, out idPos);
                    dofIds = new uint[idCount];
                    _r.Position = idPos;
                    for (int i = 0; i < idCount; i++) dofIds[i] = _r.ReadUInt(Endian.Little);
                }
            }
            if (dofIds == null) return false;
            anim.FrameCount = frameCount;

            // Channels are stored (in Data, then ConstData) in a different order than DofIds. The
            // MappingIndices array reconciles them: stored channel s carries dof DofIds[MappingIndices[s]].
            int[] map = ReadShortArrayAsInt(ri + FieldPos("RawAnimationAsset", "MappingIndices"));

            // Resolve each SEAnim bone track by name once.
            var trackByName = new Dictionary<string, SeAnimBone>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < anim.Bones.Count; i++) trackByName[anim.Bones[i].Name] = anim.Bones[i];

            // Skeleton-fit gate. The union dof map resolves a channel to a bone name from ANY hero's
            // rig; a clip only belongs on a skeleton if the bones it drives are mostly THAT skeleton's.
            // fit = (channels whose resolved bone is in this skeleton) / (channels that resolve at all).
            // Humanoid strike on Walrus ~1.0; Grievous clip on the humanoid skeleton ~0.6 (only shared
            // bones); Grievous clip on Grievous's skeleton ~1.0. Bail on poor fit so the caller skips
            // the clip (export-anim-batch) rather than posing it on the wrong skeleton. Only applies to
            // a full-skeleton decode (the export path); internal callers that decode into a handful of
            // named bones (in-place edit) are exempt, since fit is meaningless for a partial track set.
            if (dofIds.Length > 0 && anim.Bones.Count >= 16)
            {
                int resolved = 0, inSkel = 0;
                for (int i = 0; i < dofIds.Length; i++)
                {
                    DofTarget t;
                    if (_rigDof.TryGetValue(dofIds[i], out t)) { resolved++; if (trackByName.ContainsKey(t.Bone)) inSkel++; }
                }
                if (resolved == 0 || inSkel < resolved * 0.80) return false;
            }

            int animatedCount = quatCount + vec3Count + floatCount;
            bool any = false;

            // Animated channels: stored channel c in [0,animatedCount). Its dof = DofIds[map[c]];
            // its component (quat/vec3/float) is given by the storage region (quat | vec3 | float).
            for (int c = 0; c < animatedCount && c < dofIds.Length; c++)
            {
                uint dofId = DofForChannel(dofIds, map, c);
                DofTarget tgt;
                if (!_rigDof.TryGetValue(dofId, out tgt)) continue;
                SeAnimBone bone;
                if (!trackByName.TryGetValue(tgt.Bone, out bone)) continue;

                if (c < quatCount)
                {
                    // One channel per bone.component: the aggregate rig map can alias two dofIds to the
                    // same bone (it appears in multiple dof sets); take the first and skip the rest so
                    // a bone never accumulates more keys than frames.
                    if (bone.RotKeys.Count > 0) continue;
                    int col = c * 4;
                    for (int k = 0; k < numKeys; k++)
                    {
                        long off = dPos + ((long)k * perKey + col) * 4;
                        _r.Position = off;
                        float x = _r.ReadFloat(), y = _r.ReadFloat(), z = _r.ReadFloat(), w = _r.ReadFloat();
                        bone.RotKeys.Add(new SeRotKey { Frame = frames[k], Rotation = new SeQuat(x, y, z, w) });
                    }
                    any = true;
                }
                else if (c < quatCount + vec3Count)
                {
                    if (bone.PosKeys.Count > 0) continue;
                    int col = quatCount * 4 + (c - quatCount) * 4;
                    for (int k = 0; k < numKeys; k++)
                    {
                        long off = dPos + ((long)k * perKey + col) * 4;
                        _r.Position = off;
                        float x = _r.ReadFloat(), y = _r.ReadFloat(), z = _r.ReadFloat();
                        bone.PosKeys.Add(new SePosKey { Frame = frames[k], Position = new SeVec3(x, y, z) });
                    }
                    any = true;
                }
                // float channels (c >= quatCount+vec3Count): non-skeletal, skipped for SEAnim.
            }

            // Constant channels: stored channel (animatedCount+ci); dof = DofIds[map[animatedCount+ci]].
            // The component is taken from the dof's rig mapping (const quats and vec3 are interleaved).
            int constChannels = (int)(constCount / 4);
            for (int ci = 0; ci < constChannels; ci++)
            {
                int c = animatedCount + ci;
                if (c >= dofIds.Length) break;
                uint dofId = DofForChannel(dofIds, map, c);
                DofTarget tgt;
                if (!_rigDof.TryGetValue(dofId, out tgt)) continue;
                SeAnimBone bone;
                if (!trackByName.TryGetValue(tgt.Bone, out bone)) continue;

                // Skip if an animated (or earlier constant) channel already filled this component.
                if (tgt.Rotation ? bone.RotKeys.Count > 0 : bone.PosKeys.Count > 0) continue;
                long off = constPos + (long)ci * 4 * 4;
                _r.Position = off;
                float a = _r.ReadFloat(), b = _r.ReadFloat(), cc2 = _r.ReadFloat(), d = _r.ReadFloat();
                if (tgt.Rotation)
                    bone.RotKeys.Add(new SeRotKey { Frame = 0, Rotation = new SeQuat(a, b, cc2, d) });
                else
                    bone.PosKeys.Add(new SePosKey { Frame = 0, Position = new SeVec3(a, b, cc2) });
            }

            return any;
        }

        // Stored channel c carries dof DofIds[MappingIndices[c]]. Falls back to identity when the
        // MappingIndices array is absent/short (defensive).
        private static uint DofForChannel(uint[] dofIds, int[] map, int c)
        {
            int idx = (map != null && c < map.Length) ? map[c] : c;
            if (idx < 0 || idx >= dofIds.Length) idx = c;
            return (idx >= 0 && idx < dofIds.Length) ? dofIds[idx] : 0u;
        }

        private int[] ReadShortArrayAsInt(long fieldPos)
        {
            long count, dp; ReadArr(fieldPos, out count, out dp);
            if (count <= 0 || count > 10000000 || dp <= 0 || dp + count * 2 > _streamLen) return new int[0];
            int[] a = new int[count]; _r.Position = dp;
            for (long i = 0; i < count; i++) a[i] = _r.ReadShort(Endian.Little);
            return a;
        }

        // ---- traversal -------------------------------------------------------

        // Follows the controller graph (Key + Reference fields, arrays) until a codec asset.
        // Uses breadth-first traversal in document order so the FIRST codec returned is the primary
        // animation: a strike's Player.Tracks[0].Anims[] lists the actual attack (A_<Hero>_AttackLoop_
        // StrikeN) at Anims[0], followed by transition clips (..._BackToIdle, ..._To_IdleHelper) that
        // are often shared across variants. A LIFO walk picked the last (a transition/shared) clip,
        // which collapsed Vader/Kylo/Dooku variants onto one wrong clip; BFS picks Anims[0].
        // Name of the ClipControllerAsset whose Anim the last FindCodec resolved to (the source clip,
        // e.g. "A_Vader_AttackLoop_Strike1"). Surfaced so export-anim can log which clip it picked.
        public string LastClipName { get; private set; }

        private string FindCodec(long startBody, out long codecBody)
        {
            codecBody = -1;
            LastClipName = null;
            var visited = new HashSet<long>();
            var queue = new Queue<long>(); queue.Enqueue(startBody);
            var nameByBody = new Dictionary<long, string>();  // codec body -> its parent ClipController name
            int clipNamePos = FieldPos("ClipControllerAsset", "__name");
            int guard = 0;
            while (queue.Count > 0 && guard++ < 8000)
            {
                long body = queue.Dequeue();
                if (!visited.Add(body)) continue;
                if (body < 0 || body + 32 > _streamLen) continue;
                long tv = U32(body + 16);
                uint dof = U32(body + 28);
                AntBank bank;
                if (!_bankByType.TryGetValue(tv, out bank)) continue;
                if (CodecNames.Contains(bank.Name))
                {
                    codecBody = body;
                    nameByBody.TryGetValue(body, out string cn); LastClipName = cn;
                    return bank.Name;
                }
                // If this is a clip controller, tag its children with its name so we can report which
                // clip the resolved codec came from.
                string clipName = (bank.Name == "ClipControllerAsset" && clipNamePos >= 0) ? ReadStr(body + dof + clipNamePos) : null;
                foreach (var f in bank.Fields)
                {
                    bool isKey = (int)f.TypeHash == 35;
                    bool isRef = f.TypeHash == BankType.Reference;
                    if (!isKey && !isRef) continue;
                    long fp = body + dof + f.Position;
                    if (!f.IsArray)
                    {
                        long b = isKey ? (I64(fp) != 0 && _bodyByKey.TryGetValue(I64(fp), out long bb) ? bb : -1) : Resolve(fp);
                        if (b > 0) { queue.Enqueue(b); if (clipName != null && !nameByBody.ContainsKey(b)) nameByBody[b] = clipName; }
                    }
                    else
                    {
                        long cnt, data; ReadArr(fp, out cnt, out data);
                        if (cnt <= 0 || cnt > 4096) continue;
                        for (long i = 0; i < cnt; i++)
                        {
                            long b = isKey ? (I64(data + i * 8) != 0 && _bodyByKey.TryGetValue(I64(data + i * 8), out long bb) ? bb : -1) : Resolve(data + i * 8);
                            if (b > 0) { queue.Enqueue(b); if (clipName != null && !nameByBody.ContainsKey(b)) nameByBody[b] = clipName; }
                        }
                    }
                }
            }
            return null;
        }

        // ---- index build -----------------------------------------------------

        private List<long> BuildIndex()
        {
            var rigBodies = new List<long>();
            long rigType = -1;
            foreach (var kv in _bankByType) if (kv.Value.Name == "RigAsset") { rigType = kv.Key; break; }

            _r.Position = _schema.DataSectionStart;
            while (_r.Position + 12 < _streamLen)
            {
                long hdr = _r.Position;
                string tag;
                try { tag = _r.ReadSizedString(7); } catch { break; }
                if (tag.Length < 3 || tag[0] != 'G' || tag[1] != 'D') break;
                Endian en = _r.ReadSizedString(1) == "b" ? Endian.Big : Endian.Little;
                int size = _r.ReadInt(en);
                if (size < 12 || hdr + size > _streamLen) break;
                long body = _r.Position;
                if (tag == "GD.DAT2" || tag == "GD.DATA")
                {
                    long tv = U32(body + 16);
                    uint dof = U32(body + 28);
                    int kp;
                    if (_keyPosByType.TryGetValue(tv, out kp) && kp >= 0)
                    {
                        long k = I64(body + dof + kp);
                        if (!_bodyByKey.ContainsKey(k)) _bodyByKey[k] = body;
                    }
                    if (tv == rigType) rigBodies.Add(body);
                }
                _r.Position = hdr + size;
            }
            return rigBodies;
        }

        // ---- low-level readers ----------------------------------------------

        private int FieldPos(string bank, string field)
        {
            string cacheKey = bank + "." + field;
            int v;
            if (_fieldPos.TryGetValue(cacheKey, out v)) return v;
            v = -1;
            foreach (var kv in _schema.Banks)
            {
                if (!string.Equals(kv.Value.Name, bank, StringComparison.Ordinal)) continue;
                foreach (var f in kv.Value.Fields)
                    if (string.Equals(f.Name, field, StringComparison.Ordinal)) { v = f.Position; break; }
                break;
            }
            _fieldPos[cacheKey] = v;
            return v;
        }

        private uint U32(long pos) { _r.Position = pos; return _r.ReadUInt(Endian.Little); }
        private ushort U16(long pos) { _r.Position = pos; return _r.ReadUShort(Endian.Little); }
        private long I64(long pos) { _r.Position = pos; return _r.ReadLong(Endian.Little); }

        // All GD.DAT2/DATA instance bodies of a given bank type key.
        private List<long> ScanType(long typeKey)
        {
            var list = new List<long>();
            _r.Position = _schema.DataSectionStart;
            while (_r.Position + 12 < _streamLen)
            {
                long hdr = _r.Position; string tag; try { tag = _r.ReadSizedString(7); } catch { break; }
                if (tag.Length < 3 || tag[0] != 'G' || tag[1] != 'D') break;
                Endian en = _r.ReadSizedString(1) == "b" ? Endian.Big : Endian.Little;
                int size = _r.ReadInt(en); if (size < 12 || hdr + size > _streamLen) break;
                long body = _r.Position;
                if ((tag == "GD.DAT2" || tag == "GD.DATA") && U32(body + 16) == typeKey) list.Add(body);
                _r.Position = hdr + size;
            }
            return list;
        }

        private string ReadName(long body)
        {
            long tv = U32(body + 16); uint dof = U32(body + 28);
            if (!_bankByType.TryGetValue(tv, out var bank)) return "";
            foreach (var f in bank.Fields) if (f.Name == "__name") return ReadStr(body + dof + f.Position) ?? "";
            return "";
        }

        // Tagged pointer at ptrPos: tag (top 4 bits) 1 => ptrPos+low, 0 => low (absolute).
        private long Resolve(long ptrPos)
        {
            _r.Position = ptrPos;
            long raw = _r.ReadLong(Endian.Little);
            long low = raw & 0x0FFFFFFFFFFFFFFFL;
            int tag = (int)((ulong)raw >> 60);
            if (tag == 1) return ptrPos + low;
            if (tag == 0) return low;
            return -1;
        }

        // Array/String field {uint32 count, uint32 capacity, taggedPtr}: out element data position.
        private void ReadArr(long fieldPos, out long count, out long dataPos)
        {
            _r.Position = fieldPos;
            count = _r.ReadUInt(Endian.Little);
            dataPos = Resolve(fieldPos + 8);
        }

        private string ReadStr(long fieldPos)
        {
            _r.Position = fieldPos;
            long len = _r.ReadUInt(Endian.Little);
            if (len <= 0 || len > 1024) return null;
            long p = Resolve(fieldPos + 8);
            if (p <= 0) return null;
            _r.Position = p;
            byte[] bytes = _r.ReadBytes((int)len);
            int n = bytes.Length;
            while (n > 0 && bytes[n - 1] == 0) n--;
            return Encoding.ASCII.GetString(bytes, 0, n);
        }

        public void Dispose() { try { _stream.Dispose(); } catch { } }
    }
}
