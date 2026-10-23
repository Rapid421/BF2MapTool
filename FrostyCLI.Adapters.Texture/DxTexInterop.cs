using System;
using System.Runtime.InteropServices;
using FrostySdk.Resources;

namespace FrostyCLI.Adapters.Texture
{
    #region -- Import / Export --
    public struct BlobData
    {
        public byte[] Data
        {
            get
            {
                byte[] outBuf = new byte[size];
                Marshal.Copy(data, outBuf, 0, (int)size);
                return outBuf;
            }
        }
        private IntPtr data;
        private long size;
    }

    public enum ImageFormat
    {
        PNG,
        TGA,
        HDR,
        DDS
    }

    public struct TextureImportOptions
    {
        public TextureType type;
        public int format; // SharpDX.DXGI.Format (int-backed enum; ABI-identical)
        public bool generateMipmaps;
        public int mipmapsFilter;
        public bool resizeTexture;
        public int resizeFilter;
        public int resizeWidth;
        public int resizeHeight;
    };
    #endregion

    public static class DxTexInterop
    {
        [DllImport("thirdparty/dxtex.dll", EntryPoint = "ConvertImageToDDS")]
        public static extern void ConvertImageToDDS(byte[] pData, long iDataSize, ImageFormat origFormat, TextureImportOptions options, ref BlobData pOutData);
        [DllImport("thirdparty/dxtex.dll", EntryPoint = "ConvertImagesToDDS")]
        public static extern void ConvertImagesToDDS(byte[] pData, long[] iDataSize, long iCount, ImageFormat origFormat, TextureImportOptions options, ref BlobData pOutData);
        [DllImport("thirdparty/dxtex.dll", EntryPoint = "ReleaseBlob")]
        public static extern void ReleaseBlob(BlobData pData);

        public static bool IsAvailable()
        {
            try
            {
                return System.IO.File.Exists(System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(DxTexInterop).Assembly.Location) ?? ".",
                    "thirdparty", "dxtex.dll"));
            }
            catch { return false; }
        }
    }
}
