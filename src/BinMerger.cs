using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SHARED_TOOLS.ALL;
using SHARED_TOOLS.REPACK;
using SHARED_TOOLS.REPACK.Structures;
using SHARED_UHD_BIN_TPL.EXTRACT;
using SHARED_UHD_BIN_TPL.ALL;
using SHARED_UHD_BIN_TPL.REPACK.Structures;
using SHARED_UHD_BIN_TPL.REPACK;
using SimpleEndianBinaryIO;

namespace RE4BinMerge
{
    /// <summary>One input pair (BIN + its TPL) that will be merged.</summary>
    public class PartInput
    {
        public string Name;       // label used in the report (default: BIN file name)
        public string BinPath;
        public string TplPath;
        /// <summary>
        /// Bone ID used for vertices of this part when the part has NO weight map (rigid mesh)
        /// but another part does. Same idea as "ObjFileUseBone" in JADERLINK's idxuubin.
        /// </summary>
        public byte RigidBone = 0;
    }

    public class MergeOptions
    {
        /// <summary>Groups that end up with an identical material are joined in one group.</summary>
        public bool MergeSameMaterials = true;
        /// <summary>Put materials that have an opacity (alpha) texture at the end (draw order).</summary>
        public bool TransparentLast = false;
        /// <summary>Write a *_preview.obj built by re-reading the merged BIN.</summary>
        public bool ExportPreviewObj = true;
        /// <summary>
        /// The base game only accepts 255 weight-map combinations. If the merge goes above that, the weights are
        /// rounded in steps (5,10,20,25,50) until it fits. Turn off if you use the Qingsheng DLL (more bones memory).
        /// </summary>
        public bool LimitWeightMaps = true;
        /// <summary>
        /// Folder with the DDS / TGA texture files (only used by the GUI preview; the merge itself never touches textures).
        /// </summary>
        public string TexturesFolder;
    }

    public class MergeResult
    {
        public string BinPath;
        public string TplPath;
        public string ReportPath;
        public string ObjPath;
        public List<string> Warnings = new List<string>();
        public bool Success;
    }

    // ------------------------------------------------------------------

    internal class StripInfo
    {
        public ushort Type;
        public ushort Count;
    }

    internal class SourceGroup
    {
        public int PartIndex;
        public string PartName;
        public int OriginalIndex;       // material index inside the part
        public MaterialPart Material;   // material AFTER texture-index remap
        public List<StripInfo> Strips = new List<StripInfo>();
        public int VertexStart;         // inside the part vertex arrays
        public int VertexCount;
    }

    internal class SourcePart
    {
        public PartInput Input;
        public UhdBIN Bin;
        public UhdTPL Tpl;
        public List<SourceGroup> Groups = new List<SourceGroup>();
        public int[] TplMap;            // old tpl index -> merged tpl index
    }

    // ------------------------------------------------------------------

    public static class BinMerger
    {
        private const byte NoTexture = 0xFF;

        public static MergeResult Merge(List<PartInput> inputs, string outputBase, MergeOptions options, TextWriter log)
        {
            if (inputs == null || inputs.Count == 0) throw new ArgumentException("No parts to merge.");
            if (options == null) options = new MergeOptions();

            MergeResult result = new MergeResult();
            Action<string> warn = s => { result.Warnings.Add(s); log.WriteLine("WARNING: " + s); };

            // ---------- 1) read all parts ----------
            List<SourcePart> parts = new List<SourcePart>();
            for (int i = 0; i < inputs.Count; i++)
            {
                parts.Add(ReadPart(inputs[i], i, log));
            }

            // ---------- 2) merge TPL ----------
            List<TplInfo> mergedTpl = new List<TplInfo>();
            foreach (var p in parts)
            {
                p.TplMap = new int[p.Tpl.TplArray.Length];
                for (int t = 0; t < p.Tpl.TplArray.Length; t++)
                {
                    TplInfo info = p.Tpl.TplArray[t];
                    int found = mergedTpl.FindIndex(x => x.PackID == info.PackID && x.TextureID == info.TextureID);
                    if (found < 0)
                    {
                        mergedTpl.Add(info);
                        found = mergedTpl.Count - 1;
                    }
                    p.TplMap[t] = found;
                }
            }
            if (mergedTpl.Count > 255)
            {
                warn("Merged TPL has " + mergedTpl.Count + " textures; a material can only address 0-254. Textures above 254 will not be reachable.");
            }

            // ---------- 3) remap material texture indexes ----------
            foreach (var p in parts)
            {
                foreach (var g in p.Groups)
                {
                    g.Material = RemapMaterial(g.Material, p, g.PartName, g.OriginalIndex, warn);
                }
            }

            // ---------- 4) bones ----------
            // RE4 parts share ONE global skeleton by BoneID, but partial skeletons (hands, head) use their own
            // local root (parent 255) and positions. So: first part that defines a BoneID wins, no per-bone spam.
            List<Bone> mergedBones = new List<Bone>();
            int boneDiffs = 0;
            foreach (var p in parts)
            {
                foreach (var b in p.Bin.Bones)
                {
                    int ex = mergedBones.FindIndex(x => x.BoneID == b.BoneID);
                    if (ex < 0)
                    {
                        mergedBones.Add(b);
                    }
                    else
                    {
                        Bone o = mergedBones[ex];
                        if (o.BoneParent != b.BoneParent && b.BoneParent != 255)
                        {
                            boneDiffs++;
                        }
                    }
                }
            }
            if (boneDiffs > 0)
            {
                warn(boneDiffs + " bone(s) have a different parent in a later part than in the first part that defines them. The first definition was kept (put the full-body BIN first).");
            }
            if (mergedBones.Count > 254) warn("Merged skeleton has " + mergedBones.Count + " bones (ids above 254 are invalid).");

            // bone pairs / flags
            bool useWeightMap = parts.Any(p => p.Bin.WeightMaps != null && p.Bin.WeightIndex != null && p.Bin.WeightIndex.Length != 0);
            bool useColors = parts.Any(p => p.Bin.Header.vertex_colour_offset != 0 && p.Bin.Vertex_Color_Array.Length != 0);
            bool altNormals = parts.Any(p => p.Bin.Header.ReturnsHasNormalsAlternativeTag());
            bool bonepairTag = parts.Any(p => p.Bin.Header.ReturnsHasEnableBonepairTag());
            bool adjacentTag = parts.Any(p => p.Bin.Header.ReturnsHasEnableAdjacentBoneTag());

            List<(ushort b1, ushort b2, ushort b3, ushort b4)> bonePairs = new List<(ushort b1, ushort b2, ushort b3, ushort b4)>();
            foreach (var p in parts)
            {
                foreach (var bp in p.Bin.BonePairs)
                {
                    var t = (bp.Bone1, bp.Bone2, bp.Bone3, bp.Bone4);
                    if (!bonePairs.Contains(t)) bonePairs.Add(t);
                }
            }

            // ---------- 5) order groups ----------
            List<SourceGroup> all = parts.SelectMany(p => p.Groups).ToList();

            // each output group = list of source groups sharing one material
            List<List<SourceGroup>> outGroups = new List<List<SourceGroup>>();
            foreach (var g in all)
            {
                List<SourceGroup> target = null;
                if (options.MergeSameMaterials)
                {
                    target = outGroups.FirstOrDefault(x => x[0].Material.Equals(g.Material));
                }
                if (target == null)
                {
                    target = new List<SourceGroup>();
                    outGroups.Add(target);
                }
                target.Add(g);
            }
            if (options.TransparentLast)
            {
                outGroups = outGroups.Where(x => x[0].Material.opacity_map == NoTexture)
                            .Concat(outGroups.Where(x => x[0].Material.opacity_map != NoTexture)).ToList();
            }

            // ---------- 6) build FinalStructure ----------
            var pos = new List<(float vx, float vy, float vz)>();
            var nor = new List<(float nx, float ny, float nz)>();
            var uv = new List<(float tu, float tv)>();
            var col = new List<(byte a, byte r, byte g, byte b)>();
            var weightMaps = new List<FinalWeightMap>();
            var weightIndex = new List<ushort>();
            var perVertexWeights = new List<FinalWeightMap>();
            var finalGroups = new List<FinalMaterialGroup>();
            IdxMaterial idxMaterial = new IdxMaterial();
            idxMaterial.MaterialDic = new Dictionary<string, MaterialPart>();

            List<string> groupNames = new List<string>();

            for (int gi = 0; gi < outGroups.Count; gi++)
            {
                var members = outGroups[gi];
                string name = "Material_" + gi.ToString("D3");
                groupNames.Add(name);
                idxMaterial.MaterialDic[name] = members[0].Material;

                FinalMaterialGroup fg = new FinalMaterialGroup();
                fg.materialName = name;
                List<FinalFace> faces = new List<FinalFace>();

                foreach (var sg in members)
                {
                    SourcePart sp = parts[sg.PartIndex];

                    foreach (var s in sg.Strips)
                    {
                        faces.Add(new FinalFace { Type = s.Type, Count = s.Count });
                    }

                    bool partHasWeights = sp.Bin.WeightMaps != null && sp.Bin.WeightIndex != null && sp.Bin.WeightIndex.Length >= sg.VertexStart + sg.VertexCount;
                    bool partHasColors = sp.Bin.Vertex_Color_Array != null && sp.Bin.Vertex_Color_Array.Length >= sg.VertexStart + sg.VertexCount;

                    for (int v = sg.VertexStart; v < sg.VertexStart + sg.VertexCount; v++)
                    {
                        pos.Add(sp.Bin.Vertex_Position_Array[v]);
                        nor.Add(sp.Bin.Vertex_Normal_Array[v]);
                        uv.Add(sp.Bin.Vertex_UV_Array[v]);

                        if (useColors)
                        {
                            col.Add(partHasColors ? sp.Bin.Vertex_Color_Array[v] : ((byte)255, (byte)255, (byte)255, (byte)255));
                        }

                        if (useWeightMap)
                        {
                            FinalWeightMap fw;
                            if (partHasWeights)
                            {
                                WeightMap w = sp.Bin.WeightMaps[sp.Bin.WeightIndex[v]];
                                fw = new FinalWeightMap(w.count, w.boneId1, w.weight1, w.boneId2, w.weight2, w.boneId3, w.weight3);
                            }
                            else
                            {
                                fw = new FinalWeightMap(1, sp.Input.RigidBone, 100, 0, 0, 0, 0);
                            }

                            perVertexWeights.Add(fw);
                        }
                    }
                }

                fg.Mesh = faces.ToArray();
                finalGroups.Add(fg);
            }

            if (useWeightMap)
            {
                BuildWeightMaps(perVertexWeights, weightMaps, weightIndex);
                if (weightMaps.Count > 255 && options.LimitWeightMaps)
                {
                    int before = weightMaps.Count;
                    foreach (int step in new[] { 5, 10, 20, 25, 50 })
                    {
                        var q = perVertexWeights.Select(w => QuantizeWeight(w, step)).ToList();
                        weightMaps.Clear(); weightIndex.Clear();
                        BuildWeightMaps(q, weightMaps, weightIndex);
                        if (weightMaps.Count <= 255)
                        {
                            warn("Weight maps reduced from " + before + " to " + weightMaps.Count + " by rounding vertex weights to steps of " + step + "% (the base game accepts only 255).");
                            break;
                        }
                    }
                    if (weightMaps.Count > 255)
                    {
                        warn("Could not reduce weight maps to 255 (now " + weightMaps.Count + ").");
                    }
                }
            }

            if (pos.Count > ushort.MaxValue - 1)
            {
                warn("Merged BIN has " + pos.Count + " vertices; the limit for normal (non-scenario) BIN files is 65534.");
            }
            if (useWeightMap && weightMaps.Count > 255)
            {
                warn("Merged BIN has " + weightMaps.Count + " weight-map combinations; the base game only accepts 255 (more needs the Qingsheng DLL 'Allocate more memory for bones').");
            }

            FinalStructure final = new FinalStructure();
            final.Vertex_Position_Array = pos.ToArray();
            final.Vertex_Normal_Array = nor.ToArray();
            final.Vertex_UV_Array = uv.ToArray();
            final.Vertex_Color_Array = useColors ? col.ToArray() : new (byte a, byte r, byte g, byte b)[0];
            final.WeightMaps = useWeightMap ? weightMaps.ToArray() : new FinalWeightMap[0];
            final.WeightIndex = useWeightMap ? weightIndex.ToArray() : new ushort[0];
            final.Groups = finalGroups.ToArray();

            FinalBoneLine[] boneLines = mergedBones
                .Select(b => new FinalBoneLine(b.BoneID, b.BoneParent, b.PositionX, b.PositionY, b.PositionZ, Endianness.LittleEndian))
                .ToArray();

            // ---------- 7) write BIN + TPL ----------
            string dir = Path.GetDirectoryName(Path.GetFullPath(outputBase));
            string baseName = Path.GetFileName(outputBase);
            Directory.CreateDirectory(dir);

            result.BinPath = Path.Combine(dir, baseName + ".bin");
            result.TplPath = Path.Combine(dir, baseName + ".tpl");
            result.ReportPath = Path.Combine(dir, baseName + "_merge_report.txt");

            using (FileStream fs = new FileStream(result.BinPath, FileMode.Create, FileAccess.ReadWrite))
            {
                BINmakeFile.MakeFile(fs, 0, out _, final, boneLines, idxMaterial, bonePairs.ToArray(),
                    altNormals, useWeightMap, bonepairTag && bonePairs.Count != 0, adjacentTag, useColors,
                    false, Endianness.LittleEndian);
            }

            UhdTPL outTpl = new UhdTPL();
            outTpl.TplArray = mergedTpl.ToArray();
            using (FileStream fs = new FileStream(result.TplPath, FileMode.Create, FileAccess.ReadWrite))
            {
                TPLmakeFile.MakeFile(outTpl, fs, 0, out _, false, Endianness.LittleEndian);
            }

            // ---------- 8) verify by reading it back ----------
            UhdBIN check;
            UhdTPL checkTpl;
            using (FileStream fs = new FileStream(result.BinPath, FileMode.Open, FileAccess.Read))
            {
                check = UhdBinDecoder.Decoder(fs, 0, out _, false, Endianness.LittleEndian);
            }
            using (FileStream fs = new FileStream(result.TplPath, FileMode.Open, FileAccess.Read))
            {
                checkTpl = UhdTplDecoder.Decoder(fs, 0, out _, false, Endianness.LittleEndian);
            }

            bool ok = true;
            if (check.Vertex_Position_Array.Length != pos.Count)
            {
                warn("Verification: vertex count mismatch (" + check.Vertex_Position_Array.Length + " read, " + pos.Count + " written).");
                ok = false;
            }
            if (check.Materials.Length != outGroups.Count)
            {
                warn("Verification: material count mismatch.");
                ok = false;
            }
            if (checkTpl.TplArray.Length != mergedTpl.Count)
            {
                warn("Verification: TPL count mismatch.");
                ok = false;
            }
            result.Success = ok;

            // ---------- 9) report / preview ----------
            WriteReport(result, parts, outGroups, mergedTpl, mergedBones, bonePairs.Count, check, useWeightMap, useColors, options);

            if (options.ExportPreviewObj)
            {
                result.ObjPath = Path.Combine(dir, baseName + "_preview.obj");
                WritePreviewObj(result.ObjPath, check, groupNames);
            }

            return result;
        }

        private static void BuildWeightMaps(List<FinalWeightMap> perVertex, List<FinalWeightMap> maps, List<ushort> index)
        {
            Dictionary<FinalWeightMap, int> lookup = new Dictionary<FinalWeightMap, int>();
            foreach (var w in perVertex)
            {
                int wi;
                if (!lookup.TryGetValue(w, out wi))
                {
                    wi = maps.Count;
                    maps.Add(w);
                    lookup[w] = wi;
                }
                index.Add((ushort)wi);
            }
        }

        private static FinalWeightMap QuantizeWeight(FinalWeightMap w, int step)
        {
            int n = Math.Max(1, Math.Min(3, (int)w.Links));
            int[] ids = { w.BoneID1, w.BoneID2, w.BoneID3 };
            int[] ws = { w.Weight1, w.Weight2, w.Weight3 };

            List<int> oi = new List<int>();
            List<int> ow = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int q = (int)Math.Round(ws[i] / (double)step) * step;
                if (q > 0) { oi.Add(ids[i]); ow.Add(q); }
            }
            if (oi.Count == 0) { oi.Add(ids[0]); ow.Add(100); }

            int diff = 100 - ow.Sum();
            int big = ow.IndexOf(ow.Max());
            ow[big] += diff;

            while (oi.Count < 3) { oi.Add(0); ow.Add(0); }
            int links = Math.Max(1, ow.Take(3).Count(x => x > 0));
            return new FinalWeightMap((byte)links, (byte)oi[0], (byte)ow[0], (byte)oi[1], (byte)ow[1], (byte)oi[2], (byte)ow[2]);
        }

        // ------------------------------------------------------------------
        // Reading
        // ------------------------------------------------------------------

        private static SourcePart ReadPart(PartInput input, int index, TextWriter log)
        {
            if (string.IsNullOrEmpty(input.Name)) input.Name = Path.GetFileName(input.BinPath);

            SourcePart part = new SourcePart();
            part.Input = input;

            if (!File.Exists(input.BinPath)) throw new FileNotFoundException("BIN not found: " + input.BinPath);
            if (string.IsNullOrEmpty(input.TplPath) || !File.Exists(input.TplPath))
                throw new FileNotFoundException("TPL not found for '" + input.Name + "': " + input.TplPath);

            using (FileStream fs = new FileStream(input.BinPath, FileMode.Open, FileAccess.Read))
            {
                part.Bin = UhdBinDecoder.Decoder(fs, 0, out _, false, Endianness.LittleEndian);

                // second pass: keep the strip structure (type/count), the decoder only keeps triangles
                part.Groups = ReadGroups(fs, part.Bin.Header, index, input.Name);
            }

            using (FileStream fs = new FileStream(input.TplPath, FileMode.Open, FileAccess.Read))
            {
                part.Tpl = UhdTplDecoder.Decoder(fs, 0, out _, false, Endianness.LittleEndian);
            }

            int sum = part.Groups.Sum(g => g.VertexCount);
            if (sum != part.Bin.Vertex_Position_Array.Length)
            {
                throw new InvalidDataException("'" + input.Name + "': strip vertex count (" + sum + ") does not match the vertex array (" + part.Bin.Vertex_Position_Array.Length + ").");
            }

            log.WriteLine("Read " + input.Name + ": " + part.Bin.Vertex_Position_Array.Length + " vertices, "
                + part.Groups.Count + " materials, " + part.Bin.Bones.Length + " bones, TPL entries: " + part.Tpl.TplArray.Length);
            return part;
        }

        private static List<SourceGroup> ReadGroups(Stream stream, UhdBinHeader header, int partIndex, string partName)
        {
            List<SourceGroup> list = new List<SourceGroup>();
            BinaryReader br = new BinaryReader(stream);
            br.BaseStream.Position = header.material_offset;

            int vertexPos = 0;
            for (int i = 0; i < header.material_count; i++)
            {
                SourceGroup g = new SourceGroup();
                g.PartIndex = partIndex;
                g.PartName = partName;
                g.OriginalIndex = i;

                byte[] m = br.ReadBytes(24);
                MaterialPart mat = new MaterialPart();
                mat.unk_min_11 = m[0]; mat.unk_min_10 = m[1]; mat.unk_min_09 = m[2]; mat.unk_min_08 = m[3];
                mat.unk_min_07 = m[4]; mat.unk_min_06 = m[5]; mat.unk_min_05 = m[6]; mat.unk_min_04 = m[7];
                mat.unk_min_03 = m[8]; mat.unk_min_02 = m[9]; mat.unk_min_01 = m[10]; mat.material_flag = m[11];
                mat.diffuse_map = m[12]; mat.bump_map = m[13]; mat.opacity_map = m[14]; mat.generic_specular_map = m[15];
                mat.intensity_specular_r = m[16]; mat.intensity_specular_g = m[17]; mat.intensity_specular_b = m[18];
                mat.unk_08 = m[19]; mat.unk_09 = m[20]; mat.specular_scale = m[21]; mat.unk_11 = m[22]; mat.custom_specular_map = m[23];
                g.Material = mat;

                uint bufferSize = br.ReadUInt32();
                br.ReadUInt32(); // total count (unused)
                long tmp = br.BaseStream.Position;
                uint stripCount = br.ReadUInt32();

                g.VertexStart = vertexPos;
                for (int s = 0; s < stripCount; s++)
                {
                    ushort type = br.ReadUInt16();
                    ushort count = br.ReadUInt16();
                    int used = VerticesUsed(type, count);
                    vertexPos += used;
                    g.VertexCount += used;
                    g.Strips.Add(new StripInfo { Type = type, Count = (ushort)used });
                }
                br.BaseStream.Position = tmp + bufferSize;
                list.Add(g);
            }
            return list;
        }

        // mirrors the index consumption of UhdBinDecoder.Get_face_index_parts
        private static int VerticesUsed(ushort type, ushort count)
        {
            switch (type)
            {
                case 5: return (count / 3) * 3;
                case 6: return count < 3 ? 3 : count;
                case 7: return count;
                case 8: return (count / 4) * 4;
                default: return 0;
            }
        }

        // ------------------------------------------------------------------
        // Materials
        // ------------------------------------------------------------------

        private static MaterialPart RemapMaterial(MaterialPart src, SourcePart part, string partName, int matIndex, Action<string> warn)
        {
            MaterialPart m = new MaterialPart();
            m.unk_min_11 = src.unk_min_11; m.unk_min_10 = src.unk_min_10; m.unk_min_09 = src.unk_min_09; m.unk_min_08 = src.unk_min_08;
            m.unk_min_07 = src.unk_min_07; m.unk_min_06 = src.unk_min_06; m.unk_min_05 = src.unk_min_05; m.unk_min_04 = src.unk_min_04;
            m.unk_min_03 = src.unk_min_03; m.unk_min_02 = src.unk_min_02; m.unk_min_01 = src.unk_min_01; m.material_flag = src.material_flag;
            m.intensity_specular_r = src.intensity_specular_r; m.intensity_specular_g = src.intensity_specular_g;
            m.intensity_specular_b = src.intensity_specular_b; m.unk_08 = src.unk_08; m.unk_09 = src.unk_09;
            m.specular_scale = src.specular_scale; m.unk_11 = src.unk_11;

            string where = partName + " material " + matIndex;
            m.diffuse_map = Remap(src.diffuse_map, part, where + " (diffuse)", warn);
            m.bump_map = Remap(src.bump_map, part, where + " (bump)", warn);
            m.opacity_map = Remap(src.opacity_map, part, where + " (opacity)", warn);
            m.generic_specular_map = Remap(src.generic_specular_map, part, where + " (specular)", warn);
            m.custom_specular_map = Remap(src.custom_specular_map, part, where + " (custom specular)", warn);
            return m;
        }

        private static byte Remap(byte value, SourcePart part, string where, Action<string> warn)
        {
            if (value == NoTexture) return NoTexture;
            if (value >= part.TplMap.Length)
            {
                warn(where + ": texture index " + value + " is not in the TPL given for this part (has " + part.TplMap.Length + " entries). Set to 'no texture'. Is it the right TPL?");
                return NoTexture;
            }
            int n = part.TplMap[value];
            if (n > 254)
            {
                warn(where + ": new texture index " + n + " is above 254, set to 'no texture'.");
                return NoTexture;
            }
            return (byte)n;
        }

        // ------------------------------------------------------------------
        // Output helpers
        // ------------------------------------------------------------------

        private static string Tex(byte v) { return v == NoTexture ? "--" : v.ToString("D3"); }

        private static void WriteReport(MergeResult result, List<SourcePart> parts, List<List<SourceGroup>> outGroups,
            List<TplInfo> mergedTpl, List<Bone> bones, int bonePairCount, UhdBIN check, bool useWeightMap, bool useColors, MergeOptions options)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("RE4 UHD BIN/TPL MERGE REPORT");
            sb.AppendLine("============================");
            sb.AppendLine("Output BIN : " + Path.GetFileName(result.BinPath));
            sb.AppendLine("Output TPL : " + Path.GetFileName(result.TplPath));
            sb.AppendLine();

            sb.AppendLine("INPUT PARTS");
            sb.AppendLine("-----------");
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                bool w = p.Bin.WeightMaps != null && p.Bin.WeightIndex.Length != 0;
                sb.AppendLine(string.Format("[{0}] BIN {1} + TPL {2}", i, Path.GetFileName(p.Input.BinPath), Path.GetFileName(p.Input.TplPath)));
                sb.AppendLine(string.Format("     vertices {0}, materials {1}, bones {2}, weighted {3}{4}",
                    p.Bin.Vertex_Position_Array.Length, p.Groups.Count, p.Bin.Bones.Length, w ? "yes" : "no",
                    w ? "" : " (rigid bone " + p.Input.RigidBone + ")"));
            }
            sb.AppendLine();

            sb.AppendLine("MERGED TPL  (new index -> PackID / TextureID, and where it came from)");
            sb.AppendLine("--------------------------------------------------------------------");
            for (int i = 0; i < mergedTpl.Count; i++)
            {
                var t = mergedTpl[i];
                List<string> from = new List<string>();
                for (int pi = 0; pi < parts.Count; pi++)
                {
                    for (int k = 0; k < parts[pi].TplMap.Length; k++)
                    {
                        if (parts[pi].TplMap[k] == i) from.Add("part" + pi + ":old" + k);
                    }
                }
                sb.AppendLine(string.Format("TPL {0:D3}  Pack {1:X8}  Tex {2:D4}  {3}x{4}   <- {5}",
                    i, t.PackID, t.TextureID, t.Width, t.Height, string.Join(", ", from)));
            }
            sb.AppendLine();

            sb.AppendLine("MERGED BIN LAYOUT");
            sb.AppendLine("-----------------");
            sb.AppendLine("Vertices        : " + check.Vertex_Position_Array.Length);
            sb.AppendLine("Materials       : " + check.Materials.Length + (options.MergeSameMaterials ? " (identical materials joined)" : ""));
            sb.AppendLine("Bones           : " + bones.Count);
            sb.AppendLine("Bone pairs      : " + bonePairCount);
            sb.AppendLine("Weight maps     : " + (useWeightMap ? (check.WeightMaps != null ? check.WeightMaps.Length : 0).ToString() : "none"));
            sb.AppendLine("Vertex colors   : " + (useColors ? "yes" : "no"));
            sb.AppendLine();
            sb.AppendLine("Order  Diff Bump Opac Spec CSpc  Triangles  Contains");
            for (int i = 0; i < outGroups.Count; i++)
            {
                var m = outGroups[i][0].Material;
                int tris = check.Materials[i].face_index_array.Length;
                string contains = string.Join(" + ", outGroups[i].Select(g => "part" + g.PartIndex + "/mat" + g.OriginalIndex));
                sb.AppendLine(string.Format("{0,5}  {1,4} {2,4} {3,4} {4,4} {5,4}  {6,9}  {7}",
                    i, Tex(m.diffuse_map), Tex(m.bump_map), Tex(m.opacity_map), Tex(m.generic_specular_map), Tex(m.custom_specular_map), tris, contains));
            }
            sb.AppendLine();

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("WARNINGS");
                sb.AppendLine("--------");
                foreach (var w in result.Warnings) sb.AppendLine("* " + w);
            }
            else
            {
                sb.AppendLine("No warnings.");
            }

            File.WriteAllText(result.ReportPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static void WritePreviewObj(string path, UhdBIN bin, List<string> groupNames)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# merged BIN preview (re-read from the generated BIN)");

            for (int i = 0; i < bin.Vertex_Position_Array.Length; i++)
            {
                var p = bin.Vertex_Position_Array[i];
                sb.AppendLine("v " + p.vx.ToString("0.######", ci) + " " + p.vy.ToString("0.######", ci) + " " + p.vz.ToString("0.######", ci));
            }
            for (int i = 0; i < bin.Vertex_UV_Array.Length; i++)
            {
                var t = bin.Vertex_UV_Array[i];
                sb.AppendLine("vt " + t.tu.ToString("0.######", ci) + " " + (1f - t.tv).ToString("0.######", ci));
            }
            for (int i = 0; i < bin.Vertex_Normal_Array.Length; i++)
            {
                var n = bin.Vertex_Normal_Array[i];
                sb.AppendLine("vn " + n.nx.ToString("0.######", ci) + " " + n.ny.ToString("0.######", ci) + " " + n.nz.ToString("0.######", ci));
            }

            for (int g = 0; g < bin.Materials.Length; g++)
            {
                string name = g < groupNames.Count ? groupNames[g] : "Material_" + g.ToString("D3");
                sb.AppendLine("g " + name);
                sb.AppendLine("usemtl " + name);
                foreach (var f in bin.Materials[g].face_index_array)
                {
                    int a = f.i1 + 1, b = f.i2 + 1, c = f.i3 + 1;
                    sb.AppendLine("f " + a + "/" + a + "/" + a + " " + b + "/" + b + "/" + b + " " + c + "/" + c + "/" + c);
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
