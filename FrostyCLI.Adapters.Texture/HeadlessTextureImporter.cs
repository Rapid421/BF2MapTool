using System;
using System.IO;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using Frosty.Core;
using Frosty.Core.Viewport;
using Frosty.Hash;
using D3D11 = SharpDX.Direct3D11;
using FbTexture = FrostySdk.Resources.Texture;

namespace FrostyCLI.Adapters.Texture
{
    /// <summary>
    /// Headless port of Frosty Editor's texture-replace logic
    /// (Plugins/TexturePlugin/FrostyTextureEditor.cs, import flow ~lines 284-513).
    /// Replaces an existing TextureAsset's pixels from a DDS/PNG/TGA/HDR file.
    ///
    /// Supports TT_2d, TT_Cube, TT_2dArray and TT_3d. NO mutation of the AssetManager
    /// occurs unless every validation passes first.
    /// </summary>
    public static class HeadlessTextureImporter
    {
        /// <summary>
        /// Replace the pixels of <paramref name="entry"/> from <paramref name="filePath"/>.
        /// Returns null on success, otherwise an error message.
        /// </summary>
        /// <param name="entry">The target TextureAsset EBX entry.</param>
        /// <param name="filePath">Source image file (DDS read directly; PNG/TGA/HDR converted via dxtex.dll).</param>
        /// <param name="formatStr">Format token: "DDS", "PNG", "TGA", or "HDR" (already validated/normalized by the caller).</param>
        /// <param name="generateMipmaps">When converting non-DDS input, whether to generate mipmaps.</param>
        /// <summary>
        /// Whether the texture asset stores sRGB data. Null when the asset or its resource cannot be
        /// read. The importer refuses to put non-sRGB pixels into an sRGB texture, so clone templates
        /// must be picked by this.
        /// </summary>
        public static bool? IsSrgb(EbxAssetEntry entry)
        {
            try
            {
                if (App.AssetManager == null || entry == null) return null;
                var asset = App.AssetManager.GetEbx(entry);
                ulong resRid = (ulong)((dynamic)asset.RootObject).Resource;
                if (resRid == 0) return null;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
                if (resEntry == null) return null;
                FbTexture tex = App.AssetManager.GetResAs<FbTexture>(resEntry);
                if (tex == null) return null;
                return tex.PixelFormat.Contains("SRGB") || ((tex.Flags & TextureFlags.SrgbGamma) != 0);
            }
            catch { return null; }
        }

        public static string Import(EbxAssetEntry entry, string filePath, string formatStr, bool generateMipmaps)
        {
            try
            {
                if (App.AssetManager == null)
                    return "AssetManager not initialized.";
                if (entry == null)
                    return "EBX entry is null.";

                // 1. Resolve EBX -> Resource RID (mirrors HeadlessTextureAssetDefinition.cs:18-25).
                var asset = App.AssetManager.GetEbx(entry);
                if (asset == null)
                    return "Failed to load EBX: " + entry.Name;
                dynamic texObj = asset.RootObject;
                ulong resRid;
                try { resRid = (ulong)texObj.Resource; }
                catch (Exception ex) { return "Could not read Resource RID: " + ex.Message; }
                if (resRid == 0)
                    return "Resource RID is 0 for " + entry.Name;

                // 2. Resolve Res + existing Texture + Chunk.
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
                if (resEntry == null)
                    return "ResAssetEntry not found for RID 0x" + resRid.ToString("X16");
                FbTexture old = App.AssetManager.GetResAs<FbTexture>(resEntry);
                if (old == null)
                    return "Failed to load Texture resource for " + entry.Name;
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(old.ChunkId);

                bool textureIsSRGB = old.PixelFormat.Contains("SRGB") || ((old.Flags & TextureFlags.SrgbGamma) != 0);

                // Parse the format token.
                ImageFormat fmt;
                switch ((formatStr ?? "").ToUpperInvariant())
                {
                    case "DDS": fmt = ImageFormat.DDS; break;
                    case "PNG": fmt = ImageFormat.PNG; break;
                    case "TGA": fmt = ImageFormat.TGA; break;
                    case "HDR": fmt = ImageFormat.HDR; break;
                    default: return "Unsupported format '" + formatStr + "'.";
                }

                // 3. Obtain DDS bytes -- either read directly, or convert from PNG/TGA/HDR via dxtex.dll.
                MemoryStream memStream;
                BlobData blob = new BlobData();
                bool blobAllocated = false;
                try
                {
                    if (fmt == ImageFormat.DDS)
                    {
                        // Straight DDS import -- no native dependency.
                        byte[] buf;
                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                            buf = NativeReader.ReadInStream(fs);
                        memStream = new MemoryStream(buf);
                    }
                    else
                    {
                        // Non-DDS conversion requires thirdparty/dxtex.dll. v1: TT_2d only,
                        // so a single image -> single DDS via ConvertImageToDDS.
                        if (!DxTexInterop.IsAvailable())
                            return "thirdparty/dxtex.dll not found -- DDS input still works; PNG/TGA/HDR require the native converter.";

                        var options = new TextureImportOptions
                        {
                            type = old.Type,
                            // ToShaderFormat returns a SharpDX.DXGI.Format; the headless options.format
                            // field is int (ABI-identical), so cast explicitly.
                            format = (int)TextureUtils.ToShaderFormat(old.PixelFormat, (old.Flags & TextureFlags.SrgbGamma) != 0),
                            generateMipmaps = generateMipmaps && old.MipCount > 1,
                            mipmapsFilter = 0,
                            resizeTexture = false,
                            resizeFilter = 0,
                            resizeHeight = 0,
                            resizeWidth = 0
                        };

                        byte[] buf;
                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                            buf = NativeReader.ReadInStream(fs);
                        DxTexInterop.ConvertImageToDDS(buf, buf.Length, fmt, options, ref blob);
                        blobAllocated = true;
                        memStream = new MemoryStream(blob.Data);
                    }

                    // ---- Parse + validate. No mutation happens in this block. ----
                    using (NativeReader reader = new NativeReader(memStream))
                    {
                        TextureUtils.DDSHeader header = new TextureUtils.DDSHeader();
                        if (!header.Read(reader))
                            return "Invalid DDS format";

                        // 5. Detect TextureType from header.
                        TextureType type = TextureType.TT_2d;
                        if (header.HasExtendedHeader)
                        {
                            if (header.ExtendedHeader.resourceDimension == D3D11.ResourceDimension.Texture2D)
                            {
                                if ((header.ExtendedHeader.miscFlag & 4) != 0)
                                    type = TextureType.TT_Cube;
                                else if (header.ExtendedHeader.arraySize > 1)
                                    type = TextureType.TT_2dArray;
                            }
                            else if (header.ExtendedHeader.resourceDimension == D3D11.ResourceDimension.Texture3D)
                                type = TextureType.TT_3d;
                        }
                        else
                        {
                            if ((header.dwCaps2 & TextureUtils.DDSCaps2.CubeMap) != 0)
                                type = TextureType.TT_Cube;
                            else if ((header.dwCaps2 & TextureUtils.DDSCaps2.Volume) != 0)
                                type = TextureType.TT_3d;
                        }

                        // TT_2d, TT_Cube, TT_2dArray and TT_3d are supported. Cube/2dArray use the
                        // slice-rejig below; TT_3d (volume) needs no rejig -- its DDS layout (depth
                        // slices contiguous per mip) already matches Frostbite's, so it is written
                        // as-is with depth = dwDepth, exactly as the Frosty editor does.
                        if (type != old.Type)
                            return "Imported texture must match original texture type. Original texture type is "
                                   + old.Type + ". Imported texture type is " + type;

                        // 6. Pixel format + base flags.
                        string pixelFormat;
                        TextureFlags baseFlags;
                        GetPixelFormat(old, header, out pixelFormat, out baseFlags);

                        // SRGB guard: don't allow changing SRGB to non-SRGB.
                        if (textureIsSRGB)
                        {
                            if (!header.HasExtendedHeader || !header.ExtendedHeader.dxgiFormat.ToString().ToLower().Contains("srgb"))
                                return "Format must be SRGB variant";
                        }

                        // 7. Compressed + mips requires width/height divisible by 4.
                        if (TextureUtils.IsCompressedFormat(pixelFormat) && old.MipCount > 1)
                        {
                            if (header.dwWidth % 4 != 0 || header.dwHeight % 4 != 0)
                                return "Texture width/height must be divisible by 4 for compressed formats requiring mip maps";
                        }

                        // ---- All validation passed. Mutation begins below. ----

                        // 8. Read remaining bytes as the pixel buffer.
                        byte[] buffer = new byte[reader.Length - reader.Position];
                        reader.Read(buffer, 0, (int)(reader.Length - reader.Position));

                        // Depth/slice count from the DDS header (1 for 2D; 6 for cube; arraySize for array).
                        ushort depth = (ushort)((!header.HasExtendedHeader ||
                            header.ExtendedHeader.resourceDimension != D3D11.ResourceDimension.Texture2D)
                            ? 1 : (ushort)header.ExtendedHeader.arraySize);
                        if ((header.dwCaps2 & TextureUtils.DDSCaps2.CubeMap) != 0) depth = 6;
                        if ((header.dwCaps2 & TextureUtils.DDSCaps2.Volume) != 0) depth = (ushort)header.dwDepth;

                        FbTexture newTex = new FbTexture(old.Type, pixelFormat, (ushort)header.dwWidth, (ushort)header.dwHeight, depth)
                        {
                            FirstMip = old.FirstMip
                        };
                        if (header.dwMipMapCount <= old.FirstMip)
                            newTex.FirstMip = 0;
                        // FROSTYCLI_TEX_FIRSTMIP=<n>: override the template's first streamed mip (0 = every mip in the
                        // bundle-resident range, nothing streamed) - Cato streaming-crash diagnosis.
                        string fmEnv = Environment.GetEnvironmentVariable("FROSTYCLI_TEX_FIRSTMIP");
                        if (!string.IsNullOrEmpty(fmEnv) && int.TryParse(fmEnv, out int fmOverride) && fmOverride >= 0 && fmOverride < header.dwMipMapCount)
                            newTex.FirstMip = (byte)fmOverride;

                        newTex.TextureGroup = old.TextureGroup;
                        newTex.CalculateMipData(
                            (byte)header.dwMipMapCount,
                            TextureUtils.GetFormatBlockSize(pixelFormat),
                            TextureUtils.IsCompressedFormat(pixelFormat),
                            (uint)buffer.Length);
                        newTex.Flags = baseFlags;

                        // Copy old flags (minus gamma) onto the new texture.
                        TextureFlags oldFlags = old.Flags & ~(TextureFlags.SrgbGamma);
                        newTex.Flags |= oldFlags;

                        // Cube/2dArray: reorder the DDS pixel data (slice-major: all mips of slice 0,
                        // then slice 1, ...) into the Frostbite layout (mip-major: all slices of mip 0,
                        // then mip 1, ...). Ported from FrostyTextureEditor's import.
                        if (newTex.Type == TextureType.TT_Cube || newTex.Type == TextureType.TT_2dArray)
                        {
                            int sliceCount = newTex.Type == TextureType.TT_2dArray ? newTex.Depth : 6;
                            var src = new MemoryStream(buffer);
                            var dst = new MemoryStream();
                            uint[] mipOffsets = new uint[newTex.MipCount];
                            for (int l = 0; l < newTex.MipCount - 1; l++)
                                mipOffsets[l + 1] = mipOffsets[l] + (uint)(int)(newTex.MipSizes[l] * sliceCount);
                            byte[] mipBuf = new byte[newTex.MipSizes[0]];
                            for (int s = 0; s < sliceCount; s++)
                                for (int mip = 0; mip < newTex.MipCount; mip++)
                                {
                                    int sz = (int)newTex.MipSizes[mip];
                                    src.Read(mipBuf, 0, sz);
                                    dst.Position = mipOffsets[mip] + sz * s;
                                    dst.Write(mipBuf, 0, sz);
                                }
                            buffer = dst.ToArray();
                        }

                        // 9. Chunk write.
                        if (ProfilesLibrary.MustAddChunks && chunkEntry != null && chunkEntry.Bundles.Count == 0 && !chunkEntry.IsAdded)
                        {
                            // DAI-style: add a new chunk.
                            old.ChunkId = App.AssetManager.AddChunk(buffer, null,
                                (newTex.Flags & TextureFlags.OnDemandLoaded) != 0 ? null : newTex);
                            chunkEntry = App.AssetManager.GetChunkEntry(old.ChunkId);
                        }
                        else
                        {
                            App.AssetManager.ModifyChunk(old.ChunkId, buffer,
                                ((newTex.Flags & TextureFlags.OnDemandLoaded) != 0 || newTex.Type != TextureType.TT_2d) ? null : newTex);
                        }

                        // 10. Finalize the new texture and link.
                        for (int i = 0; i < 4; i++)
                            newTex.Unknown3[i] = old.Unknown3[i];
                        newTex.SetData(old.ChunkId, App.AssetManager);
                        newTex.AssetNameHash = (uint)Fnv1.HashString(resEntry.Name);
                        if (newTex.Type == TextureType.TT_Cube || newTex.Type == TextureType.TT_2dArray)
                            FixArrayHeader(newTex, App.AssetManager.GetChunkEntry(old.ChunkId));

                        App.AssetManager.ModifyRes(resRid, newTex);

                        resEntry.LinkAsset(chunkEntry);
                        entry.LinkAsset(resEntry);
                    }
                }
                finally
                {
                    if (blobAllocated)
                        DxTexInterop.ReleaseBlob(blob);
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// A cube / array texture header as the engine defines it and as both SWBF games ship it (every 2015 and BFII
        /// reflection cubemap): mipmapChainSize = the mip chain of ONE slice ("does not include the size of texture
        /// arrays", engine TextureHeader; shipped cubes 174720 for 128^2 RGBA16F x6 mips), and compressedChunkOffsets =
        /// the compressed-chunk offsets where mip 1 and mip 2 start (mip-major layout: all slices of a mip together).
        /// Frosty's texture path only fills these for 2D textures, so a cube came out with the WHOLE chunk as its chain
        /// size (6x) and offsets 0/0.
        /// </summary>
        internal static void FixArrayHeader(FbTexture tex, ChunkAssetEntry chunk)
        {
            int slices = tex.Type == TextureType.TT_Cube ? 6 : Math.Max(1, (int)tex.Depth);
            uint chain = 0;
            for (int i = 0; i < tex.MipCount; i++) chain += tex.MipSizes[i];
            System.Reflection.PropertyInfo cs = typeof(FbTexture).GetProperty("ChunkSize");
            if (cs != null) cs.SetValue(tex, chain);
            byte[] comp = chunk?.ModifiedEntry?.Data;
            if (comp == null || tex.MipCount < 3) return;
            long b1 = (long)slices * tex.MipSizes[0], b2 = b1 + (long)slices * tex.MipSizes[1];
            uint[] offs = CompressedOffsets(comp, new[] { b1, b2 });
            if (offs == null) return;
            tex.FirstMipOffset = offs[0];
            tex.SecondMipOffset = offs[1];
        }

        /// <summary>Offsets in a Frostbite block-compressed chunk (per block: u32 BE decompressed size, u16 BE code whose
        /// low nibble holds bits 16..19 of the compressed size, u16 BE compressed size, payload) at which the given
        /// decompressed positions start; null when a position does not fall on a block boundary.</summary>
        internal static uint[] CompressedOffsets(byte[] comp, long[] rawPositions)
        {
            var result = new uint[rawPositions.Length];
            var found = new bool[rawPositions.Length];
            long raw = 0, pos = 0;
            for (int k = 0; k < rawPositions.Length; k++) if (rawPositions[k] == 0) { result[k] = 0; found[k] = true; }
            while (pos + 8 <= comp.Length)
            {
                uint dsize = (uint)(comp[pos] << 24 | comp[pos + 1] << 16 | comp[pos + 2] << 8 | comp[pos + 3]);
                int code = comp[pos + 4] << 8 | comp[pos + 5];
                int csize = (comp[pos + 6] << 8 | comp[pos + 7]) | ((code & 0xF) << 16);
                pos += 8 + csize;
                raw += dsize;
                for (int k = 0; k < rawPositions.Length; k++)
                    if (!found[k] && raw == rawPositions[k]) { result[k] = (uint)pos; found[k] = true; }
            }
            foreach (bool f in found) if (!f) return null;
            return result;
        }

        /// <summary>
        /// Port of FrostyTextureEditor.GetPixelFormat (lines 666-806). Maps a DDS header's
        /// pixel format to a Frostbite pixel-format string + base TextureFlags, branching on
        /// the active profile (legacy DAI/BF4/PvZ vs. newer formats).
        /// </summary>
        private static void GetPixelFormat(FbTexture old, TextureUtils.DDSHeader header, out string pixelFormat, out TextureFlags flags)
        {
            pixelFormat = "Unknown";
            flags = 0;

            if (ProfilesLibrary.DataVersion == (int)ProfileVersion.DragonAgeInquisition
                || ProfilesLibrary.DataVersion == (int)ProfileVersion.Battlefield4
                || ProfilesLibrary.DataVersion == (int)ProfileVersion.PlantsVsZombiesGardenWarfare)
            {
                // DXT1
                if (header.ddspf.dwFourCC == 0x31545844)
                {
                    pixelFormat = "BC1_UNORM";
                    if (old.PixelFormat.Contains("Normal"))
                        pixelFormat = old.PixelFormat;
                    else if (old.PixelFormat.StartsWith("BC1A"))
                        pixelFormat = old.PixelFormat;
                }
                // ATI2 or BC5U
                else if (header.ddspf.dwFourCC == 0x32495441 || header.ddspf.dwFourCC == 0x55354342)
                    pixelFormat = "NormalDXN";
                // DXT3
                else if (header.ddspf.dwFourCC == 0x33545844)
                    pixelFormat = "BC2_UNORM";
                // DXT5
                else if (header.ddspf.dwFourCC == 0x35545844)
                    pixelFormat = "BC3_UNORM";
                // ATI1
                else if (header.ddspf.dwFourCC == 0x31495441)
                    pixelFormat = "BC3A_UNORM";
                // All others
                else if (header.HasExtendedHeader)
                {
                    switch (header.ExtendedHeader.dxgiFormat)
                    {
                        case SharpDX.DXGI.Format.R32G32B32A32_Float: pixelFormat = "ARGB32F"; break;
                        case SharpDX.DXGI.Format.R9G9B9E5_Sharedexp: pixelFormat = "R9G9B9E5F"; break;
                        case SharpDX.DXGI.Format.R8_UNorm: pixelFormat = "L8"; break;
                        case SharpDX.DXGI.Format.R16_UNorm: pixelFormat = "L16"; break;
                        case SharpDX.DXGI.Format.R8G8B8A8_UNorm: pixelFormat = "ARGB8888"; break;
                        case SharpDX.DXGI.Format.BC1_UNorm:
                            pixelFormat = "BC1_UNORM";
                            if (old.PixelFormat.Contains("Normal") || old.PixelFormat.StartsWith("BC1A"))
                                pixelFormat = old.PixelFormat;
                            break;
                        case SharpDX.DXGI.Format.BC2_UNorm: pixelFormat = "BC2_UNORM"; break;
                        case SharpDX.DXGI.Format.BC3_UNorm: pixelFormat = "BC3_UNORM"; break;
                        case SharpDX.DXGI.Format.BC5_UNorm: pixelFormat = "NormalDXN"; break;
                        case SharpDX.DXGI.Format.BC7_UNorm: pixelFormat = "BC7_UNORM"; break;
                        case SharpDX.DXGI.Format.BC1_UNorm_SRgb: pixelFormat = "BC1_UNORM"; flags = TextureFlags.SrgbGamma; break;
                        case SharpDX.DXGI.Format.BC2_UNorm_SRgb: pixelFormat = "BC2_UNORM"; flags = TextureFlags.SrgbGamma; break;
                        case SharpDX.DXGI.Format.BC3_UNorm_SRgb:
                            pixelFormat = (old.PixelFormat == "BC3A_UNORM") ? old.PixelFormat : "BC3_UNORM";
                            flags = TextureFlags.SrgbGamma;
                            break;
                        case SharpDX.DXGI.Format.BC7_UNorm_SRgb: pixelFormat = "BC7_UNORM"; flags = TextureFlags.SrgbGamma; break;
                    }
                }
            }
            else
            {
                // Newer format PixelFormats
                if (header.ddspf.dwFourCC == 0)
                {
                    if (header.ddspf.dwRBitMask == 0x000000FF && header.ddspf.dwGBitMask == 0x0000FF00
                        && header.ddspf.dwBBitMask == 0x00FF0000 && header.ddspf.dwABitMask == 0xFF000000)
                        pixelFormat = "R8G8B8A8_UNORM";
                }
                // DXT1
                else if (header.ddspf.dwFourCC == 0x31545844)
                {
                    pixelFormat = "BC1_UNORM";
                    if (old.PixelFormat == "BC1A_UNORM")
                        pixelFormat = "BC1A_UNORM";
                }
                // DXT5
                else if (header.ddspf.dwFourCC == 0x35545844)
                    pixelFormat = "BC3_UNORM";
                // ATI1
                else if (header.ddspf.dwFourCC == 0x31495441)
                    pixelFormat = "BC4_UNORM";
                // ATI2 or BC5U
                else if (header.ddspf.dwFourCC == 0x32495441 || header.ddspf.dwFourCC == 0x55354342)
                    pixelFormat = "BC5_UNORM";
                // All others
                else if (header.HasExtendedHeader)
                {
                    if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC1_UNorm)
                    {
                        pixelFormat = "BC1_UNORM";
                        if (old.PixelFormat == "BC1A_UNORM")
                            pixelFormat = "BC1A_UNORM";
                    }
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC3_UNorm)
                        pixelFormat = "BC3_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC4_UNorm)
                        pixelFormat = "BC4_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC5_UNorm)
                        pixelFormat = "BC5_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC1_UNorm_SRgb && old.PixelFormat == "BC1A_SRGB")
                        pixelFormat = "BC1A_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC1_UNorm_SRgb)
                        pixelFormat = "BC1_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC3_UNorm_SRgb)
                        pixelFormat = "BC3_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC6H_Uf16)
                        pixelFormat = "BC6U_FLOAT";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC7_UNorm)
                        pixelFormat = "BC7_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC7_UNorm_SRgb)
                        pixelFormat = "BC7_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R8_UNorm)
                        pixelFormat = "R8_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R16G16B16A16_Float)
                        pixelFormat = "R16G16B16A16_FLOAT";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R32G32B32A32_Float)
                        pixelFormat = "R32G32B32A32_FLOAT";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R9G9B9E5_Sharedexp)
                        pixelFormat = "R9G9B9E5_FLOAT";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R8G8B8A8_UNorm)
                        pixelFormat = "R8G8B8A8_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R8G8B8A8_UNorm_SRgb)
                        pixelFormat = "R8G8B8A8_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R10G10B10A2_UNorm)
                        pixelFormat = "R10G10B10A2_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R16_UNorm)
                    {
                        pixelFormat = "R16_UNORM";
                        if (old.PixelFormat == "D16_UNORM")
                            pixelFormat = "D16_UNORM";
                    }
                }
            }
        }
    }
}
