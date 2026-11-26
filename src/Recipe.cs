using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RE4BinMerge
{
    /// <summary>Reads / writes the .merge.txt recipe files (shared by the CLI and the GUI).</summary>
    public static class Recipe
    {
        public static void Load(string path, List<PartInput> parts, MergeOptions options, ref string output)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                int c = line.IndexOf(':');
                if (c < 0) continue;
                string key = line.Substring(0, c).Trim().ToLowerInvariant();
                string val = line.Substring(c + 1).Trim();

                switch (key)
                {
                    case "output": output = Path.Combine(dir, val); break;
                    case "mergesamematerials": options.MergeSameMaterials = ParseBool(val); break;
                    case "transparentlast": options.TransparentLast = ParseBool(val); break;
                    case "limitweightmaps": options.LimitWeightMaps = ParseBool(val); break;
                    case "previewobj": options.ExportPreviewObj = ParseBool(val); break;
                    case "textures": options.TexturesFolder = Path.Combine(dir, val); break;
                    case "part":
                        {
                            string[] f = val.Split(',').Select(s => s.Trim()).Where(s => s.Length != 0).ToArray();
                            if (f.Length < 2) throw new FormatException("Bad Part line: " + raw);
                            PartInput p = new PartInput
                            {
                                BinPath = Path.Combine(dir, f[0]),
                                TplPath = Path.Combine(dir, f[1])
                            };
                            for (int i = 2; i < f.Length; i++)
                            {
                                if (f[i].ToLowerInvariant().StartsWith("bone="))
                                    p.RigidBone = byte.Parse(f[i].Substring(5));
                            }
                            parts.Add(p);
                            break;
                        }
                }
            }
            if (output == null) output = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "_merged");
        }

        /// <summary>Paths are written relative to the recipe folder when possible.</summary>
        public static void Save(string path, List<PartInput> parts, MergeOptions options, string output)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# RE4 UHD BIN+TPL merge recipe");
            sb.AppendLine("Output: " + Rel(dir, output));
            sb.AppendLine("MergeSameMaterials: " + (options.MergeSameMaterials ? "true" : "false"));
            sb.AppendLine("TransparentLast: " + (options.TransparentLast ? "true" : "false"));
            sb.AppendLine("LimitWeightMaps: " + (options.LimitWeightMaps ? "true" : "false"));
            sb.AppendLine("PreviewObj: " + (options.ExportPreviewObj ? "true" : "false"));
            if (!string.IsNullOrEmpty(options.TexturesFolder)) sb.AppendLine("Textures: " + Rel(dir, options.TexturesFolder));
            foreach (PartInput p in parts)
            {
                string line = "Part: " + Rel(dir, p.BinPath) + ", " + Rel(dir, p.TplPath);
                if (p.RigidBone != 0) line += ", bone=" + p.RigidBone;
                sb.AppendLine(line);
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string Rel(string dir, string file)
        {
            string full = Path.GetFullPath(file);
            string d = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return full.Substring(d.Length);
            return full;
        }

        private static bool ParseBool(string v)
        {
            v = v.Trim().ToLowerInvariant();
            return v == "true" || v == "1" || v == "yes";
        }
    }
}
