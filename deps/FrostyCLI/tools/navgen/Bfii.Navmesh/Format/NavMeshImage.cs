// VENDORED from BfiiMapTools src\Bfii.Navmesh\Format\NavMeshImage.cs (sha256:0883B78BFEA442E9).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using Bfii.Navmesh.Io;

namespace Bfii.Navmesh.Format
{
    /// <summary>
    /// A complete pathfinding blob payload: resource header, one section, one navigation set.
    /// </summary>
    /// <remarks>
    /// Layout (COMMUNITY_TESTED, FORMAT-PROVENANCE.md rows H1 to H4):
    ///   resource header, 24 bytes
    ///     uint32 endianness flag
    ///     uint32 resource header version, asserted to be 2
    ///     uint32 image size, meaning every byte after this header
    ///     uint32 checksum over those same bytes
    ///     uint32 runtime flags
    ///     uint32 constant flags
    ///   section header, 12 bytes
    ///     uint32 section id, observed as 0x10000
    ///     uint32 section size, excluding this 12-byte header
    ///     uint32 pointer size code, 1 meaning 64-bit pointers
    ///   navigation set
    ///
    /// This is the payload of a PathfindingBlobAsset chunk. For the single-resource delivery path
    /// used by campaign levels, several of these are concatenated in one resource and the lengths
    /// are carried in a separate ChunkSizes array, so a caller slices before parsing; see
    /// <see cref="ReadConcatenated"/>.
    /// </remarks>
    public sealed class NavMeshImage
    {
        public const int ResourceHeaderSize = 24;
        public const int SectionHeaderSize = 12;

        /// <summary>The only resource header version observed. A different value is a hard error.</summary>
        public const uint ExpectedResourceVersion = 2;

        /// <summary>The only section id observed.</summary>
        public const uint ExpectedSectionId = 0x10000;

        public NavMeshImage()
        {
            Order = ByteOrder.Little;
            ResourceVersion = ExpectedResourceVersion;
            SectionId = ExpectedSectionId;
            PointerSizeCode = 1;
            NavSet = new NavSet();
        }

        public ByteOrder Order { get; set; }

        public uint ResourceVersion { get; set; }

        public uint RuntimeFlags { get; set; }

        public uint ConstantFlags { get; set; }

        public uint SectionId { get; set; }

        /// <summary>Pointer width code. 1 means 64-bit pointers; anything else means 32-bit.</summary>
        public uint PointerSizeCode { get; set; }

        public NavSet NavSet { get; set; }

        /// <summary>Image size field as read. Zero for an image that was built in memory.</summary>
        public uint SourceImageSize { get; private set; }

        /// <summary>Checksum field as read. Zero for an image that was built in memory.</summary>
        public uint SourceChecksum { get; private set; }

        /// <summary>
        /// Which checksum reduction reproduced <see cref="SourceChecksum"/>, computed at read time.
        /// Null for an image that was built in memory. This is the calibrator for the one open
        /// question in the container: see <see cref="NavChecksum"/>.
        /// </summary>
        public ChecksumCalibration? ChecksumCalibration { get; private set; }

        public static NavMeshImage Read(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (payload.Length < ResourceHeaderSize + SectionHeaderSize)
            {
                throw new NavFormatException(
                    "payload is " + payload.Length + " bytes, too short to hold a resource header " +
                    "and a section header (" + (ResourceHeaderSize + SectionHeaderSize) + " bytes)");
            }

            BlobReader reader = new BlobReader(payload);
            NavMeshImage image = new NavMeshImage
            {
                Order = NavEndianFlag.Decode(
                    reader.ReadUInt32(ByteOrder.Big, "image.endianFlag"), "image.endianFlag"),
            };

            ByteOrder order = image.Order;

            image.ResourceVersion = reader.ReadUInt32(order, "image.resourceVersion");
            if (image.ResourceVersion != ExpectedResourceVersion)
            {
                throw new NavFormatException(
                    "resource header version is " + image.ResourceVersion + ", expected " +
                    ExpectedResourceVersion + "; this is not a navigation image this library knows");
            }

            image.SourceImageSize = reader.ReadUInt32(order, "image.imageSize");
            image.SourceChecksum = reader.ReadUInt32(order, "image.checksum");
            image.RuntimeFlags = reader.ReadUInt32(order, "image.runtimeFlags");
            image.ConstantFlags = reader.ReadUInt32(order, "image.constantFlags");

            long declaredEnd = (long)ResourceHeaderSize + image.SourceImageSize;
            if (declaredEnd > payload.Length)
            {
                throw new NavFormatException(
                    "resource header declares " + image.SourceImageSize + " bytes after the header, " +
                    "but only " + (payload.Length - ResourceHeaderSize) + " are present");
            }

            image.ChecksumCalibration = NavChecksum.Calibrate(
                payload, ResourceHeaderSize, (int)image.SourceImageSize, image.SourceChecksum);

            image.SectionId = reader.ReadUInt32(order, "image.sectionId");
            uint sectionSize = reader.ReadUInt32(order, "image.sectionSize");
            image.PointerSizeCode = reader.ReadUInt32(order, "image.pointerSize");

            long sectionEnd = (long)ResourceHeaderSize + SectionHeaderSize + sectionSize;
            if (sectionEnd > payload.Length)
            {
                throw new NavFormatException(
                    "section header declares " + sectionSize + " bytes of content, which runs past " +
                    "the end of the payload");
            }

            image.NavSet = NavSet.Read(reader, order, image.PointerSizeCode);

            if (reader.Position != sectionEnd)
            {
                throw new NavFormatException(
                    "navigation set consumed to offset " + reader.Position + " but the section " +
                    "declared its content ends at " + sectionEnd);
            }

            return image;
        }

        /// <summary>
        /// Slices a resource holding several concatenated images and parses each one.
        /// </summary>
        /// <remarks>
        /// This is the campaign delivery shape: one resource, a parallel array of per-segment byte
        /// counts. Worker report 08 section 2.2 B. The community importer refuses this path
        /// entirely; reading it costs nothing here, so it is supported.
        /// </remarks>
        public static NavMeshImage[] ReadConcatenated(byte[] payload, int[] segmentSizes)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (segmentSizes == null)
            {
                throw new ArgumentNullException(nameof(segmentSizes));
            }

            NavMeshImage[] images = new NavMeshImage[segmentSizes.Length];
            int offset = 0;

            for (int i = 0; i < segmentSizes.Length; i++)
            {
                int size = segmentSizes[i];
                if (size < 0 || offset + size > payload.Length)
                {
                    throw new NavFormatException(
                        "segment " + i + " declares " + size + " bytes at offset " + offset +
                        ", which runs past the end of the " + payload.Length + "-byte resource");
                }

                byte[] slice = new byte[size];
                Array.Copy(payload, offset, slice, 0, size);
                images[i] = Read(slice);
                offset += size;
            }

            return images;
        }

        /// <summary>Serializes the image, recomputing every size field and the checksum.</summary>
        public byte[] Write() => Write(NavChecksum.DefaultWordOrder);

        /// <summary>
        /// Serializes the image using an explicit checksum reduction. Callers that have calibrated
        /// against a real donor should pass the order that donor proved.
        /// </summary>
        public byte[] Write(NavChecksum.WordOrder checksumWordOrder)
        {
            BlobWriter writer = new BlobWriter(64 * 1024);
            ByteOrder order = Order;

            writer.WriteUInt32(NavEndianFlag.Encode(order), order);
            writer.WriteUInt32(ResourceVersion, order);

            int imageSizeOffset = writer.Position;
            writer.WriteUInt32(0, order);
            int checksumOffset = writer.Position;
            writer.WriteUInt32(0, order);

            writer.WriteUInt32(RuntimeFlags, order);
            writer.WriteUInt32(ConstantFlags, order);

            writer.WriteUInt32(SectionId, order);
            int sectionSizeOffset = writer.Position;
            writer.WriteUInt32(0, order);
            writer.WriteUInt32(PointerSizeCode, order);

            int sectionContentStart = writer.Position;
            NavSet.Write(writer, order, PointerSizeCode);
            int sectionContentBytes = writer.Position - sectionContentStart;

            int imageSize = writer.Position - ResourceHeaderSize;

            writer.PatchUInt32(sectionSizeOffset, (uint)sectionContentBytes, order);
            writer.PatchUInt32(imageSizeOffset, (uint)imageSize, order);

            byte[] result = writer.ToArray();
            uint checksum = NavChecksum.Compute(result, ResourceHeaderSize, imageSize, checksumWordOrder);

            // Patch the checksum into the finished buffer: it is a function of every byte after the
            // resource header, including the image-size field written a moment ago.
            PatchUInt32InPlace(result, checksumOffset, checksum, order);
            return result;
        }

        private static void PatchUInt32InPlace(byte[] target, int offset, uint value, ByteOrder order)
        {
            if (order == ByteOrder.Big)
            {
                target[offset] = (byte)(value >> 24);
                target[offset + 1] = (byte)(value >> 16);
                target[offset + 2] = (byte)(value >> 8);
                target[offset + 3] = (byte)value;
            }
            else
            {
                target[offset] = (byte)value;
                target[offset + 1] = (byte)(value >> 8);
                target[offset + 2] = (byte)(value >> 16);
                target[offset + 3] = (byte)(value >> 24);
            }
        }
    }
}
