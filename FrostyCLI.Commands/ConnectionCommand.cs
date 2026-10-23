using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostySdk.Ebx;
using System.Globalization;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // A blueprint/logic asset's connection graph -- the Property / Link / Event connection arrays the
    // Frosty GUI's Connection Editor shows. `ls` audits how a blueprint's objects are wired (spawn
    // managers, state machines, SubWorld logic); `add` and `remove` author that wiring.
    //
    // An endpoint is a PointerRef, and it comes in two forms. An INTERNAL ref names a sub-object of
    // the same partition by instance GUID -- which is why `ebx set-ref`, built for external file
    // references, cannot write one and this command resolves them itself. An EXTERNAL ref names an
    // object in ANOTHER asset by that asset's file GUID plus the object's exported instance GUID,
    // and that is what most real logic is made of: Kashyyyk's mode graph is 63 event connections
    // stored on Levels/MP/Kashyyyk_01/GameModes whose endpoints live in
    // Levels/MP/Kashyyyk_01/GameModes_Flow. `--source-ebx` / `--target-ebx` select that form.
    public class ConnectionCommand : ICommand
    {
        public string Name => "connection";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1) { Console.Error.WriteLine("Usage: frostycli connection <ls|add|remove> <blueprint> ..."); return 1; }
            switch (args[0].ToLowerInvariant())
            {
                case "ls":     return ExecuteLs(Sub(args), logger, formatter);
                case "add":    return ExecuteAdd(Sub(args), logger, formatter);
                case "remove": return ExecuteRemove(Sub(args), logger, formatter);
                case "names":  return ExecuteNames(Sub(args), logger, formatter);
                default:
                    Console.Error.WriteLine("connection: unknown subcommand '" + args[0] + "'. Use ls, add, remove or names.");
                    return 1;
            }
        }

        private static string[] Sub(string[] a) { var r = new string[a.Length - 1]; Array.Copy(a, 1, r, 0, r.Length); return r; }

        public static int ExecuteLs(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null; string kindFilter = null; string namesFile = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--kind" && i + 1 < args.Length) kindFilter = args[++i].ToLowerInvariant();
                else if (args[i] == "--names" && i + 1 < args.Length) namesFile = args[++i];
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }
            if (ebxPath == null) { Console.Error.WriteLine("connection ls: missing <blueprint>"); return 1; }
            // Port ids are one-way hashes, so a graph reads as hex without a vocabulary to reverse
            // them. Absent a wordlist this is a no-op and raw ids are printed as before.
            PortNames.Load(namesFile);
            if (App.AssetManager == null) { Console.Error.WriteLine("connection ls: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine("connection ls: asset not found: " + ebxPath); return 1; }
            FrostySdk.IO.EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("connection ls: load failed: " + ex.Message); return 1; }

            // Connection arrays can live on the root object OR on sub-objects in the graph (e.g. an
            // interface descriptor), so walk every object in the asset.
            int total = 0;
            foreach (object obj in asset.Objects)
            {
                if (obj == null) continue;
                foreach (PropertyInfo pi in obj.GetType().GetProperties())
                {
                    if (pi.Name.IndexOf("Connection", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string kind = pi.Name.ToLowerInvariant();
                    if (kindFilter != null && kind.IndexOf(kindFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    IList list;
                    try { list = pi.GetValue(obj) as IList; } catch { continue; }
                    if (list == null) continue;
                    foreach (object conn in list)
                    {
                        if (conn == null) continue;
                        string src = DescribeEndpoint(conn, "Source");
                        string tgt = DescribeEndpoint(conn, "Target");
                        formatter.WriteResult("OK", "connection", new Dictionary<string, string>
                        {
                            { "kind", pi.Name },
                            { "source", src },
                            { "target", tgt },
                        });
                        total++;
                    }
                }
            }
            if (total == 0) Console.Error.WriteLine("connection ls: no connections on " + ebxPath +
                (kindFilter != null ? " matching --kind " + kindFilter : ""));
            return 0;
        }

        // The field on each side is a hashed id, not a name. Verified against shipped data:
        // Utils.HashString("Value") == 0x0d6ef36e, "StringId" == 0xd2c4765d, "In0" == 0x0b87d372
        // and "In1" == 0x0b87d373, all of which appear on
        // Addons/Mode1/Mode1/UI/InGame/Hud/Shared/PF_IndexObjectiveIconSelect wired between the
        // entity types those names belong to. So a field may be given as a readable name and hashed
        // here, or as an explicit 0x id when the name is unknown.
        public static int ExecuteAdd(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null, source = null, target = null, sourceField = null, targetField = null;
            string sourceEbx = null, targetEbx = null;
            string kind = "property";
            string flagsText = null, realmText = null, propertyTypeText = null;
            bool nonStaticSource = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--flags" && i + 1 < args.Length) flagsText = args[++i];
                else if (args[i] == "--realm" && i + 1 < args.Length) realmText = args[++i];
                else if (args[i] == "--property-type" && i + 1 < args.Length) propertyTypeText = args[++i];
                else if (args[i] == "--non-static-source") nonStaticSource = true;
                else if (args[i] == "--source" && i + 1 < args.Length) source = args[++i];
                else if (args[i] == "--target" && i + 1 < args.Length) target = args[++i];
                else if (args[i] == "--source-ebx" && i + 1 < args.Length) sourceEbx = args[++i];
                else if (args[i] == "--target-ebx" && i + 1 < args.Length) targetEbx = args[++i];
                else if (args[i] == "--source-field" && i + 1 < args.Length) sourceField = args[++i];
                else if (args[i] == "--target-field" && i + 1 < args.Length) targetField = args[++i];
                else if (args[i] == "--kind" && i + 1 < args.Length) kind = args[++i].ToLowerInvariant();
                // An unrecognised option used to be skipped and its VALUE taken as the asset path,
                // because the value does not start with "--". So `--source-ebx <asset>` on a build
                // that did not know the flag silently retargeted the whole command at <asset> and
                // wrote the connection into the wrong file. Refuse instead.
                else if (args[i].StartsWith("--"))
                { Console.Error.WriteLine("connection add: unknown option '" + args[i] + "'"); return 1; }
                else ebxPath = args[i];
            }
            if (ebxPath == null)     { Console.Error.WriteLine("connection add: missing <ebx>"); return 1; }
            if (source == null)      { Console.Error.WriteLine("connection add: --source is required"); return 1; }
            if (target == null)      { Console.Error.WriteLine("connection add: --target is required"); return 1; }
            if (sourceField == null) { Console.Error.WriteLine("connection add: --source-field is required"); return 1; }
            if (targetField == null) { Console.Error.WriteLine("connection add: --target-field is required"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("connection add: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("connection add: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine("connection add: asset not found: " + ebxPath); return 1; }
            FrostySdk.IO.EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("connection add: load failed: " + ex.Message); return 1; }

            // Endpoints accept the same selector forms as `--object`: an instance GUID, a type
            // name, or "TypeName[N]". A GUID-only rule cannot name an object created earlier in the
            // same pipeline, because its GUID is assigned at creation and never makes it back into
            // the JSON -- and partition objects that are not world objects (shape data, for one) are
            // created exactly that way. With --source-ebx / --target-ebx the same selector is
            // resolved inside THAT asset instead, and the endpoint becomes an external ref.
            ConnectionEndpoint sourceEp, targetEp;
            if (!ResolveSide(asset, entry, "--source", source, sourceEbx, out sourceEp)) return 1;
            if (!ResolveSide(asset, entry, "--target", target, targetEbx, out targetEp)) return 1;

            object owner; PropertyInfo listProp;
            IList list = FindConnectionList(asset, kind, out owner, out listProp);
            if (list == null)
            {
                Console.Error.WriteLine("connection add: no '" + kind + "' connection list on " + ebxPath +
                    "; 'connection ls' shows which kinds it has");
                return 1;
            }

            Type elemType = ElementTypeOf(list);
            if (elemType == null) { Console.Error.WriteLine("connection add: cannot determine the connection type"); return 1; }

            // PropertyConnection.Flags is a packed bitfield, decoded against 241,732 shipped and
            // modded instances:
            //   bits 0-2 (0x07) realm  -- 0 Invalid, 1 ClientAndServer, 2 Client, 3 Server,
            //                             4 NetworkedClient, 5 NetworkedClientAndServer
            //   bit  3   (0x08) source cannot be static
            //   bits 4-5 (0x30) property type -- 0 Default, 1 Interface, 2 Exposed, 3 Invalid
            //   bits 6+         unused; zero in all 241,732 observed instances
            // Realm is the field that decides whether a connection does anything: one on the wrong
            // realm wires up fine in the editor and is silently inert in game. That makes a bare
            // numeric default a trap, so --realm/--property-type name the intent and --flags stays
            // available as a raw override.
            //
            // The default is ClientAndServer rather than a single realm: it is the only value that
            // behaves on both sides, so a caller who has not thought about realm gets a connection
            // that runs rather than one that quietly does not.
            int flags;
            if (flagsText != null)
            {
                if (!int.TryParse(flagsText, NumberStyles.None, CultureInfo.InvariantCulture, out flags))
                { Console.Error.WriteLine("connection add: --flags must be a non-negative integer"); return 1; }
            }
            else
            {
                uint built; string flagErr;
                if (!TryBuildFlags(realmText, propertyTypeText, nonStaticSource, out built, out flagErr))
                { Console.Error.WriteLine("connection add: " + flagErr); return 1; }
                flags = unchecked((int)built);
            }

            object conn;
            try { conn = Activator.CreateInstance(elemType); }
            catch (Exception ex) { Console.Error.WriteLine("connection add: cannot construct connection: " + ex.Message); return 1; }

            try
            {
                SetEndpoint(conn, elemType, "Source", sourceEp, sourceField);
                SetEndpoint(conn, elemType, "Target", targetEp, targetField);
                PropertyInfo flagsProp = elemType.GetProperty("Flags", BindingFlags.Public | BindingFlags.Instance);
                if (flagsProp != null)
                    flagsProp.SetValue(conn, Convert.ChangeType(flags,
                        Nullable.GetUnderlyingType(flagsProp.PropertyType) ?? flagsProp.PropertyType, CultureInfo.InvariantCulture));

                // An EventConnection has no packed Flags: its realm is the TargetType enum
                // (EventConnectionTargetType_Server and friends). Without this, --realm was accepted
                // and silently dropped on exactly the connection kind where realm decides whether
                // the event is raised on the authoritative side at all.
                PropertyInfo targetTypeProp = elemType.GetProperty("TargetType", BindingFlags.Public | BindingFlags.Instance);
                if (targetTypeProp != null)
                {
                    Type tt = Nullable.GetUnderlyingType(targetTypeProp.PropertyType) ?? targetTypeProp.PropertyType;
                    if (tt.IsEnum)
                    {
                        string want = realmText ?? "ClientAndServer";
                        string full = want.StartsWith("EventConnectionTargetType", StringComparison.OrdinalIgnoreCase)
                                        ? want : "EventConnectionTargetType_" + want;
                        if (!Enum.IsDefined(tt, full))
                        {
                            Console.Error.WriteLine("connection add: '" + want + "' is not a valid event realm for " +
                                tt.Name + " (try Client, Server, ClientAndServer or NetworkedClient)");
                            return 1;
                        }
                        targetTypeProp.SetValue(conn, Enum.Parse(tt, full));
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("connection add: " + ex.Message); return 1; }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "add {0} connection {1}.{2} -> {3}.{4} on {5}",
                    listProp.Name, Describe(sourceEbx, source), sourceField,
                    Describe(targetEbx, target), targetField, ebxPath);
                return 0;
            }

            int index = list.Add(conn);

            // The TARGET object must be marked as a client-side property target, or the engine never
            // binds the connection: it loads, shows in the editor, and is silently inert in game.
            // DataContainer.Flags bit 27 (0x08000000) is that mark. Across every vanilla vehicle
            // blueprint, each object with an incoming ClientAndServer or Client property connection
            // has it set, with no exceptions in ~1,800 cases, while Server-only targets mostly don't.
            // Field case: an E-Web turret's CameraComponentData (no vanilla incoming wires, so no
            // bit) ignored a new Client -> OverrideTransform link until the bit was set by hand.
            object markedTarget = null; uint targetFlagsBefore = 0;
            if (!targetEp.IsExternal && NeedsClientTargetBit(elemType.Name, flags))
            {
                uint before;
                if (TrySetClientTargetBit(targetEp.InternalObject, out before) && (before & ClientTargetBit) == 0)
                { markedTarget = targetEp.InternalObject; targetFlagsBefore = before; }
            }

            // Bundling walks EbxAsset.Dependencies. A cross-partition endpoint whose asset is not
            // listed there can be left out of the bundle the connection ships in, and the reference
            // then resolves to nothing at load -- with no error, which is the whole failure mode
            // this command exists to avoid.
            if (sourceEp.IsExternal) { try { asset.AddDependency(sourceEp.FileGuid); } catch { } }
            if (targetEp.IsExternal) { try { asset.AddDependency(targetEp.FileGuid); } catch { } }

            if (!EbxWriteCommand.CallModifyEbx(ebxPath, asset, logger))
            {
                list.RemoveAt(index);
                if (markedTarget != null) WriteDataContainerFlags(markedTarget, targetFlagsBefore);
                return 1;
            }
            var result = new Dictionary<string, string>
            {
                { "ebx", ebxPath }, { "kind", listProp.Name },
                { "index", index.ToString(CultureInfo.InvariantCulture) },
                { "source", Describe(sourceEbx, source) + "." + sourceField },
                { "target", Describe(targetEbx, target) + "." + targetField },
                { "flags", flags.ToString(CultureInfo.InvariantCulture) }
            };
            if (markedTarget != null) result["target_client_bit"] = "set (target Flags |= 0x08000000)";
            formatter.WriteResult("OK", "connection add", result);
            return 0;
        }

        /// <summary>DataContainer.Flags bit marking an object as the target of client-realm property connections.</summary>
        internal const uint ClientTargetBit = 0x08000000u;

        /// <summary>
        /// True when a connection of this element type and packed Flags needs its target marked:
        /// a PropertyConnection whose realm (bits 0-2) is ClientAndServer (1) or Client (2).
        /// </summary>
        internal static bool NeedsClientTargetBit(string connectionTypeName, int connectionFlags)
        {
            if (connectionTypeName != "PropertyConnection") return false;
            int realm = connectionFlags & 0x07;
            return realm == 1 || realm == 2;
        }

        /// <summary>
        /// ORs <see cref="ClientTargetBit"/> into obj.Flags. Returns false when obj has no integral
        /// Flags property; <paramref name="before"/> receives the previous value.
        /// </summary>
        internal static bool TrySetClientTargetBit(object obj, out uint before)
        {
            before = 0;
            object cur;
            if (!TryReadDataContainerFlags(obj, out cur)) return false;
            before = unchecked((uint)Convert.ToInt64(cur, CultureInfo.InvariantCulture));
            if ((before & ClientTargetBit) != 0) return true;
            return WriteDataContainerFlags(obj, before | ClientTargetBit);
        }

        private static bool TryReadDataContainerFlags(object obj, out object value)
        {
            value = null;
            if (obj == null) return false;
            PropertyInfo p = obj.GetType().GetProperty("Flags", BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanRead || !p.CanWrite) return false;
            Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (!IsIntegral(t)) return false;
            value = p.GetValue(obj);
            return value != null;
        }

        private static bool WriteDataContainerFlags(object obj, uint value)
        {
            PropertyInfo p = obj.GetType().GetProperty("Flags", BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return false;
            Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object boxed;
            if (t == typeof(uint)) boxed = value;
            else if (t == typeof(int)) boxed = unchecked((int)value);
            else if (t == typeof(ulong)) boxed = (ulong)value;
            else if (t == typeof(long)) boxed = (long)value;
            else return false;
            p.SetValue(obj, boxed);
            return true;
        }

        public static int ExecuteRemove(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null, kind = "property";
            int index = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--index" && i + 1 < args.Length) index = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--kind" && i + 1 < args.Length) kind = args[++i].ToLowerInvariant();
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }
            if (ebxPath == null) { Console.Error.WriteLine("connection remove: missing <ebx>"); return 1; }
            if (index < 0) { Console.Error.WriteLine("connection remove: --index is required (0-based, within the kind)"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("connection remove: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("connection remove: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine("connection remove: asset not found: " + ebxPath); return 1; }
            FrostySdk.IO.EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("connection remove: load failed: " + ex.Message); return 1; }

            object owner; PropertyInfo listProp;
            IList list = FindConnectionList(asset, kind, out owner, out listProp);
            if (list == null) { Console.Error.WriteLine("connection remove: no '" + kind + "' connection list on " + ebxPath); return 1; }
            if (index >= list.Count)
            {
                Console.Error.WriteLine("connection remove: index " + index + " is out of range; " +
                    listProp.Name + " holds " + list.Count);
                return 1;
            }

            object doomed = list[index];
            string described = DescribeEndpoint(doomed, "Source") + " -> " + DescribeEndpoint(doomed, "Target");
            if (DryRun.Active) { DryRun.Report(logger, "remove {0}[{1}] ({2}) from {3}", listProp.Name, index, described, ebxPath); return 0; }

            list.RemoveAt(index);
            if (!EbxWriteCommand.CallModifyEbx(ebxPath, asset, logger)) { list.Insert(index, doomed); return 1; }
            formatter.WriteResult("OK", "connection remove", new Dictionary<string, string>
            {
                { "ebx", ebxPath }, { "kind", listProp.Name },
                { "index", index.ToString(CultureInfo.InvariantCulture) }, { "was", described }
            });
            return 0;
        }

        // Accepts a readable field name (hashed the way Frostbite hashes them) or an explicit
        // 0x-prefixed / decimal id for fields whose name is not known.
        private static readonly Dictionary<string, uint> Realms = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "Invalid", 0 }, { "ClientAndServer", 1 }, { "Client", 2 }, { "Server", 3 },
            { "NetworkedClient", 4 }, { "NetworkedClientAndServer", 5 },
        };

        private static readonly Dictionary<string, uint> PropertyTypes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "Default", 0 }, { "Interface", 1 }, { "Exposed", 2 }, { "Invalid", 3 },
        };

        internal static bool TryBuildFlags(string realm, string propertyType, bool nonStaticSource,
                                           out uint flags, out string error)
        {
            flags = 0; error = null;
            uint r;
            if (!Realms.TryGetValue(realm ?? "ClientAndServer", out r))
            { error = "unknown --realm '" + realm + "' (Invalid|ClientAndServer|Client|Server|NetworkedClient|NetworkedClientAndServer)"; return false; }
            uint pt;
            if (!PropertyTypes.TryGetValue(propertyType ?? "Default", out pt))
            { error = "unknown --property-type '" + propertyType + "' (Default|Interface|Exposed|Invalid)"; return false; }
            flags = (r & 0x07u) | (nonStaticSource ? 0x08u : 0u) | ((pt & 0x03u) << 4);
            return true;
        }

        internal static bool TryResolveFieldId(string text, out uint id)
        {
            id = 0;
            if (string.IsNullOrEmpty(text)) return false;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
            uint parsed;
            if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)) { id = parsed; return true; }
            // Shipped connection records store port ids as SIGNED ints, so a graph dump hands back
            // values like -763641474 for StreamIn (0xD27BC17E). Without this, uint.TryParse fails and
            // the negative literal falls through to being HASHED AS TEXT -- which silently yields a
            // plausible-looking but wrong id (0x5564917E here) and an unresolvable name. Only strings
            // that actually start with '-' take this path, so real port names are unaffected.
            int signed;
            if (text[0] == '-' && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out signed))
            { id = unchecked((uint)signed); return true; }
            id = unchecked((uint)FrostySdk.Utils.HashString(text));
            return true;
        }

        private static string Describe(string otherEbx, string selector)
        {
            return otherEbx == null ? selector : otherEbx + "#" + selector;
        }

        /// <summary>
        /// Resolves one side of a connection to an endpoint.
        ///
        /// Without <paramref name="otherEbx"/> the selector names an object in the asset being
        /// edited and the result is an internal ref -- the only thing this command could author
        /// before. With it, the selector is resolved INSIDE that other asset and the result is an
        /// external ref: the other asset's file guid plus the endpoint's exported instance guid.
        ///
        /// That second form is what real logic is made of. Kashyyyk's mode graph is 63 event
        /// connections stored on Levels/MP/Kashyyyk_01/GameModes whose endpoints live in
        /// Levels/MP/Kashyyyk_01/GameModes_Flow; with only internal refs available, such a graph can
        /// be inspected but never extended.
        /// </summary>
        private static bool ResolveSide(FrostySdk.IO.EbxAsset asset, EbxAssetEntry entry, string flag,
                                        string selector, string otherEbx, out ConnectionEndpoint ep)
        {
            ep = default(ConnectionEndpoint);

            if (otherEbx == null)
            {
                Guid g;
                bool isGuid = Guid.TryParse(selector, out g);
                object obj = ResolveEndpoint(asset, selector, isGuid, g);
                if (obj == null)
                {
                    Console.Error.WriteLine("connection add: " + flag + " not found in " + entry.Name + ": " + selector);
                    return false;
                }
                ep = ConnectionEndpoint.Internal(obj);
                return true;
            }

            // `new:` creates the endpoint object in the asset being edited and depends on this
            // command saving it. Across assets there is nothing here to save it into, so the object
            // would be dropped and the reference left addressing a GUID that exists nowhere.
            if (selector != null && selector.StartsWith("new:", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("connection add: " + flag + " new: cannot be combined with " +
                    flag + "-ebx -- create the object in " + otherEbx +
                    " first (ebx new-object), then name its GUID here");
                return false;
            }

            EbxAssetEntry other = App.AssetManager.GetEbxEntry(otherEbx);
            if (other == null)
            {
                Console.Error.WriteLine("connection add: " + flag + "-ebx asset not found: " + otherEbx);
                return false;
            }

            // An external ref to the partition's own file guid is not an internal ref by another
            // name; the runtime reads it as a broken import.
            if (other.Guid == entry.Guid)
            {
                Console.Error.WriteLine("connection add: " + flag + "-ebx names the asset being edited (" +
                    otherEbx + "); drop it and the endpoint resolves in this partition as an internal ref");
                return false;
            }

            FrostySdk.IO.EbxAsset otherAsset;
            try { otherAsset = App.AssetManager.GetEbx(other); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("connection add: could not load " + otherEbx + ": " + ex.Message);
                return false;
            }

            object endpointObj = EbxWriteCommand.FindPartitionObject(otherAsset, selector);
            if (endpointObj == null)
            {
                Console.Error.WriteLine("connection add: " + flag + " not found in " + otherEbx + ": " + selector);
                return false;
            }

            string error;
            if (!TryExternalEndpoint(endpointObj, other.Guid, out ep, out error))
            {
                Console.Error.WriteLine("connection add: " + flag + " " + selector + " in " + otherEbx + ": " + error);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Turns an object in ANOTHER asset into an external endpoint.
        ///
        /// An external PointerRef addresses its target by exported instance guid, so an endpoint
        /// that was never exported cannot be addressed at all: the reference is written happily,
        /// resolves to nothing at load, and the connection is inert with no error anywhere. Refusing
        /// here is the only place that failure is visible.
        /// </summary>
        internal static bool TryExternalEndpoint(object endpointObj, Guid fileGuid,
                                                 out ConnectionEndpoint ep, out string error)
        {
            ep = default(ConnectionEndpoint);
            error = null;
            if (endpointObj == null) { error = "endpoint object is null"; return false; }

            Guid exported = PointerRefUtil.InstanceGuid(endpointObj);
            if (exported == Guid.Empty)
            {
                error = endpointObj.GetType().Name + " has no exported instance GUID. An external " +
                        "reference addresses its target by that GUID, so this endpoint would resolve " +
                        "to nothing in game and the connection would never fire.";
                return false;
            }

            ep = ConnectionEndpoint.External(fileGuid, exported);
            return true;
        }

        internal static void SetEndpoint(object conn, Type connType, string prefix,
                                         ConnectionEndpoint ep, string field)
        {
            PropertyInfo refProp = connType.GetProperty(prefix, BindingFlags.Public | BindingFlags.Instance);
            if (refProp == null) throw new InvalidOperationException("connection type has no '" + prefix + "' member");
            Type pointerRefType = refProp.PropertyType;

            // The external case reuses EbxRefWriter's builder rather than repeating it: the real
            // PointerRef is an immutable struct whose Type/External are set only through its
            // constructors, and a hand-rolled second copy of that is how a ref gets written with
            // Type left at Null -- which serializes cleanly and points at nothing.
            object pointerRef = ep.IsExternal
                ? FrostyCLI.Core.EbxRefWriter.BuildExternalPointerRef(pointerRefType, ep.FileGuid, ep.ClassGuid)
                : BuildInternalPointerRef(pointerRefType, ep.InternalObject);
            refProp.SetValue(conn, pointerRef);

            uint id;
            if (!TryResolveFieldId(field, out id))
                throw new InvalidOperationException("could not read field '" + field + "'");

            // A PropertyConnection stores the field twice: SourceFieldId as a signed int and
            // SourceField as the CString "0x<lowercase hex>" of the same value. Shipped data always
            // carries both (verified on PF_IndexObjectiveIconSelect: SourceFieldId -798498252 next
            // to SourceField "0xd067e234"), so writing only one leaves an inconsistent record.
            bool wroteAny = false;
            foreach (string suffix in new[] { "FieldId", "EventId", "Field", "Event", "Property" })
            {
                PropertyInfo candidate = connType.GetProperty(prefix + suffix, BindingFlags.Public | BindingFlags.Instance);
                if (candidate == null) continue;
                Type ct = Nullable.GetUnderlyingType(candidate.PropertyType) ?? candidate.PropertyType;
                if (IsIntegral(ct))
                {
                    object value = ct == typeof(int) ? (object)unchecked((int)id)
                                 : ct == typeof(long) ? (object)unchecked((long)id)
                                 : Convert.ChangeType(id, ct, CultureInfo.InvariantCulture);
                    candidate.SetValue(conn, value);
                    wroteAny = true;
                }
                else if (ct.Name == "CString")
                {
                    object cstr = MakeCString(ct, "0x" + id.ToString("x8", CultureInfo.InvariantCulture));
                    if (cstr != null) { candidate.SetValue(conn, cstr); wroteAny = true; }
                }
                else if (ct.Name == "EventSpec")
                {
                    // An EventConnection names its endpoints with an EventSpec struct (Id + Name)
                    // rather than a bare id, so the integral/CString branches above never match and
                    // event connections were rejected outright. Shipped EventSpecs carry the id in
                    // Id and render Name as "0x<hex>" when no authored string was present, so both
                    // are written: the id always, and the readable name when one was supplied.
                    object spec = candidate.GetValue(conn) ?? Activator.CreateInstance(ct);

                    PropertyInfo idProp = ct.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                    if (idProp != null)
                    {
                        Type it = Nullable.GetUnderlyingType(idProp.PropertyType) ?? idProp.PropertyType;
                        object v = it == typeof(int) ? (object)unchecked((int)id)
                                 : it == typeof(long) ? (object)unchecked((long)id)
                                 : Convert.ChangeType(id, it, CultureInfo.InvariantCulture);
                        idProp.SetValue(spec, v);
                    }

                    PropertyInfo nameProp = ct.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null)
                    {
                        bool literal = field.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                                       uint.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out _);
                        string text = literal ? "0x" + id.ToString("x8", CultureInfo.InvariantCulture) : field;
                        Type nt = Nullable.GetUnderlyingType(nameProp.PropertyType) ?? nameProp.PropertyType;
                        object nv = nt.Name == "CString" ? MakeCString(nt, text) : (object)text;
                        if (nv != null) nameProp.SetValue(spec, nv);
                    }

                    // EventSpec is a struct: it was boxed on read, so it must be written back.
                    candidate.SetValue(conn, spec);
                    wroteAny = true;
                }
            }
            if (!wroteAny)
                throw new InvalidOperationException("no '" + prefix + "Field'-style member on " +
                    connType.Name + "; this connection kind is not supported yet");
        }

        private static object BuildInternalPointerRef(Type pointerRefType, object endpointObj)
        {
            ConstructorInfo internalCtor = pointerRefType.GetConstructor(new[] { typeof(object) });
            if (internalCtor != null) return internalCtor.Invoke(new[] { endpointObj });

            // The generated PointerRef takes the concrete object type rather than System.Object.
            foreach (ConstructorInfo c in pointerRefType.GetConstructors())
            {
                ParameterInfo[] ps = c.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(endpointObj))
                    return c.Invoke(new[] { endpointObj });
            }
            throw new InvalidOperationException("cannot build an internal PointerRef for " + endpointObj.GetType().Name);
        }

        private static object MakeCString(Type cstringType, string text)
        {
            ConstructorInfo ctor = cstringType.GetConstructor(new[] { typeof(string) });
            return ctor != null ? ctor.Invoke(new object[] { text }) : null;
        }

        private static bool IsIntegral(Type t)
        {
            Type u = Nullable.GetUnderlyingType(t) ?? t;
            return u == typeof(uint) || u == typeof(int) || u == typeof(ulong) || u == typeof(long)
                || u == typeof(ushort) || u == typeof(short) || u == typeof(byte) || u == typeof(sbyte);
        }

        /// <summary>
        /// Resolves a connection endpoint: an instance GUID, a `TypeName`/`TypeName[N]` selector,
        /// or `new:&lt;TypeName&gt;` to create the object and wire it in one step.
        ///
        /// The `new:` form exists because a partition object nothing references yet is unreachable
        /// from the root, and EBX serialization keeps only the reachable graph -- so creating it in
        /// one step and linking it in the next loses it silently. That is not hypothetical: shape
        /// data created with `ebx new-object` and left unreferenced vanished on save, and the layer
        /// tested clean only because it was empty again.
        ///
        /// It matters for shape data specifically. A ZoneVectorShapeData is GameObjectData, not
        /// EntityData, so it must NOT be listed under a layer's Objects -- shipped levels hold it as
        /// a bare partition object reached only through a link. Listing one under Objects asks the
        /// engine to spawn something unspawnable and the level dies late rather than erroring.
        /// </summary>
        private static object ResolveEndpoint(FrostySdk.IO.EbxAsset asset, string spec, bool isGuid, Guid guid)
        {
            if (isGuid) return FindByInstanceGuid(asset, guid);

            if (spec != null && spec.StartsWith("new:", StringComparison.OrdinalIgnoreCase))
            {
                string typeName = spec.Substring(4);
                object created = EbxWriteCommand.CreateObjectByType(typeName);
                if (created == null)
                { Console.Error.WriteLine("connection add: new: type not found: " + typeName); return null; }
                try
                {
                    MethodInfo setGuid = created.GetType().GetMethod("SetInstanceGuid",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (setGuid != null)
                        setGuid.Invoke(created, new object[] {
                            new AssetClassGuid(Guid.NewGuid(), LevelCommand.NextInternalId(asset)) });
                    asset.AddObject(created, false);
                    InProcessObjects.Mark(created);
                }
                catch (Exception ex)
                { Console.Error.WriteLine("connection add: could not create " + typeName + ": " + ex.Message); return null; }
                return created;
            }

            return EbxWriteCommand.FindPartitionObject(asset, spec);
        }

        private static object FindByInstanceGuid(FrostySdk.IO.EbxAsset asset, Guid guid)
        {
            foreach (object obj in asset.Objects)
            {
                if (obj == null) continue;
                try
                {
                    MethodInfo gig = obj.GetType().GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                    if (gig == null) continue;
                    object acg = gig.Invoke(obj, null);
                    if (acg != null && string.Equals(acg.ToString(), guid.ToString(), StringComparison.OrdinalIgnoreCase))
                        return obj;
                }
                catch { }
            }
            return null;
        }

        // "property" matches PropertyConnections, "link" LinkConnections, "event" EventConnections --
        // whichever object in the partition actually carries them.
        private static IList FindConnectionList(FrostySdk.IO.EbxAsset asset, string kind, out object owner, out PropertyInfo listProp)
        {
            owner = null;
            listProp = null;
            foreach (object obj in asset.Objects)
            {
                if (obj == null) continue;
                foreach (PropertyInfo pi in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (pi.Name.IndexOf("Connection", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (pi.Name.IndexOf(kind, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    IList list;
                    try { list = pi.GetValue(obj) as IList; } catch { continue; }
                    if (list == null || list.IsReadOnly || list.IsFixedSize) continue;
                    owner = obj;
                    listProp = pi;
                    return list;
                }
            }
            return null;
        }

        private static Type ElementTypeOf(IList list)
        {
            Type t = list.GetType();
            if (t.IsGenericType) return t.GetGenericArguments()[0];
            return list.Count > 0 && list[0] != null ? list[0].GetType() : null;
        }

        // Builds "<ref>.<field>" for the Source/Target side of a connection. <prefix> is "Source" or
        // "Target"; the field is whichever of <prefix>Field / <prefix>FieldId / <prefix>Event exists.
        private static string DescribeEndpoint(object conn, string prefix)
        {
            Type t = conn.GetType();
            object refObj = GetProp(conn, t, prefix);            // PointerRef
            string refDesc = DescribeRef(refObj);
            string field = null;
            // Order matters. A connection stores the field twice -- `SourceFieldId` as the numeric id
            // and `SourceField` as that same id pre-rendered "0x0d6ef36e" -- and an EventSpec.Name is
            // likewise written as hex when the partition carried no authored string. Reaching a
            // hex-rendered member first would short-circuit with hex and make the wordlist look
            // broken, so the resolvable members are tried first.
            foreach (string suffix in new[] { "Event", "FieldId", "EventId", "Field", "Property" })
            {
                object f = GetProp(conn, t, prefix + suffix);
                if (f == null) continue;

                // An EventSpec carries its own name; use it only when it is a real name, not the id.
                object specName = GetProp(f, f.GetType(), "Name");
                if (specName != null && IsRealName(specName.ToString())) { field = specName.ToString(); break; }

                object specId = GetProp(f, f.GetType(), "Id");
                if (specId != null && IsIntegral(specId.GetType()))
                { field = PortNames.Describe(Convert.ToInt32(specId, CultureInfo.InvariantCulture)); break; }

                if (IsIntegral(f.GetType()))
                {
                    int id = Convert.ToInt32(f, CultureInfo.InvariantCulture);
                    if (id != 0) { field = PortNames.Describe(id); break; }
                    continue;
                }

                string s = f.ToString();
                if (!string.IsNullOrEmpty(s) && s != "0") { field = s; break; }
            }
            return field != null ? refDesc + "." + field : refDesc;
        }

        // "0x8b8b5d9e" is an id rendered as text, not a port name.
        private static bool IsRealName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return true;
            for (int i = 2; i < s.Length; i++) if (!Uri.IsHexDigit(s[i])) return true;
            return false;
        }

        // Reports the port-name vocabulary and reverse-resolves ids. Without it, a missing wordlist
        // and a genuinely unnamed id are indistinguishable -- both print bare hex.
        public static int ExecuteNames(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string namesFile = null; var ids = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--names" && i + 1 < args.Length) namesFile = args[++i];
                else if (args[i] == "--resolve" && i + 1 < args.Length) ids.Add(args[++i]);
                else if (args[i] == "--hash" && i + 1 < args.Length)
                {
                    string w = args[++i];
                    uint h = PortNames.Hash(w);
                    formatter.WriteResult("OK", "connection names", new Dictionary<string, string>
                    { { "text", w }, { "id_u32", h.ToString(CultureInfo.InvariantCulture) },
                      { "id_i32", unchecked((int)h).ToString(CultureInfo.InvariantCulture) },
                      { "hex", "0x" + h.ToString("x8") } });
                }
                else { Console.Error.WriteLine("connection names: unknown option '" + args[i] + "'"); return 1; }
            }

            bool ok = PortNames.Load(namesFile, true);
            formatter.WriteResult(ok ? "OK" : "WARN", "connection names", new Dictionary<string, string>
            {
                { "loaded", ok.ToString() },
                { "source", PortNames.LoadedFrom ?? "<none found>" },
                { "words", PortNames.WordCount.ToString(CultureInfo.InvariantCulture) },
            });

            foreach (string spec in ids)
            {
                uint id;
                if (!TryResolveFieldId(spec, out id))
                { Console.Error.WriteLine("connection names: cannot read id '" + spec + "'"); return 1; }
                IList<string> names = PortNames.Resolve(id);
                formatter.WriteResult("OK", "connection names", new Dictionary<string, string>
                {
                    { "id", spec }, { "hex", "0x" + id.ToString("x8") },
                    { "candidates", names.Count == 0 ? "<unresolved>" : string.Join("|", names) },
                });
            }
            return 0;
        }

        // Reads a member by name as either a property OR a field. EbxImportReference exposes FileGuid /
        // ClassGuid as public FIELDS (not properties), so a property-only lookup would return null and
        // every external connection endpoint would render as "ext:?".
        private static object GetProp(object obj, Type t, string name)
        {
            PropertyInfo pi = t.GetProperty(name);
            if (pi != null) return pi.GetValue(obj);
            FieldInfo fi = t.GetField(name);
            return fi != null ? fi.GetValue(obj) : null;
        }

        // Describes a PointerRef: Null, an internal sub-object (its type + instance guid), or an external
        // file reference (file/class guids). Reflection-based so it doesn't bind a specific SDK shape.
        private static string DescribeRef(object pr)
        {
            if (pr == null) return "<null>";
            Type t = pr.GetType();
            object type = GetProp(pr, t, "Type");
            string typeStr = type != null ? type.ToString() : "";
            if (typeStr == "Null") return "<null>";

            if (typeStr == "Internal")
            {
                object inner = GetProp(pr, t, "Internal");
                if (inner == null) return "<internal:?>";
                string tn = inner.GetType().Name;
                try
                {
                    MethodInfo gig = inner.GetType().GetMethod("GetInstanceGuid");
                    object g = gig?.Invoke(inner, null);
                    return tn + "(" + (g != null ? g.ToString() : "?") + ")";
                }
                catch { return tn; }
            }

            if (typeStr == "External")
            {
                object ext = GetProp(pr, t, "External");
                if (ext == null) return "<external:?>";
                Type et = ext.GetType();
                object fileGuid  = GetProp(ext, et, "FileGuid");
                object classGuid = GetProp(ext, et, "ClassGuid");
                EbxAssetEntry fe = null;
                if (fileGuid is Guid fg) fe = App.AssetManager.GetEbxEntry(fg);
                string fileName = fe != null ? fe.Name : (fileGuid != null ? fileGuid.ToString() : "?");
                return "ext:" + fileName + (classGuid != null ? "#" + classGuid : "");
            }
            return typeStr;
        }
    }
}
