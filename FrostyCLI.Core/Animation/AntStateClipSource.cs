using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FrostySdk.IO;

namespace FrostyCLI.Core.Animation
{
    // One hero-strike clip discovered in a soldier state machine.
    public sealed class StrikeClip
    {
        public string FullName;        // normalized clip name, e.g. "Vader_Strike_01" (the .seanim filename)
        public string ControllerName;  // raw controller name from the state machine
        public uint ClipOffset;         // CharacterStateClipControllerData._ClipOffset
        public uint ClipLength;         // CharacterStateClipControllerData._Length
        public Guid ControllerAssetGuid; // _Controller (AntRef).AssetGuid
    }

    // Enumerates hero-strike clips from a CharacterStateOwnerData state machine (e.g.
    // Gameplay/SoldierStateMachine/DefaultSoldierStateMachine), matching FAAE's StateMachineParser:
    // AllControllerDatas is walked in triplets (player / keyframed-transform / named-controller);
    // the named controller [i+2] carries the strike name, the player controller [i] carries the
    // clip data (offset/length/AntRef).
    public static class AntStateClipSource
    {
        public static List<StrikeClip> EnumerateStrikes(EbxAsset stateMachineAsset)
        {
            var result = new List<StrikeClip>();
            if (stateMachineAsset == null) return result;

            IList all = GetList(stateMachineAsset.RootObject, "AllControllerDatas");
            if (all == null) return result;

            for (int i = 0; i < all.Count - 2; i++)
            {
                object named = ResolveInternal(all[i + 2]);
                string ctrlName = TryGetString(named, "Name");
                if (string.IsNullOrEmpty(ctrlName)) ctrlName = TryGetString(named, "Id");
                if (string.IsNullOrEmpty(ctrlName)) continue;

                string fullName = StrikeClipName.BuildFullName(ctrlName);
                if (fullName == null) continue;

                // The clip-bearing CharacterStateClipControllerData is usually the player ref [i],
                // but for some (e.g. additive) strikes [i] is a TransitionController with a null
                // controller and the actual clip lives on the named [i+2] (or keyframed [i+1]) ref.
                // Pick the first triplet member whose Controller AntRef is non-zero.
                object player = ResolveInternal(all[i]);
                object keyframed = (i + 1 < all.Count) ? ResolveInternal(all[i + 1]) : null;
                object clipObj = FirstWithController(player, named, keyframed);
                var clip = new StrikeClip
                {
                    FullName = fullName,
                    ControllerName = ctrlName,
                    ClipOffset = TryGetUInt(clipObj, "ClipOffset"),
                    ClipLength = TryGetUInt(clipObj, "Length"),
                    ControllerAssetGuid = TryGetAntRefGuid(clipObj, "Controller"),
                };
                result.Add(clip);
                i += 2;
            }
            return result;
        }

        // Every CharacterStateClipControllerData in the state machine that carries a clip, keyed by its
        // own Name (not just hero strikes). Used by export-anim --any to pull locomotion/reaction clips.
        public static List<StrikeClip> EnumerateAllClips(EbxAsset stateMachineAsset)
        {
            var result = new List<StrikeClip>();
            if (stateMachineAsset == null) return result;
            foreach (object obj in stateMachineAsset.Objects)
            {
                if (obj == null || obj.GetType().Name != "CharacterStateClipControllerData") continue;
                Guid ctrl = TryGetAntRefGuid(obj, "Controller");
                if (ctrl == Guid.Empty) continue;
                string name = TryGetString(obj, "Name");
                if (string.IsNullOrEmpty(name)) continue;
                result.Add(new StrikeClip
                {
                    FullName = name,
                    ControllerName = name,
                    ClipOffset = TryGetUInt(obj, "ClipOffset"),
                    ClipLength = TryGetUInt(obj, "Length"),
                    ControllerAssetGuid = ctrl,
                });
            }
            return result;
        }

        // Returns the first candidate whose Controller AntRef is non-zero; defaults to the first.
        private static object FirstWithController(params object[] candidates)
        {
            foreach (object c in candidates)
                if (c != null && TryGetAntRefGuid(c, "Controller") != Guid.Empty) return c;
            return candidates[0];
        }

        private static IList GetList(object obj, string prop)
        {
            try { dynamic d = obj; if (prop == "AllControllerDatas") return d.AllControllerDatas as IList; }
            catch { }
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pi != null) return pi.GetValue(obj) as IList;
            }
            catch { }
            return null;
        }

        private static object ResolveInternal(object pointerRef)
        {
            if (pointerRef == null) return null;
            try { dynamic ptr = pointerRef; return ptr.Internal; }
            catch { return null; }
        }

        private static string TryGetString(object obj, string prop)
        {
            if (obj == null) return null;
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                return pi?.GetValue(obj)?.ToString();
            }
            catch { return null; }
        }

        private static uint TryGetUInt(object obj, string prop)
        {
            if (obj == null) return 0;
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                object v = pi?.GetValue(obj);
                return v == null ? 0u : Convert.ToUInt32(v);
            }
            catch { return 0; }
        }

        private static Guid TryGetAntRefGuid(object obj, string prop)
        {
            if (obj == null) return Guid.Empty;
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                object antRef = pi?.GetValue(obj);
                if (antRef == null) return Guid.Empty;
                PropertyInfo gi = antRef.GetType().GetProperty("AssetGuid", BindingFlags.Public | BindingFlags.Instance);
                object g = gi?.GetValue(antRef);
                if (g is Guid guid) return guid;
            }
            catch { }
            return Guid.Empty;
        }
    }
}
