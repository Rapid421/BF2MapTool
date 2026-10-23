using Frosty.Core;
using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
#if FROSTY_107
using FrostySdk.Managers.Entries;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ShaderDataPlugin
{
    // Stand-in for Frosty.Core.Windows.FrostyTaskWindow (progress UI) so the parser runs headless.
    public class FrostyTaskWindow { public void Update(string text = null, double progress = 0) { } }


    public enum ShaderType
    {
        PixelShader,
        VertexShader
    }

    internal class ShaderStreamableTexture
    {
        public uint NameHash;
        public uint TextureHandle;
        public uint CoordinateType;
        public uint VertexUsage;
        public float TilesPerCoordinate;
    }

    public class ShaderDb
    {

        #region Properties
        public bool Loaded { get; private set; }
        private int numStreams
        {
            get
            {
                if (Version == (int)ShaderDBVersion.StarWarsBattlefront1)
                {
                    return 6;
                }
                else if (Version >= (int)ShaderDBVersion.StarWarsBattlefront2Alpha)
                {
                    if (Version == (int)ShaderDBVersion.MassEffectAndromeda
                    || Version == (int)ShaderDBVersion.Battlefield1
                    || Version == (int)ShaderDBVersion.BattlefieldV
                    || ProfilesLibrary.DataVersion == (int)ProfileVersion.MirrorsEdgeCatalyst)
                        return 8;
                    else
                        return 16;
                }
                else
                {
                    return 4;
                }
            }
        }
        #endregion

        #region Fields
        public static uint Version = 150;
        public static bool UseCache = false;

        private List<BundleEntry> entries;
        private AssetEntry assetEntry;
        private ILogger logger;

        // cached data so subsequent loads are faster
        private static Dictionary<uint, List<RenderPath>> shaderMap = new Dictionary<uint, List<RenderPath>>();
        private static Dictionary<uint, string> textureHashMap = new Dictionary<uint, string>();
        private static Dictionary<uint, List<ShaderStreamableTexture>> streamableTextureMap =
            new Dictionary<uint, List<ShaderStreamableTexture>>();

        private uint dbSize = 0;
        static bool firstTimeLoad = true;
        static bool textureCacheLoaded = false;
        #endregion

        public ShaderDb(List<BundleEntry> entriesToSearch, AssetEntry asset, ILogger inLogger)
        {
            entries = entriesToSearch;
            assetEntry = asset;
            logger = inLogger;
        }

        private static bool globalLoadAttempted = false;

        /// <summary>
        /// Scans all shader databases into the static shaderMap cache if it hasn't
        /// been done yet this session. Safe to call repeatedly - only does real work once.
        /// Call this before TryGetTextureConstants if you can't guarantee the user
        /// has already opened a shader graph in the plugin's own editor.
        /// </summary>

        // Headless API (frostycli): load every shader database once, then look a graph up by name.
        public static bool LoadAll(ILogger inLogger) { EnsureLoaded(inLogger); return !firstTimeLoad; }
        public static bool TryGetRenderPaths(string shaderGraphName, out List<RenderPath> paths)
        {
            uint hash = (uint)Utils.HashString(shaderGraphName, true);
            return shaderMap.TryGetValue(hash, out paths) && paths != null;
        }

        public static List<object[]> GetStreamableTextures(string shaderGraphName)
        {
            uint hash = (uint)Utils.HashString(shaderGraphName, true);
            var list = new List<object[]>();
            List<ShaderStreamableTexture> texs;
            if (!streamableTextureMap.TryGetValue(hash, out texs)) return list;
            foreach (ShaderStreamableTexture t in texs)
            {
                string name; textureHashMap.TryGetValue(t.NameHash, out name);
                list.Add(new object[] { name ?? ("0x" + t.NameHash.ToString("X8")), t.TextureHandle, t.CoordinateType, t.VertexUsage, t.TilesPerCoordinate });
            }
            return list;
        }
        public static IEnumerable<uint> LoadedShaderHashes { get { return shaderMap.Keys; } }

        public static void EnsureLoaded(ILogger inLogger)
        {
            if (!firstTimeLoad || globalLoadAttempted)
                return;

            globalLoadAttempted = true;

            // Battlefront 2015's constant texture table stores name hashes only, so the
            // map has to exist before any database is read. Keyed off the profile rather
            // than Version, which isn't known until a database has already been parsed.
            if (ProfilesLibrary.DataVersion == (int)ProfileVersion.StarWarsBattlefront
                && textureHashMap.Count == 0)
            {
                foreach (EbxAssetEntry asset in App.AssetManager.EnumerateEbx("TextureBaseAsset"))
                {
                    uint texHash = (uint)Utils.HashString(asset.Name, true);
                    if (!textureHashMap.ContainsKey(texHash))
                        textureHashMap.Add(texHash, asset.Name);
                }
            }

            IEnumerable<ResAssetEntry> shaderDbs = App.AssetManager.EnumerateRes((uint)Utils.HashString("IShaderDatabase", true));
            bool failed = false;

            // dummy instance purely to reuse the existing Read() parsing logic;
            // targetData is unused inside Read() so null is safe here
            ShaderDb loader = new ShaderDb(new List<BundleEntry>(), null, inLogger);

            foreach (ResAssetEntry resEntry in shaderDbs)
            {
                loader.Read(resEntry, null);
                if (!loader.Loaded)
                {
                    inLogger?.Log("A shader database has failed to load");
                    failed = true;
                    break;
                }
            }

            // mirror the original Load() behavior: only mark as "done" if it didn't fail
            firstTimeLoad = failed;
        }

        /// <summary>
        /// Looks up the ordered Constant Textures list for a shader graph by name,
        /// from the static cache. Call EnsureLoaded() first if unsure it's populated.
        /// </summary>
        public static bool TryGetTextureConstants(string shaderGraphName, out List<CString> textures)
        {
            textures = null;
            uint hash = (uint)Utils.HashString(shaderGraphName, true);

            if (!shaderMap.ContainsKey(hash))
                return false;

            List<RenderPath> paths = shaderMap[hash];
            if (paths == null)
                return false;

            // permutations of the same graph share their constant textures, so the
            // first one carrying any is as good as the one the viewport will use
            foreach (RenderPath path in paths)
            {
                foreach (PermutationPair pair in path.PermutationPairs)
                {
                    if (pair.PixelShader.TextureConstants.Count == 0)
                        continue;

                    textures = pair.PixelShader.TextureConstants;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Loads ShaderDb data into memory on the initial load. Subsequent loads pull from the built cache
        /// </summary>
        /// <param name="targetData">The data to be displayed in the property grid</param>
        /// <param name="task"></param>
        public void Load(ShaderGraphData targetData, FrostyTaskWindow task)
        {
            targetData.Guid = ((EbxAssetEntry)assetEntry).Guid;
            targetData.Name = assetEntry.Name;

            // on the first load, build a dictionary with all the necessary shader info
            // also load the texture hash cache for the games that need it
            if (firstTimeLoad)
            {
                if (UseCache && !textureCacheLoaded)
                {
                    string cachePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "ShaderDataPlugin");
                    cachePath = Path.Combine(cachePath, ((ProfileVersion)ProfilesLibrary.DataVersion).ToString());
                    cachePath = Path.Combine(cachePath, "textures.cache");

                    using (NativeReader cache = new NativeReader(File.Open(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read)))
                    {
                        task.Update("Loading texture hash map");
                        int numTextures = cache.ReadInt();
                        for (int i = 0; i < numTextures; ++i)
                        {
                            task.Update(progress: (i / (double)numTextures) * 100.0);
                            uint hash = cache.ReadUInt();
                            Guid guid = cache.ReadGuid();

                            EbxAssetEntry asset = App.AssetManager.GetEbxEntry(guid);
                            textureHashMap.Add(hash, asset.Name);
                        }
                    }
                    textureCacheLoaded = true;
                }

                // Battlefront 2015 stores the per-surface texture references
                // in streamableTextures rather than in the hashed constant
                // texture table. Build the same hash map for that database.
                if (Version == (int)ShaderDBVersion.StarWarsBattlefront1
                    && textureHashMap.Count == 0)
                {
                    task.Update("Loading Battlefront texture hash map");
                    foreach (EbxAssetEntry asset in
                        App.AssetManager.EnumerateEbx("TextureBaseAsset"))
                    {
                        uint hash = (uint)Utils.HashString(asset.Name, true);
                        if (!textureHashMap.ContainsKey(hash))
                            textureHashMap.Add(hash, asset.Name);
                    }
                }

                IEnumerable<ResAssetEntry> shaderDbs = App.AssetManager.EnumerateRes((uint)Utils.HashString("IShaderDatabase", true));
                int dbCount = shaderDbs.Count();
                {
                    string countMsg = $"ShaderDb.Load: EnumerateRes(\"IShaderDatabase\") found {dbCount} res entries";
                    logger.Log(countMsg);
                    DebugFileLog.Write(countMsg);
                }
                int progress = 0;
                bool failed = false;
                foreach (ResAssetEntry resEntry in shaderDbs)
                {
                    task.Update($"Loading database: {resEntry.Name}", (progress++ / (double)dbCount) * 100.0);
                    Read(resEntry, targetData);
                    if (!Loaded)
                    {
                        logger.Log($"A shader database has failed to load");
                        failed = true;
                        break;
                    }
                }
                firstTimeLoad = failed;
            }

            uint shaderHash = (uint)Utils.HashString(assetEntry.Name, true);
            if (shaderMap.ContainsKey(shaderHash) && !firstTimeLoad)
            {
                Loaded = true;
                targetData.RenderPaths = shaderMap[shaderHash];
            }
            else
            {
                Loaded = false;
                string notFoundMsg = $"Shader {assetEntry.Filename} not found in any database";
                logger.Log(notFoundMsg);
                DebugFileLog.Write(notFoundMsg);
            }
        }

        private void Read(ResAssetEntry db, ShaderGraphData targetData)
        {
            // DIAGNOSTIC: guaranteed first line, independent of everything -
            // if this never shows up in ShaderDataPlugin_debug.log, Read()
            // itself is never being called for this database/asset.
            DebugFileLog.Write($"ShaderDb.Read: entering for database {db?.Name}");

            // DIAGNOSTIC: everything below used to be able to throw before
            // reaching the try/catch further down (which only wraps the
            // shader-map-building step), so any exception during header or
            // permutation parsing died silently with nothing written to the
            // log panel. Wrapping the whole method guarantees we always see
            // *something*, even if it's just a raw exception dump.
            try
            {
                ReadInternal(db, targetData);
            }
            catch (Exception ex)
            {
                Loaded = false;
                string errMsg = $"ShaderDb.Read threw while parsing database: {db.Name}" +
                    $"\nLast known Version field value: {Version}" +
                    $"\nMessage ---\n{ex.Message}" +
                    $"\nStackTrace ---\n{ex.StackTrace}";
                logger?.Log(errMsg);
                DebugFileLog.Write(errMsg);
            }
        }

        private void ReadInternal(ResAssetEntry db, ShaderGraphData targetData)
        {
            Stream resStream = App.AssetManager.GetRes(db);
            using (NativeReader reader = new NativeReader(resStream))
            {
                uint numRenderPaths;

                // Anthem uses the res meta as the database header
                // and this is just the beginning of many Anthem exclusive changes...
                if (ProfilesLibrary.DataVersion == (int)ProfileVersion.Anthem)
                {
                    using (NativeReader metaReader = new NativeReader(new MemoryStream(db.ResMeta)))
                    {
                        numRenderPaths = metaReader.ReadUInt();
                        metaReader.ReadUInt();
                        dbSize = metaReader.ReadUInt();
                        Version = metaReader.ReadUInt();
                    }
                    // DIAGNOSTIC
                    string anthemMsg = $"ShaderDb: database {db.Name} reports Version={Version} (Anthem-style header), numRenderPaths={numRenderPaths}";
                    logger?.Log(anthemMsg);
                    DebugFileLog.Write(anthemMsg);
                }
                else
                {
                    numRenderPaths = reader.ReadUInt();
                }

                for (int pathIt = 0; pathIt < numRenderPaths; ++pathIt)
                {
                    // get render path header data
                    uint renderPath = reader.ReadUInt();
                    if (ProfilesLibrary.DataVersion == (int)ProfileVersion.Anthem)
                    {
                        // quality level
                        reader.ReadUInt();
                    }
                    else
                    {
                        dbSize = reader.ReadUInt();
                        Version = reader.ReadUInt();
                        // DIAGNOSTIC: log the very first render path's raw version
                        // unconditionally, before the supported-version switch below
                        // can bail out or anything downstream can throw.
                        if (pathIt == 0)
                        {
                            string versionMsg = $"ShaderDb: database {db.Name} reports Version={Version}, dbSize={dbSize}, numRenderPaths={numRenderPaths}";
                            logger?.Log(versionMsg);
                            DebugFileLog.Write(versionMsg);
                        }
                        // path
                        reader.ReadUInt();
                        // quality level
                        reader.ReadUInt();
                        // BFV unknown byte
                        if (Version == (int)ShaderDBVersion.BattlefieldV)
                            reader.ReadByte();
                        // Unbound unknown uint
                        if (Version == (int)ShaderDBVersion.NFSUnbound)
                            reader.ReadUInt(); // seems to always be 1
                    }

                    switch ((ShaderDBVersion)Version)
                    {
                        case ShaderDBVersion.Battlefield4:
                        case ShaderDBVersion.DragonAgeInquisition:
                        case ShaderDBVersion.PvZGardenWarfare1:
                        case ShaderDBVersion.NFSRivals:
                        case ShaderDBVersion.NFS2015_PvZGardenWarfare2:
                        case ShaderDBVersion.StarWarsBattlefront1:
                        case ShaderDBVersion.StarWarsBattlefront2Alpha:
                        case ShaderDBVersion.NFSPayback_MECatalyst:
                        case ShaderDBVersion.MassEffectAndromeda:
                        case ShaderDBVersion.Battlefield1:
                        case ShaderDBVersion.BattlefieldV:
                        case ShaderDBVersion.Anthem:
                        case ShaderDBVersion.PvZBattleForNeighborville:
                        case ShaderDBVersion.NFSHeat:
                        case ShaderDBVersion.NFSUnbound:
                            break;
                        default:
                            Loaded = false;
                            {
                                string unsupportedMsg = $"Shader database version {Version} is not supported at this time";
                                logger.Log(unsupportedMsg);
                                DebugFileLog.Write(unsupportedMsg);
                            }
                            return;
                    }

                    // shader constants size
                    if (Version == (int)ShaderDBVersion.Anthem || Version == (int)ShaderDBVersion.NFSUnbound)
                        reader.ReadUInt();
                    // get constants for this database
                    uint shaderConstantsCount = reader.ReadUInt();
                    List<GenericShaderConstants> shaderConstants = new List<GenericShaderConstants>();
                    for (int i = 0; i < shaderConstantsCount; ++i)
                        shaderConstants.Add(new GenericShaderConstants(reader));

                    // constant function blocks size
                    if (Version == (int)ShaderDBVersion.Anthem || Version == (int)ShaderDBVersion.NFSUnbound)
                        reader.ReadUInt();
                    uint constantFunctionBlocksCount = reader.ReadUInt();
                    List<ConstantFunctionBlock> constantFunctionBlocks = new List<ConstantFunctionBlock>();
                    // get constant function blocks
                    for (int i = 0; i < constantFunctionBlocksCount; ++i)
                        constantFunctionBlocks.Add(new ConstantFunctionBlock(reader));

                    // texture function blocks size
                    if (Version == (int)ShaderDBVersion.Anthem || Version == (int)ShaderDBVersion.NFSUnbound)
                        reader.ReadUInt();
                    uint textureFunctionBlocksCount = reader.ReadUInt();
                    List<TextureFunctionBlock> textureFunctionBlocks = new List<TextureFunctionBlock>();
                    // get texture function blocks
                    for (int i = 0; i < textureFunctionBlocksCount; ++i)
                        textureFunctionBlocks.Add(new TextureFunctionBlock(reader));

                    List<BufferFunctionBlock> bufferFunctionBlocks = new List<BufferFunctionBlock>();
                    // FB2013 games (BF4, PvZ GW1, NFS Rivals, etc.) don't have buffer function blocks
                    if (Version > (int)ShaderDBVersion.NFSRivals)
                    {
                        // buffer function blocks size
                        if (Version == (int)ShaderDBVersion.Anthem || Version == (int)ShaderDBVersion.NFSUnbound)
                            reader.ReadUInt();
                        uint bufferFunctionBlocksCount = reader.ReadUInt();
                        // get buffer function blocks
                        for (int i = 0; i < bufferFunctionBlocksCount; ++i)
                            bufferFunctionBlocks.Add(new BufferFunctionBlock(reader));
                    }


                    //
                    // read all shader permutations
                    // Anthem has an extra array of structs after each permutation array
                    //

                    // get vertex shader permutations
                    uint vertexShaderPermutationCount = reader.ReadUInt();
                    List<ShaderPermutation> vsPermutations = new List<ShaderPermutation>();
                    for (int i = 0; i < vertexShaderPermutationCount; ++i)
                        vsPermutations.Add(new VertexShaderPermutation(reader, db.Name));

                    if (Version == (int)ShaderDBVersion.Anthem)
                    {
                        uint count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            new AnthemUnkStruct(reader);
                    }

                    // get pixel shader permutations
                    uint pixelShaderPermutationCount = reader.ReadUInt();
                    List<ShaderPermutation> psPermutations = new List<ShaderPermutation>();
                    for (int i = 0; i < pixelShaderPermutationCount; ++i)
                        psPermutations.Add(new PixelShaderPermutation(reader, db.Name));

                    if (Version == (int)ShaderDBVersion.Anthem)
                    {
                        uint count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            new AnthemUnkStruct(reader);
                    }

                    // read geometry shader permutations
                    uint geometryShaderPermutationCount = reader.ReadUInt();
                    for (int i = 0; i < geometryShaderPermutationCount; ++i)
                        new GeometryShaderPermutation(reader);

                    if (Version == (int)ShaderDBVersion.Anthem)
                    {
                        uint count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            new AnthemUnkStruct(reader);
                    }

                    // read hull shader permutations
                    uint hullShaderPermutationCount = reader.ReadUInt();
                    for (int i = 0; i < hullShaderPermutationCount; ++i)
                        new HullShaderPermutation(reader, db.Name);

                    if (Version == (int)ShaderDBVersion.Anthem)
                    {
                        uint count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            new AnthemUnkStruct(reader);
                    }

                    // get domain shader permutations
                    uint domainShaderPermutationCount = reader.ReadUInt();
                    for (int i = 0; i < domainShaderPermutationCount; ++i)
                        new DomainShaderPermutation(reader, db.Name);

                    if (Version == (int)ShaderDBVersion.Anthem)
                    {
                        uint count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            new AnthemUnkStruct(reader);

                        count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            new AnthemUnkStruct1(reader);
                    }

                    // BFV has 3 new arrays of shader permutations, not sure what they're for
                    if (Version == (int)ShaderDBVersion.BattlefieldV)
                    {
                        uint count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            new BFVUnkShaderPermutation(reader, db.Name);

                        // these next 2 permutation arrays contain DXIL data
                        count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            new BFVUnkShaderPermutation1(reader, db.Name);

                        count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            new BFVUnkShaderPermutation1(reader, db.Name);
                    }

                    // Unbound unknown 12 bytes
                    if (Version == (int)ShaderDBVersion.NFSUnbound)
                    {
                        reader.Position += 12;
                    }

                    // get shader solutions
                    uint solutionCount = reader.ReadUInt();
                    List<ShaderSolution> solutions = new List<ShaderSolution>();
                    for (int i = 0; i < solutionCount; ++i)
                        solutions.Add(new ShaderSolution(reader));

                    // get all geometry declaration hashes from the shader solution states
                    // this is the only data needed from them so that the geometry declarations can be looked up later
                    uint stateCount = reader.ReadUInt();
                    List<uint> geomDeclLookupList = new List<uint>();
                    for (int i = 0; i < stateCount; ++i)
                    {
                        // surfaceShaderNameHash
                        reader.ReadUInt();
                        // vertexShaderFragmentNameHash
                        reader.ReadUInt();
                        // tessellationShaderFragmentNameHash
                        reader.ReadUInt();

                        // geometryDeclarationHash
                        // since newer games don't look up vertex elements with this hash, we can skip adding them to the list
                        if (Version >= (int)ShaderDBVersion.Anthem)
                            reader.ReadUInt();
                        else
                            geomDeclLookupList.Add(reader.ReadUInt());

                        // renderStateFlags
                        reader.ReadUInt();
                        // unknown data
                        reader.ReadBytes(12);
                    }

                    uint geometryDeclarationsCount = reader.ReadUInt();
                    Dictionary<uint, List<VertexElementBase>> geomDecls = new Dictionary<uint, List<VertexElementBase>>();
                    // get geometry declarations
                    for (int i = 0; i < geometryDeclarationsCount; ++i)
                    {
                        List<VertexElementBase> elements = new List<VertexElementBase>();

                        if (Version == (int)ShaderDBVersion.StarWarsSquadrons || Version < (int)ShaderDBVersion.Anthem)
                        {
                            uint elementsHash = reader.ReadUInt();

                            // skip over the elements and streams and store the offset before both so we can grab their counts and return to them later
                            long elementsOffset = reader.Position;
                            reader.ReadBytes(64); // struct is 4 bytes, and the array has 16 elements
                            long streamsOffset = reader.Position;
                            reader.ReadBytes(2 * numStreams); // 2 byte struct

                            byte elementCount = reader.ReadByte();
                            byte streamCount = reader.ReadByte();
                            // unknown
                            reader.ReadUShort();
                            long endOffset = reader.Position;

                            // read streams first so we can look them up in a list when reading the elements
                            reader.Position = streamsOffset;
                            List<VertexElement_Old.VertexStream> streams = new List<VertexElement_Old.VertexStream>();
                            // streamCount is unreliable because for some reason it doesn't always give the true number of streams
                            // so instead we can fall back to NumStreams and discard any null streams (when stride and classification are zero)
                            for (int streamIt = 0; streamIt < numStreams; ++streamIt)
                            {
                                VertexElement_Old.VertexStream stream = new VertexElement_Old.VertexStream();
                                stream.stride = reader.ReadByte();
                                stream.classification = reader.ReadByte();
                                // the vertex stride can never be zero or else the vertex stream wouldn't be able to advance, so we can use this to discard null streams
                                if (stream.stride != 0)
                                    streams.Add(stream);
                            }

                            reader.Position = elementsOffset;
                            for (int elemIt = 0; elemIt < elementCount; ++elemIt)
                            {
                                VertexElement_Old element = new VertexElement_Old();
                                element.usage = reader.ReadByte();
                                element.format = reader.ReadByte();
                                element.offset = reader.ReadByte();
                                element.streamIndex = reader.ReadByte();
                                element.Stream = streams[element.streamIndex];
                                elements.Add(element);
                            }

                            // return to the end position
                            reader.Position = endOffset;
                            geomDecls.Add(elementsHash, elements);
                        }
                        else
                        {
                            // newer games don't have a separate streams array and the vertex element struct is slightly different
                            // there also isn't an element hash so the vertex element lookup is handled differently

                            uint elementCount = reader.ReadUInt();
                            for (int elemIt = 0; elemIt < elementCount; ++elemIt)
                            {
                                VertexElement_New element = new VertexElement_New();
                                element.format = reader.ReadByte();
                                element.classification = reader.ReadByte();
                                element.offset = reader.ReadByte();
                                element.streamIndex = reader.ReadByte();
                                elements.Add(element);
                            }

                            // skip the rest of the elements since they're empty and don't need to be stored
                            reader.ReadBytes((int)(64 - (elementCount * 4)));
                            // just do it like this so we don't have to use a separate list
                            geomDecls.Add((uint)i, elements);
                        }

                    }

                    // database versions >229 all have a list of indices that map to the geometry declaration that a shader uses
                    if (Version > (int)ShaderDBVersion.BattlefieldV)
                    {
                        uint count = reader.ReadUInt();
                        for (int i = 0; i < count; ++i)
                            geomDeclLookupList.Add(reader.ReadUInt());
                    }

                    if (Version == (int)ShaderDBVersion.NFSUnbound)
                    {
                        // unknown data
                        reader.ReadUInt();
                    }

                    uint shaderCount = reader.ReadUInt();
                    Dictionary<uint, List<uint>> surfaceShaderSolutionMap = new Dictionary<uint, List<uint>>();
                    // get shader map
                    for (int i = 0; i < shaderCount; ++i)
                    {
                        uint key = reader.ReadUInt();

                        // shaderType, maps to SurfaceShaderType
                        if (Version != (int)ShaderDBVersion.MassEffectAndromeda && Version != (int)ShaderDBVersion.BattlefieldV)
                            reader.ReadUInt();
                        else
                            reader.ReadByte();
                        // unknown data
                        switch ((ShaderDBVersion)Version)
                        {
                            case ShaderDBVersion.Battlefield4:
                            case ShaderDBVersion.DragonAgeInquisition:
                            case ShaderDBVersion.PvZGardenWarfare1:
                            case ShaderDBVersion.NFSRivals:
                            case ShaderDBVersion.NFS2015_PvZGardenWarfare2:
                            case ShaderDBVersion.StarWarsBattlefront1:
                            case ShaderDBVersion.NFSPayback_MECatalyst:
                            case ShaderDBVersion.Battlefield1:
                                // Certain games have 4 more bytes in whatever this data is
                                if (ProfilesLibrary.DataVersion == (int)ProfileVersion.NeedForSpeed || Version == (int)ShaderDBVersion.StarWarsBattlefront1)
                                    reader.ReadBytes(40);
                                else
                                    reader.ReadBytes(36);
                                break;
                            case ShaderDBVersion.BattlefieldV:
                                reader.ReadBytes(39);
                                break;
                            case ShaderDBVersion.StarWarsBattlefront2Alpha:
                            case ShaderDBVersion.MassEffectAndromeda:
                            case ShaderDBVersion.Anthem:
                            case ShaderDBVersion.StarWarsSquadrons:
                            case ShaderDBVersion.PvZBattleForNeighborville:
                            case ShaderDBVersion.NFSHeat:
                                reader.ReadBytes(104);
                                break;
                            case ShaderDBVersion.NFSUnbound:
                                reader.ReadBytes(284);
                                break;
                            default:
                                break;
                        }


                        int count = reader.ReadInt();
                        List<ShaderStreamableTexture> streamableTextures =
                            new List<ShaderStreamableTexture>();
                        // streamableTextures
                        switch ((ShaderDBVersion)Version)
                        {
                            case ShaderDBVersion.DragonAgeInquisition:
                            case ShaderDBVersion.MassEffectAndromeda:
                                // - (4 bytes) nameHash, uint
                                // - (4 bytes) coordType, maps to ShaderTextureCoordType
                                // - (4 bytes) vertexUsage, maps to VertexElementUsage
                                // - (4 bytes) textureTilesPerCoord, float
                                // - (1 byte)  unknown
                                for (int textureIndex = 0; textureIndex < count; ++textureIndex)
                                    streamableTextures.Add(ReadStreamableTexture(reader, true, false, false));
                                break;
                            default:
                                // - (4 bytes) nameHash, uint
                                // - (4 bytes) coordType, maps to ShaderTextureCoordType
                                // - (4 bytes) vertexUsage, maps to VertexElementUsage
                                // - (4 bytes) textureTilesPerCoord, float
                                for (int textureIndex = 0; textureIndex < count; ++textureIndex)
                                    streamableTextures.Add(ReadStreamableTexture(reader, false, false, false));
                                break;
                        }

                        count = reader.ReadInt();
                        // streamableExternalTextures
                        switch ((ShaderDBVersion)Version)
                        {
                            case ShaderDBVersion.DragonAgeInquisition:
                            case ShaderDBVersion.MassEffectAndromeda:
                                // - (4 bytes) nameHash, uint
                                // - (4 bytes) textureHandle, uint
                                // - (4 bytes) coordType, maps to ShaderTextureCoordType
                                // - (4 bytes) vertexUsage, maps to VertexElementUsage
                                // - (4 bytes) textureTilesPerCoord, float
                                // - (1 byte)  unknown
                                for (int textureIndex = 0; textureIndex < count; ++textureIndex)
                                    streamableTextures.Add(ReadStreamableTexture(reader, true, true, false));
                                break;
                            case ShaderDBVersion.NFSUnbound:
                                // - (4 bytes) nameHash, uint
                                // - (4 bytes) textureHandle, uint
                                // - (4 bytes) unknown
                                // - (4 bytes) unknown
                                // - (4 bytes) coordType, maps to ShaderTextureCoordType
                                // - (4 bytes) vertexUsage, maps to VertexElementUsage
                                // - (4 bytes) textureTilesPerCoord, float
                                for (int textureIndex = 0; textureIndex < count; ++textureIndex)
                                    streamableTextures.Add(ReadStreamableTexture(reader, false, true, true));
                                break;
                            default:
                                // - (4 bytes) nameHash, uint
                                // - (4 bytes) textureHandle, uint
                                // - (4 bytes) coordType, maps to ShaderTextureCoordType
                                // - (4 bytes) vertexUsage, maps to VertexElementUsage
                                // - (4 bytes) textureTilesPerCoord, float
                                for (int textureIndex = 0; textureIndex < count; ++textureIndex)
                                    streamableTextures.Add(ReadStreamableTexture(reader, false, true, false));
                                break;
                        }

                        streamableTextureMap[key] = streamableTextures;

                        // unknown data, seems to always be zero
                        if (Version == (int)ShaderDBVersion.NFSUnbound)
                            reader.ReadUInt();

                        count = reader.ReadInt();
                        List<uint> solutionIndices = new List<uint>();
                        for (int j = 0; j < count; ++j)
                        {
                            // FB2013 games store these indices as uint16 values
                            if (Version <= (int)ShaderDBVersion.NFSRivals)
                                solutionIndices.Add(reader.ReadUShort());
                            else
                            {
                                try
                                {
                                    solutionIndices.Add(reader.ReadUInt());
                                }
                                catch
                                {
                                    throw new Exception($"Encountered error in database: {db.Name}");
                                }
                            }
                        }
                        surfaceShaderSolutionMap.Add(key, solutionIndices);
                    }

                    try
                    {
                        foreach (uint key in surfaceShaderSolutionMap.Keys)
                        {
                            List<uint> indices = surfaceShaderSolutionMap[key];
                            List<ShaderStreamableTexture> streamableTextures =
                                streamableTextureMap.ContainsKey(key)
                                    ? streamableTextureMap[key]
                                    : new List<ShaderStreamableTexture>();

                            if (!shaderMap.ContainsKey(key))
                            {
                                List<RenderPath> paths = new List<RenderPath>();
                                shaderMap.Add(key, paths);
                            }
                            string pathName = TypeLibrary.GetType("ShaderRenderPath").GetEnumName(renderPath);


                            List<PermutationPair> permutationPairs;
                            try
                            {
                                permutationPairs = shaderMap[key].Find(x => x.RenderPathName == pathName).PermutationPairs;
                            }
                            catch
                            {
                                RenderPath path = new RenderPath() { RenderPathName = pathName };
                                shaderMap[key].Add(path);
                                permutationPairs = path.PermutationPairs;
                            }

                            // The same render path can be encountered through
                            // multiple surface-shader solution groups. Seed
                            // this set from the entries already collected for
                            // the path so duplicates across those groups are
                            // removed as well.
                            HashSet<string> displayedPermutationKeys =
                                new HashSet<string>(
                                    permutationPairs
                                        .Where(x => HasDisplayedPixelShaderData(x.ps))
                                        .Select(x => GetDisplayedPixelShaderKey(x.ps)),
                                    StringComparer.Ordinal);

                            // fill in shader data
                            for (int i = 0; i < indices.Count; ++i)
                            {
                                int solutionIndex = (int)indices[i];
                                PermutationPair pair = new PermutationPair();
                                pair.ps = new ShaderGraphPermutation();
                                pair.vs = new ShaderGraphPermutation();
                                List<VertexElementBase> elems;
                                if (Version > (int)ShaderDBVersion.BattlefieldV)
                                    elems = geomDecls[geomDeclLookupList[(int)solutions[solutionIndex].vertexPermutationIndex]];
                                else
                                    elems = geomDecls[geomDeclLookupList[solutionIndex]];

                                pair.PixelShader.VertexElements = elems;
                                int pixelPermutationIdx = (int)solutions[solutionIndex].pixelPermutationIndex;
                                if (pixelPermutationIdx > 0)
                                {
                                    pair.ps.shaderDataLookup = psPermutations[pixelPermutationIdx];
                                    GetShaderPermutation(shaderConstants[(int)solutions[solutionIndex].pixelConstantsIndex],
                                        constantFunctionBlocks.Count > 0 ? constantFunctionBlocks[(int)pair.ps.shaderDataLookup.ConstantFunctionBlocksIndex] : null,
                                        textureFunctionBlocks.Count > 0 ? textureFunctionBlocks[(int)pair.ps.shaderDataLookup.TextureFunctionBlocksIndex] : null,
                                        bufferFunctionBlocks.Count > 0 ? bufferFunctionBlocks[(int)pair.ps.shaderDataLookup.BufferFunctionBlocksIndex] : null,
                                        ref pair.ps);
                                    AddStreamableTextures(pair.ps, streamableTextures, assetEntry != null ? assetEntry.Name : null);
                                }

                                pair.VertexShader.VertexElements = elems;
                                pair.vs.shaderDataLookup = vsPermutations[(int)solutions[solutionIndex].vertexPermutationIndex];
                                GetShaderPermutation(shaderConstants[(int)solutions[solutionIndex].vertexConstantsIndex],
                                    constantFunctionBlocks.Count > 0 ? constantFunctionBlocks[(int)pair.vs.shaderDataLookup.ConstantFunctionBlocksIndex] : null,
                                    textureFunctionBlocks.Count > 0 ? textureFunctionBlocks[(int)pair.vs.shaderDataLookup.TextureFunctionBlocksIndex] : null,
                                    bufferFunctionBlocks.Count > 0 ? bufferFunctionBlocks[(int)pair.vs.shaderDataLookup.BufferFunctionBlocksIndex] : null,
                                    ref pair.vs);

                                // Do not expose empty permutation records in the
                                // property grid. They contain no pixel shader
                                // textures or value parameters and are not useful
                                // when inspecting the selected shader.
                                if (HasDisplayedPixelShaderData(pair.ps)
                                    && displayedPermutationKeys.Add(GetDisplayedPixelShaderKey(pair.ps)))
                                    permutationPairs.Add(pair);
                            }

                            // Avoid leaving empty render-path groups in the
                            // property grid after empty permutations are removed.
                            if (permutationPairs.Count == 0)
                                shaderMap[key].RemoveAll(x => x.RenderPathName == pathName);
                        }

                        Loaded = true;
                    }
                    catch (Exception ex)
                    {
                        Loaded = false;
                        logger.Log($"Encountered error building shader map in database: {db.Name}" +
                            $"\nMessage ---\n{ex.Message}" +
                            $"\nStackTrace ---\n{ex.StackTrace}" +
                            $"\nTargetSite ---\n{ex.TargetSite}");
                    }
                }
            }
        }

        private void GetShaderPermutation(GenericShaderConstants constants,
            ConstantFunctionBlock constantFunctionBlock,
            TextureFunctionBlock textureFunctionBlock,
            BufferFunctionBlock bufferFunctionBlock,
            ref ShaderGraphPermutation permutation)
        {
            if (constantFunctionBlock != null)
            {
                foreach (ConstantFunctionBlock.Constant cfunc in constantFunctionBlock.Constants)
                {
                    permutation.ConstantFunctions.Add
                    (
                        new ConstantFunction
                        {
                            funcType = cfunc.constFunction,
                            CBufferIndex = cfunc.index,
                            ArraySize = cfunc.arraySize,
                            MatrixDims = cfunc.matrixDims
                        }
                    );
                }
            }

            if (textureFunctionBlock != null)
            {
                foreach (TextureFunctionBlock.Texture tfunc in textureFunctionBlock.Textures)
                {
                    permutation.TextureFunctions.Add
                    (
                        new TextureFunction
                        {
                            funcType = tfunc.constFunction,
                            texType = tfunc.valueType,
                            Index = tfunc.index
                        }
                    );
                }
            }

            if (bufferFunctionBlock != null)
            {
                foreach (BufferFunctionBlock.Buffer bfunc in bufferFunctionBlock.Buffers)
                {
                    permutation.BufferFunctions.Add
                    (
                        new BufferFunction
                        {
                            funcType = bfunc.constFunction,
                            bufType = bfunc.valueType,
                            Index = bfunc.index
                        }
                    );
                }
            }

            permutation.ValueConstants = constants.valueConstants;
            foreach (TextureConstant tex in constants.textureConstants)
            {
                string textureName = tex.name;
                string referenceType = "Embedded texture";
                string hashText = tex.nameHash == 0 ? "" : $"0x{tex.nameHash:X8}";

                // SWBF 2015 and other pre-hash databases store the actual
                // texture reference in the fixed-width name field. Newer
                // databases store a name hash instead.
                if (string.IsNullOrWhiteSpace(textureName))
                {
                    referenceType = "Hashed texture";
                    if (!textureHashMap.ContainsKey(tex.nameHash))
                    {
                        // This can be a debug/streamable reference that is
                        // not present as a TextureBaseAsset in the current
                        // Frosty profile.
                        textureName = $"Unknown texture: {hashText}";
                    }
                    else
                    {
                        textureName = textureHashMap[tex.nameHash];
                    }
                }

                permutation.TextureConstants.Add(textureName);
                permutation.TextureBindings.Add(new TextureBinding
                {
                    Slot = tex.index,
                    Texture = textureName,
                    ReferenceType = referenceType,
                    Hash = hashText
                });
            }
            foreach (ExternalTextureConstant tex in constants.externalTextureConstants)
            {
                permutation.ExternalTextureConstants.Add(tex.name);
                permutation.TextureBindings.Add(new TextureBinding
                {
                    Slot = tex.index,
                    Texture = tex.name,
                    ReferenceType = "External texture parameter",
                    TextureType = GetShaderValueTypeName(tex.textureType),
                    Hash = tex.handle == 0 ? "" : $"0x{tex.handle:X8}"
                });
            }
            foreach (ExternalValueConstant val in constants.externalValueConstants)
            {
                if (!IsUsefulValueParameter(val.name))
                    continue;

                Type extType = TypeLibrary.GetType("ExternalValueConstantType");
                permutation.ExternalValueConstants.Add
                (
                    new ExternalValue
                    {
                        Name = val.name,
                        Type = extType == null ? "ExternalValueConstantType_Vec" : extType.GetEnumName(val.type),
                        Required = Convert.ToBoolean(val.required),
                        DefaultValue = val.defaultValue
                    }
                );
            }
            foreach (ExternalBufferConstant buf in constants.externalBufferConstants)
            {
                Type extType = TypeLibrary.GetType("ShaderValueType");
                permutation.ExternalBufferConstants.Add
                (
                    new ExternalBuffer
                    {
                        Name = buf.name,
                        Required = Convert.ToBoolean(buf.required),
                        Type = extType.GetEnumName(buf.valueType)
                    }
                );
            }
            permutation.SamplerStates = constants.samplerStates;

            // Attach the shader-side function/type information to the
            // matching texture slots. Keeping this separate from the
            // original arrays preserves compatibility with the existing
            // property-grid layout while making detail-map usage visible.
            if (textureFunctionBlock != null)
            {
                foreach (TextureFunctionBlock.Texture tfunc in textureFunctionBlock.Textures)
                {
                    TextureBinding binding = permutation.TextureBindings
                        .FirstOrDefault(x => x.Slot == tfunc.index);
                    if (binding == null)
                    {
                        binding = new TextureBinding
                        {
                            Slot = tfunc.index,
                            ReferenceType = "Shader texture slot"
                        };
                        permutation.TextureBindings.Add(binding);
                    }

                    binding.ShaderFunction = GetShaderFunctionName(tfunc.constFunction);
                    binding.TextureType = GetShaderValueTypeName(tfunc.valueType);
                }
            }
        }

        private static ShaderStreamableTexture ReadStreamableTexture(
            NativeReader reader,
            bool hasTrailingByte,
            bool hasTextureHandle,
            bool hasUnboundPadding)
        {
            ShaderStreamableTexture texture = new ShaderStreamableTexture
            {
                NameHash = reader.ReadUInt()
            };

            if (hasTextureHandle)
                texture.TextureHandle = reader.ReadUInt();

            if (hasUnboundPadding)
                reader.ReadBytes(8);

            texture.CoordinateType = reader.ReadUInt();
            texture.VertexUsage = reader.ReadUInt();
            texture.TilesPerCoordinate = reader.ReadFloat();

            if (hasTrailingByte)
                reader.ReadByte();

            return texture;
        }

        private static void AddStreamableTextures(
            ShaderGraphPermutation permutation,
            List<ShaderStreamableTexture> streamableTextures,
            string shaderName)
        {
            if (Version != (int)ShaderDBVersion.StarWarsBattlefront1)
                return;

            // The SWBF1 streamable-texture list is captured once per
            // surface-shader-solution *group*, not per permutation, so the
            // same raw list gets offered to every permutation under that
            // group. Some entries genuinely belong to whatever mesh/material
            // is using this shader (e.g. a shared detail layer); others are
            // just other, unrelated meshes that happen to share the group
            // (e.g. a vehicle hull texture). Trusting every resolved hash
            // equally is what causes unrelated textures to leak in and
            // identical default spam to appear on every permutation.
            //
            // We now require BOTH:
            //  1) A mechanical signal: the streamable texture's vertex usage
            //     channel is actually consumed by this permutation's own
            //     vertex format.
            //  2) A relevance signal: the texture's asset path shares a
            //     meaningful name token with the shader, or lives in a
            //     shared/common-texture location designed to be reused
            //     across unrelated meshes (e.g. _CommonTextures).
            HashSet<uint> vertexUsages = GetPermutationVertexUsages(permutation);
            bool addedRelevantTexture = false;

            foreach (ShaderStreamableTexture streamable in streamableTextures)
            {
                if (streamable.NameHash == 0)
                    continue;

                // Some streamable records contain runtime or non-EBX hashes.
                // Do not expose those raw hashes as fake texture names.
                if (!textureHashMap.ContainsKey(streamable.NameHash))
                    continue;

                string textureName = textureHashMap[streamable.NameHash];

                bool usageMatches = vertexUsages.Count == 0
                    // No usage info could be recovered for this permutation's
                    // vertex format (newer VertexElement layout); fall back
                    // to trusting the relevance check alone.
                    || vertexUsages.Contains(streamable.VertexUsage);

                if (!usageMatches)
                    continue;

                if (!IsStreamableTextureRelevant(textureName, shaderName))
                    continue;

                addedRelevantTexture = true;

                bool alreadyPresent = permutation.TextureConstants
                    .Any(x => x.ToString().Equals(
                        textureName,
                        StringComparison.OrdinalIgnoreCase));

                if (alreadyPresent)
                    continue;

                permutation.TextureConstants.Add(textureName);
                permutation.TextureBindings.Add(new TextureBinding
                {
                    Slot = uint.MaxValue,
                    Texture = textureName,
                    ReferenceType = "Battlefront streamable texture",
                    Hash = $"0x{streamable.NameHash:X8}"
                });
            }

            // Battlefront shader constants often include generic fallback
            // assets such as shaders/T_Default_*. Only hide those fallback
            // names when we actually found a *relevant* resolved texture to
            // replace them with -- otherwise the default is the correct,
            // accurate answer (the real value is only known by the Material
            // asset, which ShaderDb has no visibility into) and hiding it
            // would just leave the permutation looking empty.
            if (addedRelevantTexture)
            {
                permutation.TextureConstants.RemoveAll(
                    x => IsGenericDefaultTexture(x.ToString()));
            }
        }

        /// <summary>
        /// Recovers the set of VertexElementUsage values actually wired up
        /// by this permutation's vertex format. Only the older, explicit
        /// per-element usage layout carries this information; newer formats
        /// derive usage elsewhere and are reported as an empty set (callers
        /// should treat an empty set as "unknown" rather than "no usages").
        /// </summary>
        private static HashSet<uint> GetPermutationVertexUsages(ShaderGraphPermutation permutation)
        {
            HashSet<uint> usages = new HashSet<uint>();
            if (permutation.VertexElements == null)
                return usages;

            foreach (VertexElementBase elem in permutation.VertexElements)
            {
                if (elem is VertexElement_Old oldElem)
                    usages.Add(oldElem.usage);
            }

            return usages;
        }

        // Locations known to hold small, intentionally shared utility layers
        // (detail/noise/mask textures reused across many unrelated meshes).
        // A hit here is treated as relevant even without a name-token match.
        private static readonly string[] SharedUtilityTexturePaths =
        {
            "_commontextures",
            "/shaders/",
            "\\shaders\\",
            "_shared",
        };

        /// <summary>
        /// Decides whether a resolved streamable texture is plausibly tied
        /// to the given shader, rather than just being another, unrelated
        /// mesh that happens to share the same surface-shader-solution
        /// group. Two independent checks: shared meaningful name tokens
        /// between the texture path and the shader name, or membership in a
        /// known shared/common-texture location.
        /// </summary>
        private static bool IsStreamableTextureRelevant(string texturePath, string shaderName)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
                return false;

            string lowerPath = texturePath.ToLowerInvariant();

            foreach (string shared in SharedUtilityTexturePaths)
            {
                if (lowerPath.Contains(shared))
                    return true;
            }

            if (string.IsNullOrWhiteSpace(shaderName))
                return false;

            HashSet<string> shaderTokens = TokenizeForRelevance(shaderName);
            HashSet<string> textureTokens = TokenizeForRelevance(texturePath);

            return shaderTokens.Overlaps(textureTokens);
        }

        // Tokens too generic to count as a meaningful relevance match on
        // their own (prefixes, channel/format suffixes, numbering, etc).
        private static readonly HashSet<string> GenericRelevanceTokens = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "t", "ss", "objects", "props", "shaders", "textures", "default",
            "c", "n", "s", "da", "ns", "cs", "rgba", "uv", "map", "preset",
            "01", "02", "03", "04", "1", "2", "3", "4"
        };

        private static HashSet<string> TokenizeForRelevance(string value)
        {
            HashSet<string> tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value))
                return tokens;

            string fileName = value.Replace('\\', '/');
            int lastSlash = fileName.LastIndexOf('/');
            if (lastSlash >= 0)
                fileName = fileName.Substring(lastSlash + 1);

            foreach (string rawToken in fileName.Split('_', '.', '-'))
            {
                string token = rawToken.Trim();
                if (token.Length < 3)
                    continue;
                if (GenericRelevanceTokens.Contains(token))
                    continue;

                tokens.Add(token);
            }

            return tokens;
        }

        private static bool IsGenericDefaultTexture(string textureName)
        {
            return textureName.IndexOf(
                       "/T_Default",
                       StringComparison.OrdinalIgnoreCase) >= 0
                || textureName.IndexOf(
                       "\\T_Default",
                       StringComparison.OrdinalIgnoreCase) >= 0
                || textureName.StartsWith(
                       "T_Default",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasDisplayedPixelShaderData(ShaderGraphPermutation permutation)
        {
            return permutation.TextureConstants.Count > 0
                || permutation.ExternalValueConstants.Count > 0
                || permutation.ExternalTextureConstants.Count > 0;
        }

        private static string GetDisplayedPixelShaderKey(ShaderGraphPermutation permutation)
        {
            StringBuilder key = new StringBuilder();

            // These are the visible texture inputs in the property grid.
            key.Append("textures:");
            foreach (CString texture in permutation.TextureConstants)
                key.Append(texture.ToString()).Append('\u001F');

            foreach (CString texture in permutation.ExternalTextureConstants)
                key.Append("external:").Append(texture.ToString()).Append('\u001F');

            // Include the visible value-parameter identity and value. This
            // keeps permutations with different tiling/detail/weather values.
            key.Append("|values:");
            foreach (ExternalValue value in permutation.ExternalValueConstants)
            {
                string name = value.Name.ToString();
                string type = value.Type.ToString();
                object defaultValueObject = value.DefaultValue;
                string defaultValue = defaultValueObject == null
                    ? ""
                    : Convert.ToString(defaultValueObject);

                key.Append(name)
                    .Append('\u001F')
                    .Append(type)
                    .Append('\u001F')
                    .Append(value.Required)
                    .Append('\u001F')
                    .Append(defaultValue)
                    .Append('\u001E');
            }

            return key.ToString();
        }

        private static string GetShaderFunctionName(uint value)
        {
            Type type = TypeLibrary.GetType("ShaderConstantFunction");
            string name = type?.GetEnumName(value);
            if (string.IsNullOrEmpty(name))
                return $"Unknown function ({value})";

            const string prefix = "ShaderConstantFunction_";
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name.Substring(prefix.Length);
            return name;
        }

        private static bool IsUsefulValueParameter(string name)
        {
            if (true) return !string.IsNullOrWhiteSpace(name);
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string value = name.ToLowerInvariant();
            return value.Contains("detail")
                || value.Contains("weather")
                || value.Contains("tiling")
                || value.Contains("scale")
                || value.Contains("offset")
                || value.Contains("normal")
                || value.Contains("basecolor")
                || value.Contains("diffuse")
                || value.Contains("roughness")
                || value.Contains("specular")
                || value.Contains("metal")
                || value.Contains("msw")
                || value.Contains("destruction");
        }

        private static string GetShaderValueTypeName(uint value)
        {
            Type type = TypeLibrary.GetType("ShaderValueType");
            string name = type?.GetEnumName(value);
            return string.IsNullOrEmpty(name) ? $"Unknown type ({value})" : name;
        }

    }
}