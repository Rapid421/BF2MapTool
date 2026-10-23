# Re-vendors the terrain byte-format libraries from BfiiMapTools into FrostyCLI.Core\Terrain.
#
# BfiiMapTools owns the terrain format: its master plan gives Bfii.TerrainFormat exclusive
# ownership of anything that reads or writes terrain resource bytes, and Bfii.TerrainDerive
# exclusive ownership of the derived arrays. This script copies that source in rather than
# forking it, so the CLI keeps one upstream to sync from. Edit the format upstream, then run
# this; do not hand-edit the vendored copies.
#
# Two mechanical adaptations are applied, because FrostyCLI targets .NET Framework 4.8 while
# the upstream targets net8.0:
#   1. "#nullable enable" is prepended -- upstream turns nullable on in its csproj, and
#      FrostyCLI.Core cannot without lighting up every other file in the assembly.
#   2. System.Buffers.Binary (BinaryPrimitives) is replaced with direct little-endian byte
#      arithmetic -- that namespace needs the System.Memory package on net48, and dragging
#      System.Memory + S.R.CS.Unsafe into a Frosty-hosting process risks binding conflicts.
#
# Every replacement below must match, or the script fails: an upstream edit that moves this
# code has to be looked at, not silently half-applied.

[CmdletBinding()]
param(
    [string] $Upstream = (Join-Path $PSScriptRoot '..\..\BfiiMapTools\src'),
    [string] $Dest     = (Join-Path $PSScriptRoot '..\FrostyCLI.Core\Terrain')
)

$ErrorActionPreference = 'Stop'

$files = @(
    'Bfii.TerrainFormat\Primitives.cs',
    'Bfii.TerrainFormat\Model.cs',
    'Bfii.TerrainFormat\MaskStride.cs',
    'Bfii.TerrainFormat\Parser.cs',
    'Bfii.TerrainFormat\Serializer.cs',
    'Bfii.TerrainFormat\Rle4.cs',
    'Bfii.TerrainDerive\Sizing.cs',
    'Bfii.TerrainDerive\MinMaxPyramid.cs',
    'Bfii.TerrainDerive\Derivations.cs',
    # Terrain authoring (BfiiMapTools terrain-authoring branch): builds every raster tree from source rasters.
    'Bfii.TerrainBuild\HeightSources.cs',
    'Bfii.TerrainBuild\MinMaxLevels.cs',
    'Bfii.TerrainBuild\OccluderPlaneFit.cs',
    'Bfii.TerrainBuild\HeightfieldBuilder.cs',
    'Bfii.TerrainBuild\MaskBuilder.cs',
    'Bfii.TerrainBuild\ColorMaterialBuilders.cs',
    'Bfii.TerrainBuild\TerrainAssembler.cs',
    'Bfii.TerrainBuild\TerrainGenerator.cs',
    'Bfii.TerrainBuild\RasterFiles.cs',
    'Bfii.TerrainBuild\HeightmapInput.cs',
    'Bfii.TerrainBuild\LayerInput.cs',
    'Bfii.TerrainBuild\TerrainExport.cs',
    'Bfii.TerrainBuild\MaskEditor.cs'
)

# Applied to Primitives.cs only: @(exact upstream text, net48 replacement).
$primitiveFixes = @(
    @('using System.Buffers.Binary;',
      '// using System.Buffers.Binary;  -- replaced with byte arithmetic below, for net48'),

    @('ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos, 2));',
      'ushort v = (ushort)(_data[_pos] | (_data[_pos + 1] << 8));'),

    @('uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos, 4));',
      'uint v = (uint)(_data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24));'),

    @('BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_len, 2), v);',
      '_buf[_len] = (byte)v; _buf[_len + 1] = (byte)(v >> 8);'),

    @('BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_len, 4), v);',
      '_buf[_len] = (byte)v; _buf[_len + 1] = (byte)(v >> 8); _buf[_len + 2] = (byte)(v >> 16); _buf[_len + 3] = (byte)(v >> 24);'),

    @('BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(offset, 4), value);',
      '_buf[offset] = (byte)value; _buf[offset + 1] = (byte)(value >> 8); _buf[offset + 2] = (byte)(value >> 16); _buf[offset + 3] = (byte)(value >> 24);'),

    # net48 has no BitConverter.SingleToUInt32Bits / UInt32BitsToSingle. GetBytes round-trips
    # the same bits on the little-endian x64 this ships for.
    @('new F32(BitConverter.SingleToUInt32Bits(value));',
      'new F32(BitConverter.ToUInt32(BitConverter.GetBytes(value), 0));'),

    @('public float Value => BitConverter.UInt32BitsToSingle(Bits);',
      'public float Value => BitConverter.ToSingle(BitConverter.GetBytes(Bits), 0);'),

    # net48 has no span-based Guid ctor / TryWriteBytes. Net48Compat (hand-written, not
    # vendored) does the same thing through the byte[] forms, which use the identical
    # mixed-endian field layout -- the bytes on disk are unchanged.
    @('new Guid(_data.AsSpan(_pos, 16))',
      'GuidBytes.Read(_data, _pos)'),

    @('g.TryWriteBytes(_buf.AsSpan(_len, 16))',
      'GuidBytes.TryWrite(g, _buf, _len)')
)

if (-not (Test-Path $Upstream)) {
    throw "Upstream not found: $Upstream. Clone BfiiMapTools next to frostycli, or pass -Upstream."
}
if (-not (Test-Path $Dest)) { New-Item -ItemType Directory -Path $Dest | Out-Null }

$stamp = @()
foreach ($rel in $files) {
    $src = Join-Path $Upstream $rel
    if (-not (Test-Path $src)) { throw "Missing upstream file: $src" }

    $name = Split-Path $rel -Leaf
    $text = [IO.File]::ReadAllText($src)
    $srcHash = (Get-FileHash -Path $src -Algorithm SHA256).Hash.Substring(0, 16)

    if ($name -eq 'Primitives.cs') {
        foreach ($fix in $primitiveFixes) {
            $find = $fix[0]
            if (-not $text.Contains($find)) {
                throw "Primitives.cs no longer contains the expected text -- upstream changed, review by hand:`n  $find"
            }
            $text = $text.Replace($find, $fix[1])
        }
    }
    elseif ($text.Contains('System.Buffers.Binary')) {
        throw "$name now uses System.Buffers.Binary; add a replacement for it to this script."
    }

    $header = "#nullable enable" + [Environment]::NewLine +
              "// VENDORED from BfiiMapTools src\$rel (sha256:$srcHash)." + [Environment]::NewLine +
              "// Re-sync with tools\sync-terrain-format.ps1; do not edit here." + [Environment]::NewLine

    [IO.File]::WriteAllText((Join-Path $Dest $name), $header + $text)
    $stamp += "  {0,-20} {1}" -f $name, $srcHash
    Write-Host "  vendored $name ($srcHash)"
}

Write-Host ""
Write-Host "Vendored $($files.Count) file(s) into $Dest"
$stamp | Write-Host
