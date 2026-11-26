using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SHARED_TOOLS.ALL;
using SHARED_UHD_BIN_TPL.EXTRACT;
using SimpleEndianBinaryIO;

namespace RE4BinMerge
{
    /// <summary>Plain triangle mesh used by the GUI preview and by the screenshot exporter.</summary>
    public class MeshData
    {
        public string Name;
        public float[] Pos;       // x,y,z per vertex
        public float[] UV;        // u,v per vertex (v = 0 is the top row of the texture)
        public int[] Tri;         // 3 indexes per triangle
        public int[] TriGroup;    // material index per triangle
        public int GroupCount;
        public int VertexCount { get { return Pos.Length / 3; } }
        public int TriCount { get { return Tri.Length / 3; } }

        // per material: which texture of the TPL it uses (PackID/TextureID), or "none"
        public bool[] HasDiffuse, HasOpacity;
        public uint[] DiffusePack, DiffuseId, OpacityPack, OpacityId;

        // filled by Bind(): the decoded textures (null = not found / not loaded)
        public TexImage[] Diffuse, Opacity;

        /// <summary>Looks up the texture files of every material in the store.</summary>
        public void Bind(TextureStore store)
        {
            Diffuse = new TexImage[GroupCount];
            Opacity = new TexImage[GroupCount];
            if (store == null || !store.HasFolder) return;
            for (int g = 0; g < GroupCount; g++)
            {
                if (HasDiffuse[g]) Diffuse[g] = store.Get(DiffusePack[g], DiffuseId[g]);
                if (HasOpacity[g]) Opacity[g] = store.Get(OpacityPack[g], OpacityId[g]);
            }
        }

        /// <summary>Distinct (pack, texture id) pairs this mesh asks for.</summary>
        public List<KeyValuePair<uint, uint>> UsedTextures()
        {
            HashSet<ulong> seen = new HashSet<ulong>();
            List<KeyValuePair<uint, uint>> list = new List<KeyValuePair<uint, uint>>();
            for (int g = 0; g < GroupCount; g++)
            {
                if (HasDiffuse[g] && seen.Add(TextureStore.Key(DiffusePack[g], DiffuseId[g])))
                    list.Add(new KeyValuePair<uint, uint>(DiffusePack[g], DiffuseId[g]));
                if (HasOpacity[g] && seen.Add(TextureStore.Key(OpacityPack[g], OpacityId[g])))
                    list.Add(new KeyValuePair<uint, uint>(OpacityPack[g], OpacityId[g]));
            }
            return list;
        }
    }

    public static class BinPreview
    {
        /// <summary>Loads a BIN as a triangle mesh. With the TPL given, each material also knows its texture.</summary>
        public static MeshData Load(string binPath, string tplPath = null)
        {
            UhdBIN bin;
            using (FileStream fs = new FileStream(binPath, FileMode.Open, FileAccess.Read))
            {
                bin = UhdBinDecoder.Decoder(fs, 0, out _, false, Endianness.LittleEndian);
            }
            UhdTPL tpl = null;
            if (!string.IsNullOrEmpty(tplPath) && File.Exists(tplPath))
            {
                try
                {
                    using (FileStream fs = new FileStream(tplPath, FileMode.Open, FileAccess.Read))
                        tpl = UhdTplDecoder.Decoder(fs, 0, out _, false, Endianness.LittleEndian);
                }
                catch (Exception) { tpl = null; }
            }

            MeshData m = new MeshData();
            m.Name = Path.GetFileName(binPath);
            var p = bin.Vertex_Position_Array;
            m.Pos = new float[p.Length * 3];
            m.UV = new float[p.Length * 2];
            var uv = bin.Vertex_UV_Array;
            for (int i = 0; i < p.Length; i++)
            {
                m.Pos[i * 3] = p[i].vx;
                m.Pos[i * 3 + 1] = p[i].vy;
                m.Pos[i * 3 + 2] = p[i].vz;
                if (uv != null && i < uv.Length) { m.UV[i * 2] = uv[i].tu; m.UV[i * 2 + 1] = uv[i].tv; }
            }

            int gc = bin.Materials.Length;
            m.HasDiffuse = new bool[gc]; m.HasOpacity = new bool[gc];
            m.DiffusePack = new uint[gc]; m.DiffuseId = new uint[gc];
            m.OpacityPack = new uint[gc]; m.OpacityId = new uint[gc];

            List<int> tri = new List<int>();
            List<int> grp = new List<int>();
            for (int g = 0; g < gc; g++)
            {
                foreach (var f in bin.Materials[g].face_index_array)
                {
                    tri.Add(f.i1); tri.Add(f.i2); tri.Add(f.i3);
                    grp.Add(g);
                }
                if (tpl != null)
                {
                    MaterialPart mp = bin.Materials[g].material;
                    if (mp.diffuse_map != 0xFF && mp.diffuse_map < tpl.TplArray.Length)
                    {
                        m.HasDiffuse[g] = true;
                        m.DiffusePack[g] = tpl.TplArray[mp.diffuse_map].PackID;
                        m.DiffuseId[g] = tpl.TplArray[mp.diffuse_map].TextureID;
                    }
                    if (mp.opacity_map != 0xFF && mp.opacity_map < tpl.TplArray.Length)
                    {
                        m.HasOpacity[g] = true;
                        m.OpacityPack[g] = tpl.TplArray[mp.opacity_map].PackID;
                        m.OpacityId[g] = tpl.TplArray[mp.opacity_map].TextureID;
                    }
                }
            }
            m.Tri = tri.ToArray();
            m.TriGroup = grp.ToArray();
            m.GroupCount = gc;
            m.Diffuse = new TexImage[gc];
            m.Opacity = new TexImage[gc];
            return m;
        }

        /// <summary>Writes an OBJ of one BIN (used for "before" pictures / quick look).</summary>
        public static void ExportObj(string binPath, string objPath)
        {
            MeshData m = Load(binPath);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            using (StreamWriter w = new StreamWriter(objPath))
            {
                for (int i = 0; i < m.VertexCount; i++)
                    w.WriteLine("v " + m.Pos[i * 3].ToString("0.###", ci) + " " + m.Pos[i * 3 + 1].ToString("0.###", ci) + " " + m.Pos[i * 3 + 2].ToString("0.###", ci));
                int cur = -1;
                for (int t = 0; t < m.TriCount; t++)
                {
                    if (m.TriGroup[t] != cur) { cur = m.TriGroup[t]; w.WriteLine("g mat" + cur.ToString("D3")); }
                    w.WriteLine("f " + (m.Tri[t * 3] + 1) + " " + (m.Tri[t * 3 + 1] + 1) + " " + (m.Tri[t * 3 + 2] + 1));
                }
            }
        }
    }

    /// <summary>
    /// Reads the "BIN - TPL" list files (like the ones in BIN_-_TPL_list_by__BN_LOS.zip):
    ///   TPL - 001
    ///   BIN - 000 - Body
    /// </summary>
    public static class TplGuess
    {
        /// <summary>
        /// Guess the TPL of a BIN from numbers only (used when the list does not say it):
        /// n+1, then n-1, then the nearest lower TPL (head/neck/hands that follow their body BIN).
        /// Only a guess: the GUI marks it so the user can check it.
        /// </summary>
        public static string ByNumber(int bin, IDictionary<int, string> tpls)
        {
            string p;
            if (tpls.TryGetValue(bin + 1, out p)) return p;
            if (tpls.TryGetValue(bin - 1, out p)) return p;
            for (int k = bin - 2; k >= 0 && k >= bin - 40; k--)
            {
                if (tpls.TryGetValue(k, out p)) return p;
            }
            return null;
        }
    }

    public class ListEntry
    {
        public int Bin;
        public int Tpl = -1;       // -1 = the list does not say which TPL
        public string Description;
        public string TplNote;     // e.g. "Explosive Variation"
    }

    /// <summary>
    /// Reads the BIN/TPL list files. Two formats are understood:
    /// 1) "BIN - TPL" lists:          TPL - 001  /  BIN - 000 - Body
    /// 2) the enemy list from the forum (saved page .html/.mhtml/.txt):
    ///        em2b | Gigante
    ///        001.bin - Body ]-> 003.tpl
    ///        002.bin - Head
    ///        Both Files Above Use -> 003.tpl
    /// Result: section name (em2b, or "" for format 1) -> entries.
    /// </summary>
    public static class ListParser
    {
        public static List<ListEntry> Parse(string path)
        {
            var all = ParseAny(path);
            List<ListEntry> list = new List<ListEntry>();
            foreach (var kv in all) list.AddRange(kv.Value);
            return list;
        }

        public static List<KeyValuePair<string, List<ListEntry>>> ParseAny(string path)
        {
            string text = File.ReadAllText(path);
            bool html = text.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0;
            if (html)
            {
                text = Regex.Replace(text, @"<(script|style)[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"</(p|div|li|tr|h\d)>", "\n", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"<[^>]+>", "");
                text = System.Net.WebUtility.HtmlDecode(text);
            }
            string[] lines = text.Replace("\r", "").Split('\n');

            // format 1 has lines like "BIN - 000 - Body"
            bool format1 = lines.Any(l => Regex.IsMatch(l.Trim(), @"^BIN\s*-\s*\d+", RegexOptions.IgnoreCase));
            return format1 ? ParseFormat1(lines) : ParseEnemyFormat(lines);
        }

        private static List<KeyValuePair<string, List<ListEntry>>> ParseFormat1(string[] lines)
        {
            List<ListEntry> list = new List<ListEntry>();
            int tpl = -1;
            string note = null;
            bool lastWasBin = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string up = line.ToUpperInvariant();
                string[] f = line.Split(new[] { '-' }, 3);
                int n;
                if (f.Length < 2 || !int.TryParse(f[1].Trim(), out n)) continue;

                if (up.StartsWith("TPL"))
                {
                    // several TPL lines in a row are variations of the same BIN: the first one is the pair
                    if (tpl < 0 || lastWasBin)
                    {
                        tpl = n;
                        note = f.Length > 2 ? f[2].Trim() : null;
                        lastWasBin = false;
                    }
                }
                else if (up.StartsWith("BIN") && tpl >= 0)
                {
                    list.Add(new ListEntry { Bin = n, Tpl = tpl, Description = f.Length > 2 ? f[2].Trim() : "", TplNote = note });
                    lastWasBin = true;
                }
            }
            var res = new List<KeyValuePair<string, List<ListEntry>>>();
            res.Add(new KeyValuePair<string, List<ListEntry>>("", list));
            return res;
        }

        private static List<KeyValuePair<string, List<ListEntry>>> ParseEnemyFormat(string[] lines)
        {
            var res = new List<KeyValuePair<string, List<ListEntry>>>();
            List<ListEntry> cur = null;
            List<ListEntry> pending = new List<ListEntry>();   // bins of the current block that still have no TPL

            Regex rxSection = new Regex(@"^(em[0-9a-f]{2})\b\s*(?:\|\s*(.*))?$", RegexOptions.IgnoreCase);
            Regex rxBin = new Regex(@"^(\d{1,3})\.bin\b\s*[-:]?\s*(.*)$", RegexOptions.IgnoreCase);
            Regex rxArrow = new Regex(@"\]?\s*->\s*(\d{1,3})\.tpl", RegexOptions.IgnoreCase);
            Regex rxTplLine = new Regex(@"^(?:Both Files Above Use\s*->\s*)?(\d{1,3})\.tpl\b", RegexOptions.IgnoreCase);

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                Match m = rxSection.Match(line);
                if (m.Success && !rxBin.IsMatch(line))
                {
                    cur = new List<ListEntry>();
                    res.Add(new KeyValuePair<string, List<ListEntry>>(m.Groups[1].Value.ToLowerInvariant(), cur));
                    pending.Clear();
                    continue;
                }
                if (cur == null) continue;

                m = rxBin.Match(line);
                if (m.Success)
                {
                    ListEntry e = new ListEntry { Bin = int.Parse(m.Groups[1].Value) };
                    string rest = m.Groups[2].Value;
                    Match a = rxArrow.Match(rest);
                    if (a.Success)
                    {
                        e.Tpl = int.Parse(a.Groups[1].Value);
                        rest = rest.Substring(0, a.Index);
                        pending.Clear();   // a new explicit pair ends the previous "both files above" block
                    }
                    else
                    {
                        pending.Add(e);
                    }
                    e.Description = rest.Trim().TrimEnd('-').Trim();
                    cur.Add(e);
                    continue;
                }

                m = rxTplLine.Match(line);
                if (m.Success || line.StartsWith("Both Files Above", StringComparison.OrdinalIgnoreCase))
                {
                    Match t = Regex.Match(line, @"(\d{1,3})\.tpl", RegexOptions.IgnoreCase);
                    if (t.Success && pending.Count > 0)
                    {
                        int tpl = int.Parse(t.Groups[1].Value);
                        foreach (ListEntry e in pending) e.Tpl = tpl;
                        pending.Clear();
                    }
                }
            }
            return res;
        }
    }
}
