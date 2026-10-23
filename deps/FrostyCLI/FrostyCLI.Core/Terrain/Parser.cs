#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainFormat\Parser.cs (sha256:85771DC63169B693).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;

namespace Bfii.TerrainFormat
{
    /// <summary>How the payload was framed on input. Master plan §10 C-07.</summary>
    public enum PayloadFraming
    {
        /// <summary>
        /// Payload begins at offset 0, as returned by <c>AssetManager.GetRes</c>.
        /// Provenance: BFII_EXACT — the C# reader starts at position 0 (W01 §4.4).
        /// </summary>
        Bare = 0,

        /// <summary>
        /// Payload is preceded by the 16-byte <c>resMeta</c>, as written by an exporter.
        /// Glacier's <c>mount_game.rs --dump-res</c> "prepends the 16-byte entry.res_meta so output
        /// is byte-identical to editor dumps"; its driver correspondingly does <c>buf.advance(16)</c>.
        /// Provenance: BFII_EXACT (W06 §8.3, resolving C-07).
        /// </summary>
        ResMetaPrefixed = 1,
    }

    public sealed class ParseOptions
    {
        /// <summary>
        /// Force a heightfield node-flow. When null the parser tries every flow and accepts the one
        /// that consumes exactly <c>rasterTreeLoadSize</c>. Master plan §10 C-03 is unresolved, so
        /// null (search) is the honest default.
        /// </summary>
        public HeightfieldNodeFlow? HeightfieldFlow { get; set; }

        /// <summary>
        /// Force the mask coverage-grid stack level count. When null the parser searches L in 1..8.
        /// Never defaults to a byte. See <see cref="MaskStride"/>.
        /// </summary>
        public int? MaskStrideLevel { get; set; }

        /// <summary>
        /// Force the input framing. When null the parser detects it by trying both and accepting
        /// whichever parses cleanly. Master plan §8 X1: "detect which you have; do not hard-code a
        /// skip."
        /// </summary>
        public PayloadFraming? Framing { get; set; }

        /// <summary>Safety bound on recursion depth. FIFA's own header notes 16 levels is the practical max.</summary>
        public int MaxDepth { get; set; } = 24;

        /// <summary>
        /// Safety bound on parsed node count, expressed as a multiple of the declared count. Only a
        /// miscalibrated stride or flow can exceed the declared count, and this stops it allocating.
        /// </summary>
        public int NodeCountSlackFactor { get; set; } = 4;

        /// <summary>
        /// When no heightfield node flow consumes the record exactly, accept the flow that parsed
        /// with the least leftover and carry the remainder as <see cref="RasterTreeRecord.Slack"/>.
        /// <para>
        /// This is a <b>diagnostic</b> fallback, never a tuning knob. Padding inside a record is not
        /// excluded by the evidence — W01 §4.8 grades BFII's alignment and padding rules
        /// <c>UNKNOWN</c>, "absence of evidence, not proof of no padding" — so a record with a tail
        /// is a real possibility that deserves a readable report rather than an opaque failure.
        /// Assertion B1 still fails, the report still names the byte count, and
        /// <see cref="X1TerrainReport.Accepted"/> is still false. Set false for a strict run.
        /// </para>
        /// </summary>
        public bool AllowHeightfieldSlackFallback { get; set; } = true;

        public static ParseOptions Default => new ParseOptions();

        public static ParseOptions Strict => new ParseOptions { AllowHeightfieldSlackFallback = false };
    }

    /// <summary>One attempted-and-rejected candidate, kept so a failure is explainable.</summary>
    public sealed class RejectedCandidate
    {
        public string Kind = "";
        public string Candidate = "";
        public string Reason = "";
        public override string ToString() => $"{Kind}[{Candidate}]: {Reason}";
    }

    public sealed class ParseDiagnostics
    {
        public PayloadFraming Framing;
        public int PayloadOffset;
        public int PayloadLength;

        public HeightfieldNodeFlow? FittedHeightfieldFlow;

        /// <summary>
        /// True when no flow consumed the heightfield record exactly and the parse was accepted as a
        /// diagnostic fallback. Assertion B1 fails whenever this is set.
        /// </summary>
        public bool HeightfieldSlackFallbackUsed;

        public int? FittedMaskStrideLevel;
        public int? FittedMaskStrideBytes;

        /// <summary>Every raster tree type byte observed, in order. Master plan §10 C-01 check.</summary>
        public List<byte> ObservedRasterTreeTypeBytes { get; } = new List<byte>();

        /// <summary>Type bytes outside 0..4. Must be empty for BFII; 5-7 would mean FIFA ids.</summary>
        public List<byte> OutOfRangeRasterTreeTypeBytes { get; } = new List<byte>();

        public List<RejectedCandidate> Rejected { get; } = new List<RejectedCandidate>();

        public int TotalBytesConsumed;
        public int TrailingByteCount;
    }

    public sealed class ParseResult
    {
        public TerrainStreamingTreeResource? Resource;
        public ParseDiagnostics Diagnostics = new ParseDiagnostics();
        public bool Success => Resource != null && Failure == null;
        public string? Failure;
    }

    /// <summary>
    /// Parser for the BFII <c>TerrainStreamingTree</c> RES payload.
    /// <para>
    /// Every layout claim implemented here is traced in
    /// <c>docs\session-b\FORMAT-CONTRACT-TerrainStreamingTree.md</c>. Nothing here is derived from FIFA 21
    /// source: the FIFA tree contributed the <i>shape</i> of the alternate node flow and the
    /// <i>generating formula</i> for the mask stride, both of which are implemented as searchable
    /// options rather than as assumptions, and both of which are labelled at their definition.
    /// </para>
    /// </summary>
    public static class TerrainStreamingTreeParser
    {
        public static ParseResult Parse(byte[] payload, ParseOptions? options = null)
        {
            if (payload is null) throw new ArgumentNullException(nameof(payload));
            options ??= ParseOptions.Default;

            if (options.Framing.HasValue)
                return ParseAt(payload, options, options.Framing.Value);

            // Detect framing by trying both. Bare first: that is what AssetManager.GetRes yields,
            // and it is the shape the in-Frosty reader is proven against. A parse that needed the
            // slack fallback, or that left trailing bytes, is only provisionally right — so the
            // other framing is still tried and a clean parse wins over a scruffy one.
            var bare = ParseAt(payload, options, PayloadFraming.Bare);
            if (bare.Success && IsClean(bare)) return bare;

            if (payload.Length <= BfiiProfile.ResMetaSize) return bare;

            var prefixed = ParseAt(payload, options, PayloadFraming.ResMetaPrefixed);

            if (prefixed.Success && IsClean(prefixed))
            {
                prefixed.Diagnostics.Rejected.Add(new RejectedCandidate
                {
                    Kind = "framing",
                    Candidate = "Bare",
                    Reason = bare.Success
                        ? "parsed, but not cleanly (slack fallback or trailing bytes)"
                        : bare.Failure ?? "unspecified",
                });
                return prefixed;
            }

            if (bare.Success)
            {
                bare.Diagnostics.Rejected.Add(new RejectedCandidate
                {
                    Kind = "framing",
                    Candidate = "ResMetaPrefixed",
                    Reason = prefixed.Success
                        ? "parsed, but not cleanly (slack fallback or trailing bytes)"
                        : prefixed.Failure ?? "unspecified",
                });
                return bare;
            }

            if (prefixed.Success) return prefixed;

            bare.Diagnostics.Rejected.Add(new RejectedCandidate
            {
                Kind = "framing",
                Candidate = "ResMetaPrefixed",
                Reason = prefixed.Failure ?? "unspecified",
            });
            return bare;

            static bool IsClean(ParseResult r)
                => !r.Diagnostics.HeightfieldSlackFallbackUsed && r.Diagnostics.TrailingByteCount == 0;
        }

        private static ParseResult ParseAt(byte[] payload, ParseOptions options, PayloadFraming framing)
        {
            var result = new ParseResult();
            var diag = result.Diagnostics;
            diag.Framing = framing;
            diag.PayloadOffset = framing == PayloadFraming.ResMetaPrefixed ? BfiiProfile.ResMetaSize : 0;
            diag.PayloadLength = payload.Length - diag.PayloadOffset;

            if (diag.PayloadLength < StreamingTreeHeader.SizeBytes)
            {
                result.Failure = $"payload of {diag.PayloadLength} byte(s) is shorter than the " +
                                 $"{StreamingTreeHeader.SizeBytes}-byte header";
                return result;
            }

            try
            {
                var res = new TerrainStreamingTreeResource();
                if (framing == PayloadFraming.ResMetaPrefixed)
                {
                    res.ResMetaPrefix = new byte[BfiiProfile.ResMetaSize];
                    Buffer.BlockCopy(payload, 0, res.ResMetaPrefix, 0, BfiiProfile.ResMetaSize);
                }

                var r = new ByteReader(payload, diag.PayloadOffset, diag.PayloadLength);

                // ---- header, 0x00 .. 0x19 -------------------------------------------------
                var h = res.Header;
                h.UnblurredSamplesPerNodeSidePot = r.ReadUInt32();
                h.TrackTextureDetailFalloffRaw = r.ReadByte();
                h.InvisibleDetailReductionFactor = r.ReadF32();
                h.OccludedDetailReductionFactor = r.ReadF32();
                h.Field0x0D = r.ReadUInt32();
                h.Field0x11 = r.ReadUInt32();
                h.NodeCount = r.ReadUInt32();
                h.FreeStreamingEnableRaw = r.ReadByte();

                // ---- length-framed raster tree records ------------------------------------
                while (true)
                {
                    byte typeByte = r.ReadByte();
                    if (typeByte == (byte)RasterTreeType.EndOfList)
                    {
                        res.Terminator = typeByte;
                        break;
                    }

                    diag.ObservedRasterTreeTypeBytes.Add(typeByte);
                    if (typeByte > (byte)RasterTreeType.DestructionDepth)
                        diag.OutOfRangeRasterTreeTypeBytes.Add(typeByte);

                    uint declared = r.ReadUInt32();
                    int start = r.Position;
                    if (declared > int.MaxValue || start + (long)declared > r.WindowEnd)
                        throw new TerrainFormatException(
                            $"raster tree type {typeByte} declares {declared} byte(s) at {start}, " +
                            $"which overruns the payload window end {r.WindowEnd}", start);

                    var record = new RasterTreeRecord
                    {
                        Type = (RasterTreeType)typeByte,
                        DeclaredSize = declared,
                    };

                    ParseRasterTreeBody(payload, start, (int)declared, record, options, diag);

                    record.Slack = SliceSlack(payload, start + record.ParsedBytes,
                                              (int)declared - record.ParsedBytes);
                    res.RasterTrees.Add(record);

                    r.Position = start + (int)declared;
                }

                // ---- streaming node quadtree ---------------------------------------------
                if (h.NodeCount > 0)
                {
                    int parsed = 0;
                    int budget = SafeBudget(h.NodeCount, options.NodeCountSlackFactor);
                    res.StreamingRoot = ParseStreamingNode(
                        r, new QuadtreeNodeId(0, 0, 0), ref parsed, budget, 0, options.MaxDepth);
                }

                diag.TotalBytesConsumed = r.Consumed;
                int trailing = r.Remaining;
                diag.TrailingByteCount = trailing;
                if (trailing > 0)
                    res.TrailingBytes = SliceSlack(payload, r.Position, trailing);

                result.Resource = res;
                return result;
            }
            catch (TerrainFormatException ex)
            {
                result.Failure = ex.Message;
                return result;
            }
            catch (OverflowException ex)
            {
                result.Failure = "arithmetic overflow while sizing a block: " + ex.Message;
                return result;
            }
        }

        private static byte[] SliceSlack(byte[] payload, int offset, int count)
        {
            if (count <= 0) return Array.Empty<byte>();
            var buf = new byte[count];
            Buffer.BlockCopy(payload, offset, buf, 0, count);
            return buf;
        }

        private static int SafeBudget(uint declared, int factor)
        {
            long budget = (long)declared * Math.Max(1, factor) + 64;
            return (int)Math.Min(budget, 4_000_000);
        }

        // -----------------------------------------------------------------------------------
        // Raster tree bodies
        // -----------------------------------------------------------------------------------

        private static void ParseRasterTreeBody(byte[] payload, int start, int declared,
            RasterTreeRecord record, ParseOptions options, ParseDiagnostics diag)
        {
            switch (record.Type)
            {
                case RasterTreeType.Heightfield:
                    ParseHeightfieldWithFlowSearch(payload, start, declared, record, options, diag);
                    return;

                case RasterTreeType.Mask:
                    ParseMaskWithStrideSearch(payload, start, declared, record, options, diag);
                    return;

                case RasterTreeType.Color:
                case RasterTreeType.Material:
                case RasterTreeType.DestructionDepth:
                {
                    // Structured, and accepted only on exact consumption; anything else is carried
                    // verbatim with the reason recorded, so an unknown variant stays round-trippable.
                    var reader = new ByteReader(payload, start, declared);
                    string? failure = null;
                    RasterTreeBody? tree = null;
                    try
                    {
                        tree = record.Type == RasterTreeType.Color ? ParseColorTree(reader, options)
                             : record.Type == RasterTreeType.Material ? ParseMaterialTree(reader, options)
                             : (RasterTreeBody)ParseDestructionDepthTree(reader, options);
                        if (reader.Consumed != declared)
                            failure = $"consumed {reader.Consumed} of declared {declared}";
                    }
                    catch (TerrainFormatException ex) { failure = ex.Message; }
                    catch (OverflowException ex) { failure = "overflow: " + ex.Message; }

                    if (failure == null)
                    {
                        record.Body = tree;
                        record.ParsedBytes = reader.Consumed;
                        return;
                    }

                    diag.Rejected.Add(new RejectedCandidate
                    {
                        Kind = "rasterTree",
                        Candidate = record.Type.ToString(),
                        Reason = failure + "; carried verbatim",
                    });
                    goto default;
                }

                // Unknown type ids: opaque carriage is the only honest round-trip and it is exact.
                default:
                {
                    var opaque = new OpaqueRasterTree(record.Type)
                    {
                        Payload = SliceSlack(payload, start, declared),
                    };
                    record.Body = opaque;
                    record.ParsedBytes = declared;
                    return;
                }
            }
        }

        // -----------------------------------------------------------------------------------
        // Heightfield tree (type 0)
        // -----------------------------------------------------------------------------------

        private static void ParseHeightfieldWithFlowSearch(byte[] payload, int start, int declared,
            RasterTreeRecord record, ParseOptions options, ParseDiagnostics diag)
        {
            IReadOnlyList<HeightfieldNodeFlow> flows = options.HeightfieldFlow.HasValue
                ? new[] { options.HeightfieldFlow.Value }
                : new[] { HeightfieldNodeFlow.Merged, HeightfieldNodeFlow.AlwaysReadFive };

            TerrainFormatException? lastError = null;
            HeightfieldTree? bestPartial = null;
            HeightfieldNodeFlow bestPartialFlow = default;
            int bestPartialConsumed = -1;

            foreach (var flow in flows)
            {
                try
                {
                    var reader = new ByteReader(payload, start, declared);
                    var tree = ParseHeightfieldTree(reader, flow, options);

                    // Acceptance for a flow is exact consumption of the declared record length.
                    // That is assertion B1, applied as a filter rather than only as a verdict.
                    if (reader.Consumed == declared)
                    {
                        record.Body = tree;
                        record.ParsedBytes = reader.Consumed;
                        diag.FittedHeightfieldFlow = flow;
                        return;
                    }

                    diag.Rejected.Add(new RejectedCandidate
                    {
                        Kind = "heightfieldFlow",
                        Candidate = flow.ToString(),
                        Reason = $"consumed {reader.Consumed} of declared {declared}",
                    });

                    if (reader.Consumed > bestPartialConsumed)
                    {
                        bestPartial = tree;
                        bestPartialFlow = flow;
                        bestPartialConsumed = reader.Consumed;
                    }
                }
                catch (TerrainFormatException ex)
                {
                    lastError = ex;
                    diag.Rejected.Add(new RejectedCandidate
                    {
                        Kind = "heightfieldFlow",
                        Candidate = flow.ToString(),
                        Reason = ex.Message,
                    });
                }
                catch (OverflowException ex)
                {
                    diag.Rejected.Add(new RejectedCandidate
                    {
                        Kind = "heightfieldFlow",
                        Candidate = flow.ToString(),
                        Reason = "overflow: " + ex.Message,
                    });
                }
            }

            if (options.AllowHeightfieldSlackFallback && bestPartial != null)
            {
                record.Body = bestPartial;
                record.ParsedBytes = bestPartialConsumed;
                diag.FittedHeightfieldFlow = bestPartialFlow;
                diag.HeightfieldSlackFallbackUsed = true;
                diag.Rejected.Add(new RejectedCandidate
                {
                    Kind = "heightfieldFlow",
                    Candidate = bestPartialFlow.ToString(),
                    Reason = $"ACCEPTED AS DIAGNOSTIC FALLBACK: consumed {bestPartialConsumed} of " +
                             $"declared {declared}; the remaining {declared - bestPartialConsumed} " +
                             "byte(s) are carried as slack and assertion B1 fails",
                });
                return;
            }

            throw new TerrainFormatException(
                "no heightfield node flow consumed exactly the declared rasterTreeLoadSize of " +
                $"{declared}; tried {string.Join(", ", flows)}" +
                (lastError is null ? "" : $"; last error: {lastError.Message}"), start);
        }

        private static HeightfieldTree ParseHeightfieldTree(ByteReader r, HeightfieldNodeFlow flow,
            ParseOptions options)
        {
            var t = new HeightfieldTree { Flow = flow };

            t.NodeSamplesPerSide = r.ReadUInt32();
            t.ResourceAtlasSampleCountX = r.ReadUInt32();
            t.ResourceAtlasSampleCountY = r.ReadUInt32();
            t.BlurrinessLog2 = r.ReadInt32();
            t.WorldSizeY = r.ReadF32();
            t.PhysicsMetersPerSample = r.ReadF32();
            t.PhysicsCropWidth = r.ReadInt32();
            t.Unknown0x1DRaw = r.ReadByte();
            t.Unknown0x1E = r.ReadF32();
            t.MinMaxStackDepth = r.ReadUInt32();
            t.OccluderGridStackDepth = r.ReadUInt32();
            t.DensityMapNodeSamplesPerSide = r.ReadUInt32();
            t.DensityMapBorderWidth = r.ReadUInt32();
            t.DensityMapNodeSamplesPerSidePot = r.ReadUInt32();
            t.DensityMapResolutionRatio = r.ReadF32();
            t.NodeCount = r.ReadUInt32();
            t.PersistentNodeCount = r.ReadUInt32();
            t.PersistentDedicatedServerNodeCount = r.ReadUInt32();
            t.SkippedNodeCount = r.ReadUInt32();
            t.NodeBorderWidth = r.ReadUInt32();

            // Reject obviously impossible geometry before it becomes a multi-GB allocation.
            if (t.NodeSamplesPerSide > 65536 || t.MinMaxStackDepth > 24 || t.OccluderGridStackDepth > 24
                || t.DensityMapNodeSamplesPerSide > 65536)
                throw new TerrainFormatException(
                    "heightfield header out of plausible range " +
                    $"(nodeSamplesPerSide={t.NodeSamplesPerSide}, minMaxDepth={t.MinMaxStackDepth}, " +
                    $"occluderDepth={t.OccluderGridStackDepth}, densitySamples={t.DensityMapNodeSamplesPerSide})",
                    r.Position);

            if (t.NodeCount > 0)
            {
                int parsed = 0;
                int budget = SafeBudget(t.NodeCount, options.NodeCountSlackFactor);
                t.Root = ParseHeightfieldNode(r, t, flow, new QuadtreeNodeId(0, 0, 0),
                                              ref parsed, budget, 0, options.MaxDepth);
            }

            return t;
        }

        private static HeightfieldNode ParseHeightfieldNode(ByteReader r, HeightfieldTree t,
            HeightfieldNodeFlow flow, QuadtreeNodeId id, ref int parsed, int budget, int depth, int maxDepth)
        {
            if (++parsed > budget)
                throw new TerrainFormatException($"heightfield node budget {budget} exceeded", r.Position);
            if (depth > maxDepth)
                throw new TerrainFormatException($"heightfield depth {depth} exceeds {maxDepth}", r.Position);

            var n = new HeightfieldNode { Id = id, RecordOffset = r.Position - r.WindowStart };

            n.BoundingBox = r.ReadBox();

            // NOTE: a 21st f32 is read here for DataVersion 20170321 (MEA) and 20171110 (NFS Payback)
            // only. BFII (20171117) does NOT read it. Provenance: BFII_EXACT, W01 section 4.4.1.

            n.DisabledRaw = r.ReadByte();
            if (n.Disabled)
                return n; // node ends here in both flows

            n.FlagsRead = true;
            n.SkippedRaw = r.ReadByte();
            n.HasDataRaw = r.ReadByte();

            if (flow == HeightfieldNodeFlow.Merged && !n.HasData && !n.Skipped)
                return n; // FIFA-shaped early return, before containsHoles/persistent

            n.TailFlagsRead = true;
            n.ContainsHolesRaw = r.ReadByte();
            n.PersistentRaw = r.ReadByte();

            if (flow == HeightfieldNodeFlow.Merged)
            {
                if (n.HasData && n.Persistent) n.Payload = ReadPayload(r, t);
                else if (n.Skipped) n.SkippedMinMax = r.ReadBytes(t.MinMaxBytes);
            }
            else // AlwaysReadFive
            {
                if (n.Skipped) n.SkippedMinMax = r.ReadBytes(t.MinMaxBytes);
                if (n.HasData && n.Persistent) n.Payload = ReadPayload(r, t);
            }

            n.HasChildrenFlagRead = true;
            n.HasChildrenRaw = r.ReadByte();
            if (n.HasChildren)
                for (int i = 0; i < 4; i++)
                    n.Children.Add(ParseHeightfieldNode(r, t, flow, id.Child(i),
                                                        ref parsed, budget, depth + 1, maxDepth));

            return n;
        }

        private static HeightfieldNodePayload ReadPayload(ByteReader r, HeightfieldTree t) =>
            new HeightfieldNodePayload
            {
                AtlasData = r.ReadBytes(t.AtlasBytes),
                MinMaxStackData = r.ReadBytes(t.MinMaxBytes),
                OccluderGridStackData = r.ReadBytes(t.OccluderBytes),
                DensityData = r.ReadBytes(t.DensityBytes),
            };

        // -----------------------------------------------------------------------------------
        // Mask tree (type 1)
        // -----------------------------------------------------------------------------------

        private static void ParseMaskWithStrideSearch(byte[] payload, int start, int declared,
            RasterTreeRecord record, ParseOptions options, ParseDiagnostics diag)
        {
            IReadOnlyList<int> levels = options.MaskStrideLevel.HasValue
                ? new[] { options.MaskStrideLevel.Value }
                : HeaderFirst(payload, start, declared);

            foreach (int level in levels)
            {
                string reason;
                var reader = new ByteReader(payload, start, declared);
                MaskTree? tree;
                try
                {
                    tree = ParseMaskTree(reader, level, options);
                }
                catch (TerrainFormatException ex)
                {
                    diag.Rejected.Add(new RejectedCandidate
                    {
                        Kind = "maskStrideLevel",
                        Candidate = $"L={level} stride={MaskStride.ForLevel(level)}",
                        Reason = ex.Message,
                    });
                    continue;
                }
                catch (OverflowException ex)
                {
                    diag.Rejected.Add(new RejectedCandidate
                    {
                        Kind = "maskStrideLevel",
                        Candidate = $"L={level}",
                        Reason = "overflow: " + ex.Message,
                    });
                    continue;
                }

                if (!AcceptMask(tree, reader.Consumed, declared, out reason))
                {
                    diag.Rejected.Add(new RejectedCandidate
                    {
                        Kind = "maskStrideLevel",
                        Candidate = $"L={level} stride={MaskStride.ForLevel(level)}",
                        Reason = reason,
                    });
                    continue;
                }

                record.Body = tree;
                record.ParsedBytes = reader.Consumed;
                diag.FittedMaskStrideLevel = level;
                diag.FittedMaskStrideBytes = MaskStride.ForLevel(level);
                return;
            }

            throw new TerrainFormatException(
                $"no coverage-grid stack level in [{MaskStride.MinLevel},{MaskStride.MaxLevel}] " +
                $"satisfies all four mask acceptance conditions against a declared " +
                $"rasterTreeLoadSize of {declared}", start);
        }

        /// <summary>
        /// The header states the coverage-grid level count at offset 52 (BFII_EXACT, 39/39), so it is tried first;
        /// the other candidates remain as a fallback for data that disagrees with it.
        /// </summary>
        private static IReadOnlyList<int> HeaderFirst(byte[] payload, int start, int declared)
        {
            var levels = new List<int>();
            if (declared >= MaskTree.HeaderSizeBytes)
            {
                uint stated = (uint)(payload[start + 52] | (payload[start + 53] << 8) | (payload[start + 54] << 16) | (payload[start + 55] << 24));
                if (stated >= MaskStride.MinLevel && stated <= MaskStride.MaxLevel) levels.Add((int)stated);
            }
            foreach (int l in MaskStride.CandidateLevels) if (!levels.Contains(l)) levels.Add(l);
            return levels;
        }

        /// <summary>
        /// The acceptance conditions. All must hold at once; any one alone is satisfiable by a
        /// wrong stride, which is precisely how the 0xAB-versus-11 bug survived (W01 §4.4.2).
        /// </summary>
        private static bool AcceptMask(MaskTree tree, int consumed, int declared, out string reason)
        {
            if (consumed != declared)
            {
                reason = $"consumed {consumed} of declared {declared}";
                return false;
            }

            int nodes = 0, subtiles = 0;
            foreach (var n in tree.EnumerateNodes())
            {
                nodes++;
                subtiles += n.Subtiles.Count;
                if (n.BodyPresent && n.DerivedNonTrivialSubtileCount != n.NonTrivialSubtileCount)
                {
                    reason = $"node {n.Id} declares nonTrivialSubtileCount={n.NonTrivialSubtileCount} " +
                             $"but {n.DerivedNonTrivialSubtileCount} subtile(s) carry a tile";
                    return false;
                }
                if (n.Persistent && n.TileDataBytes != (long)n.Tiles.Count * tree.TileBytes)
                {
                    reason = $"node {n.Id} declares {n.TileDataBytes} tile byte(s) but carries {n.Tiles.Count} tile(s)";
                    return false;
                }
            }

            if (nodes != tree.NodeCount)
            {
                reason = $"parsed {nodes} node(s), header declares {tree.NodeCount}";
                return false;
            }

            if (subtiles != tree.SubtileCount)
            {
                reason = $"parsed {subtiles} subtile(s), header declares {tree.SubtileCount}";
                return false;
            }

            reason = "";
            return true;
        }

        private static MaskTree ParseMaskTree(ByteReader r, int strideLevel, ParseOptions options)
        {
            var t = new MaskTree { FittedStrideLevel = strideLevel };

            t.TileSamplesPerSide = r.ReadUInt32();
            t.TilesPerNodeSide = r.ReadUInt32();
            t.SubtileRelativeLevel = r.ReadUInt32();
            t.ResourceAtlasSampleCountX = r.ReadUInt32();
            t.ResourceAtlasSampleCountY = r.ReadUInt32();
            t.BlurrinessLog2 = r.ReadInt32();
            t.TreeCoverage = r.ReadBox2();
            t.NodeCount = r.ReadUInt32();
            t.SubtileCount = r.ReadUInt32();
            t.PersistentNonTrivialSubtileCount = r.ReadUInt32();

            // The coverage-grid level count (FIFA highResolutionCoverageGridStackLevelCount). BFII_EXACT.
            t.CoverageGridLevelCount = r.ReadUInt32();

            if (t.TileSamplesPerSide > 65536)
                throw new TerrainFormatException(
                    $"mask tileSamplesPerSide={t.TileSamplesPerSide} out of plausible range", r.Position);

            if (t.NodeCount > 0)
            {
                int parsed = 0;
                int budget = SafeBudget(t.NodeCount, options.NodeCountSlackFactor);
                t.Root = ParseMaskNode(r, t, new QuadtreeNodeId(0, 0, 0), t.TreeCoverage,
                                       ref parsed, budget, 0, options.MaxDepth);
            }

            return t;
        }

        private static MaskNode ParseMaskNode(ByteReader r, MaskTree t, QuadtreeNodeId id,
            AxisAlignedBox2 coverage, ref int parsed, int budget, int depth, int maxDepth)
        {
            if (++parsed > budget)
                throw new TerrainFormatException($"mask node budget {budget} exceeded", r.Position);
            if (depth > maxDepth)
                throw new TerrainFormatException($"mask depth {depth} exceeds {maxDepth}", r.Position);

            var n = new MaskNode
            {
                Id = id,
                NodeCoverage = coverage,
                RecordOffset = r.Position - r.WindowStart,
            };

            n.PrunedRaw = r.ReadByte();
            n.SubtileCount = r.ReadUInt16();
            n.NonTrivialSubtileCount = r.ReadUInt16();
            n.NonTrivialPersistentSubtileCountRecursive = r.ReadUInt16();

            // The reader emits nothing further for a pruned or empty node, and does not recurse.
            if (n.Pruned || n.SubtileCount == 0)
                return n;

            n.BodyPresent = true;
            int stride = t.Stride;

            for (int i = 0; i < n.SubtileCount; i++)
            {
                var s = new MaskSubtile();
                s.TerrainLayerIndex = r.ReadByte();
                s.WorldCoverage = r.ReadBox2();
                s.IsLeafRaw = r.ReadByte();
                s.MaskDataFullRaw = r.ReadByte();
                s.HighResolutionCoverageGridStack = r.ReadBytes(stride);
                n.Subtiles.Add(s);
            }

            n.PersistentRaw = r.ReadByte();
            if (n.Persistent)
            {
                // The tile byte count comes first, then the tiles (BFII_EXACT, 3,109 persistent nodes).
                n.TileDataBytes = r.ReadUInt32();
                int tileBytes = t.TileBytes;
                foreach (var s in n.Subtiles)
                    if (!s.IsTrivial)
                        n.Tiles.Add(r.ReadBytes(tileBytes));
            }

            n.HasChildrenRaw = r.ReadByte();
            if (n.HasChildren)
            {
                for (int i = 0; i < 4; i++)
                    n.Children.Add(ParseMaskNode(r, t, id.Child(i), ChildCoverage(coverage, i),
                                                 ref parsed, budget, depth + 1, maxDepth));
            }

            return n;
        }

        /// <summary>
        /// Halve the node coverage box into quadrant <paramref name="i"/>, matching the reference
        /// reader's arithmetic exactly. Coverage is derived, never serialised, so this affects only
        /// what a consumer sees — never round-trip identity.
        /// </summary>
        private static AxisAlignedBox2 ChildCoverage(AxisAlignedBox2 c, int i)
        {
            float minX = c.MinX.Value, minY = c.MinY.Value, maxX = c.MaxX.Value, maxY = c.MaxY.Value;
            float w = (maxX - minX) * 0.5f;
            float hgt = (maxY - minY) * 0.5f;
            int ox = QuadtreeNodeId.ChildOffsetX[i];
            int oy = QuadtreeNodeId.ChildOffsetY[i];

            AxisAlignedBox2 outBox;
            outBox.MinX = F32.FromValue(minX + w * ox);
            outBox.MaxX = F32.FromValue(maxX - w * (1.0f - ox));
            outBox.MinY = F32.FromValue(minY + hgt * oy);
            outBox.MaxY = F32.FromValue(maxY - hgt * (1.0f - oy));
            return outBox;
        }

        // -----------------------------------------------------------------------------------
        // Material tree (type 3)
        // -----------------------------------------------------------------------------------

        private static MaterialTree ParseMaterialTree(ByteReader r, ParseOptions options)
        {
            var t = new MaterialTree();

            t.NodeSamplesPerSide = r.ReadUInt32();
            t.BlurrinessLog2 = r.ReadInt32();
            t.TreeCoverage = r.ReadBox2();
            t.NodeCount = r.ReadUInt32();
            t.PersistentNodeCount = r.ReadUInt32();
            t.LevelMax = r.ReadUInt32();

            if (t.NodeSamplesPerSide > 65536)
                throw new TerrainFormatException(
                    $"material nodeSamplesPerSide={t.NodeSamplesPerSide} out of plausible range", r.Position);

            if (t.NodeCount > 0)
            {
                int parsed = 0;
                int budget = SafeBudget(t.NodeCount, options.NodeCountSlackFactor);
                t.Root = ParseMaterialNode(r, t, new QuadtreeNodeId(0, 0, 0),
                                           ref parsed, budget, 0, options.MaxDepth);
            }

            uint declCount = r.ReadUInt32();
            if (declCount > (uint)(r.Remaining / 4) + 1)
                throw new TerrainFormatException(
                    $"materialDeclCount={declCount} exceeds the {r.Remaining} byte(s) left in the record",
                    r.Position);

            for (uint i = 0; i < declCount; i++) t.MaterialDecls.Add(r.ReadUInt32());
            t.BackgroundMaterialDecl = r.ReadUInt32();

            return t;
        }

        private static MaterialNode ParseMaterialNode(ByteReader r, MaterialTree t, QuadtreeNodeId id,
            ref int parsed, int budget, int depth, int maxDepth)
        {
            if (++parsed > budget)
                throw new TerrainFormatException($"material node budget {budget} exceeded", r.Position);
            if (depth > maxDepth)
                throw new TerrainFormatException($"material depth {depth} exceeds {maxDepth}", r.Position);

            var n = new MaterialNode { Id = id, RecordOffset = r.Position - r.WindowStart };

            n.HasDataRaw = r.ReadByte();
            n.PersistentRaw = r.ReadByte();

            if (n.CarriesData)
            {
                // BFII only: one byte ahead of the RLE size (0 on every shipped data node).
                n.EncodingRaw = r.ReadByte();
                ReadRleBody(r, n, t.NodeSamplesPerSide, "material");
            }

            n.HasChildrenRaw = r.ReadByte();
            if (n.HasChildren)
                for (int i = 0; i < 4; i++)
                    n.Children.Add(ParseMaterialNode(r, t, id.Child(i),
                                                     ref parsed, budget, depth + 1, maxDepth));

            return n;
        }

        private static void ReadRleBody(ByteReader r, RleRasterNode n, uint lines, string what)
        {
            n.DeclaredRleSize = r.ReadInt32();
            if (n.DeclaredRleSize < 0 || n.DeclaredRleSize > r.Remaining)
                throw new TerrainFormatException(
                    $"{what} RLE size {n.DeclaredRleSize} exceeds the {r.Remaining} byte(s) left",
                    r.Position);

            n.RleData = r.ReadBytes(n.DeclaredRleSize);

            n.LineSizes = new ushort[lines];
            long sum = 0;
            for (int i = 0; i < lines; i++) { n.LineSizes[i] = r.ReadUInt16(); sum += n.LineSizes[i]; }

            // The line sizes partition the RLE bytes on every shipped node; a mismatch means the record
            // is misread, not merely unusual.
            if (sum != n.DeclaredRleSize)
                throw new TerrainFormatException(
                    $"{what} node {n.Id}: line sizes sum to {sum}, RLE size is {n.DeclaredRleSize}", r.Position);
        }

        // -----------------------------------------------------------------------------------
        // Colour tree (type 2)
        // -----------------------------------------------------------------------------------

        private static ColorTree ParseColorTree(ByteReader r, ParseOptions options)
        {
            var t = new ColorTree();
            t.TileSamplesPerSide = r.ReadUInt32();
            t.TileBorderWidth = r.ReadUInt32();
            t.ResourceAtlasSampleCountX = r.ReadUInt32();
            t.ResourceAtlasSampleCountY = r.ReadUInt32();
            t.Blurriness = r.ReadInt32();
            t.TreeCoverage = r.ReadBox2();
            t.TextureFormat = r.ReadUInt32();
            t.ChannelCount = r.ReadUInt32();
            t.NodeCount = r.ReadUInt32();
            t.PersistentNodeCount = r.ReadUInt32();

            if (t.TileSamplesPerSide > 65536)
                throw new TerrainFormatException(
                    $"colour tileSamplesPerSide={t.TileSamplesPerSide} out of plausible range", r.Position);
            if (ColorTree.BytesPerBlock(t.TextureFormat) == 0)
                throw new TerrainFormatException(
                    $"colour texture format {t.TextureFormat} is not one this reader knows (54 BC1, 60 BC3)", r.Position);

            if (t.NodeCount > 0)
            {
                int parsed = 0;
                int budget = SafeBudget(t.NodeCount, options.NodeCountSlackFactor);
                t.Root = ParseColorNode(r, t, new QuadtreeNodeId(0, 0, 0), ref parsed, budget, 0, options.MaxDepth);
            }
            return t;
        }

        private static ColorNode ParseColorNode(ByteReader r, ColorTree t, QuadtreeNodeId id,
            ref int parsed, int budget, int depth, int maxDepth)
        {
            if (++parsed > budget)
                throw new TerrainFormatException($"colour node budget {budget} exceeded", r.Position);
            if (depth > maxDepth)
                throw new TerrainFormatException($"colour depth {depth} exceeds {maxDepth}", r.Position);

            var n = new ColorNode { Id = id, RecordOffset = r.Position - r.WindowStart };
            n.SkippedRaw = r.ReadByte();
            n.HasDataRaw = r.ReadByte();
            if (!n.HasData && !n.Skipped) return n;

            n.TailRead = true;
            n.PersistentRaw = r.ReadByte();
            if (n.Persistent && n.HasData) n.Tile = r.ReadBytes(t.TileBytes);

            n.HasChildrenRaw = r.ReadByte();
            if (n.HasChildren)
                for (int i = 0; i < 4; i++)
                    n.Children.Add(ParseColorNode(r, t, id.Child(i), ref parsed, budget, depth + 1, maxDepth));
            return n;
        }

        // -----------------------------------------------------------------------------------
        // Destruction depth tree (type 4)
        // -----------------------------------------------------------------------------------

        private static DestructionDepthTree ParseDestructionDepthTree(ByteReader r, ParseOptions options)
        {
            var t = new DestructionDepthTree();
            t.NodeSamplesPerSide = r.ReadUInt32();
            t.BlurrinessLog2 = r.ReadInt32();
            t.TreeCoverage = r.ReadBox2();
            t.NodeCount = r.ReadUInt32();
            t.PersistentNodeCount = r.ReadUInt32();
            t.LevelMax = r.ReadUInt32();
            t.NodeBorderWidth = r.ReadUInt32();

            if (t.NodeSamplesPerSide > 65536)
                throw new TerrainFormatException(
                    $"destruction depth nodeSamplesPerSide={t.NodeSamplesPerSide} out of plausible range", r.Position);

            if (t.NodeCount > 0)
            {
                int parsed = 0;
                int budget = SafeBudget(t.NodeCount, options.NodeCountSlackFactor);
                t.Root = ParseDestructionDepthNode(r, t, new QuadtreeNodeId(0, 0, 0), ref parsed, budget, 0, options.MaxDepth);
            }
            return t;
        }

        private static DestructionDepthNode ParseDestructionDepthNode(ByteReader r, DestructionDepthTree t,
            QuadtreeNodeId id, ref int parsed, int budget, int depth, int maxDepth)
        {
            if (++parsed > budget)
                throw new TerrainFormatException($"destruction depth node budget {budget} exceeded", r.Position);
            if (depth > maxDepth)
                throw new TerrainFormatException($"destruction depth {depth} exceeds {maxDepth}", r.Position);

            var n = new DestructionDepthNode { Id = id, RecordOffset = r.Position - r.WindowStart };
            n.HasDataRaw = r.ReadByte();
            n.PersistentRaw = r.ReadByte();
            if (n.CarriesData) ReadRleBody(r, n, t.NodeSamplesPerSide, "destruction depth");

            n.HasChildrenRaw = r.ReadByte();
            if (n.HasChildren)
                for (int i = 0; i < 4; i++)
                    n.Children.Add(ParseDestructionDepthNode(r, t, id.Child(i), ref parsed, budget, depth + 1, maxDepth));
            return n;
        }

        // -----------------------------------------------------------------------------------
        // Streaming nodes
        // -----------------------------------------------------------------------------------

        private static StreamingNode ParseStreamingNode(ByteReader r, QuadtreeNodeId id,
            ref int parsed, int budget, int depth, int maxDepth)
        {
            if (++parsed > budget)
                throw new TerrainFormatException($"streaming node budget {budget} exceeded", r.Position);
            if (depth > maxDepth)
                throw new TerrainFormatException($"streaming depth {depth} exceeds {maxDepth}", r.Position);

            var n = new StreamingNode { Id = id, RecordOffset = r.Position - r.WindowStart };

            n.Lod0Size = r.ReadUInt32();
            n.Lod0Id = r.ReadGuid();

            n.Lod1EnabledRaw = r.ReadByte();
            if (n.Lod1Enabled)
            {
                n.Lod1Size = r.ReadUInt32();
                n.Lod1Guid = r.ReadGuid();
            }

            n.PersistentDedicatedServerRaw = r.ReadByte();
            n.HasChildrenRaw = r.ReadByte();

            if (n.HasChildren)
                for (int i = 0; i < 4; i++)
                    n.Children.Add(ParseStreamingNode(r, id.Child(i), ref parsed, budget,
                                                      depth + 1, maxDepth));

            return n;
        }
    }
}
