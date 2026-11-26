using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace RE4BinMerge
{
    /// <summary>A decoded texture (ARGB) with a box-filtered mip chain for the preview.</summary>
    public class TexImage
    {
        public string Source;
        public int Width, Height;
        public int[][] Mips;     // [0] = full size
        public int[] MipW, MipH;

        public static TexImage FromArgb(int[] px, int w, int h, string source)
        {
            List<int[]> mips = new List<int[]>();
            List<int> mw = new List<int>(), mh = new List<int>();
            mips.Add(px); mw.Add(w); mh.Add(h);
            int cw = w, ch = h;
            int[] cur = px;
            while (cw > 1 || ch > 1)
            {
                int nw = Math.Max(1, cw / 2), nh = Math.Max(1, ch / 2);
                int[] next = new int[nw * nh];
                for (int y = 0; y < nh; y++)
                {
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = Math.Min(cw - 1, x * 2), x1 = Math.Min(cw - 1, x * 2 + 1);
                        int y0 = Math.Min(ch - 1, y * 2), y1 = Math.Min(ch - 1, y * 2 + 1);
                        int a = 0, r = 0, g = 0, b = 0;
                        foreach (int c in new[] { cur[y0 * cw + x0], cur[y0 * cw + x1], cur[y1 * cw + x0], cur[y1 * cw + x1] })
                        {
                            a += (c >> 24) & 255; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255;
                        }
                        next[y * nw + x] = ((a / 4) << 24) | ((r / 4) << 16) | ((g / 4) << 8) | (b / 4);
                    }
                }
                mips.Add(next); mw.Add(nw); mh.Add(nh);
                cur = next; cw = nw; ch = nh;
            }
            return new TexImage { Source = source, Width = w, Height = h, Mips = mips.ToArray(), MipW = mw.ToArray(), MipH = mh.ToArray() };
        }

        /// <summary>Bilinear sample with wrap (repeat). u,v in texture space, v = 0 is the top row.</summary>
        public int Sample(int level, float u, float v)
        {
            if (level < 0) level = 0;
            if (level >= Mips.Length) level = Mips.Length - 1;
            int w = MipW[level], h = MipH[level];
            int[] px = Mips[level];

            float fx = u * w - 0.5f, fy = v * h - 0.5f;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int xa = Mod(x0, w), xb = Mod(x0 + 1, w), ya = Mod(y0, h), yb = Mod(y0 + 1, h);

            int c00 = px[ya * w + xa], c10 = px[ya * w + xb], c01 = px[yb * w + xa], c11 = px[yb * w + xb];
            int a = Lerp2((c00 >> 24) & 255, (c10 >> 24) & 255, (c01 >> 24) & 255, (c11 >> 24) & 255, tx, ty);
            int r = Lerp2((c00 >> 16) & 255, (c10 >> 16) & 255, (c01 >> 16) & 255, (c11 >> 16) & 255, tx, ty);
            int g = Lerp2((c00 >> 8) & 255, (c10 >> 8) & 255, (c01 >> 8) & 255, (c11 >> 8) & 255, tx, ty);
            int b = Lerp2(c00 & 255, c10 & 255, c01 & 255, c11 & 255, tx, ty);
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        private static int Mod(int a, int n) { int m = a % n; return m < 0 ? m + n : m; }

        private static int Lerp2(int c00, int c10, int c01, int c11, float tx, float ty)
        {
            float top = c00 + (c10 - c00) * tx;
            float bot = c01 + (c11 - c01) * tx;
            return (int)(top + (bot - top) * ty + 0.5f);
        }
    }

    /// <summary>
    /// Finds and decodes the texture files of a model. The game only uses DDS and TGA, so those are the two formats read
    /// (DDS with DDSReaderSharp, TGA with TGASharpLib). A TPL entry is (PackID, TextureID); the file is looked up as
    ///   {folder}/{TextureID:0000}.dds|tga      (folder = the extracted pack folder, e.g. 00000015)
    ///   {folder}/{PackID:00000000}/{TextureID:0000}.dds|tga   (folder = the parent of the pack folders)
    /// </summary>
    public class TextureStore
    {
        private readonly Dictionary<ulong, TexImage> cache = new Dictionary<ulong, TexImage>();
        private readonly Dictionary<ulong, string> problems = new Dictionary<ulong, string>();

        public string Folder { get; private set; }
        public bool HasFolder { get { return !string.IsNullOrEmpty(Folder) && Directory.Exists(Folder); } }

        public void SetFolder(string folder)
        {
            Folder = folder;
            cache.Clear();
            problems.Clear();
        }

        public static ulong Key(uint pack, uint id) { return ((ulong)pack << 32) | id; }

        public string FindFile(uint pack, uint id)
        {
            if (!HasFolder) return null;
            string name = id.ToString("D4");
            string[] dirs = { Folder, Path.Combine(Folder, pack.ToString("D8")) };
            string[] exts = { ".dds", ".DDS", ".tga", ".TGA" };
            foreach (string d in dirs)
            {
                foreach (string e in exts)
                {
                    string p = Path.Combine(d, name + e);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        /// <summary>Returns the texture or null (missing / unsupported file; see Problem()).</summary>
        public TexImage Get(uint pack, uint id)
        {
            ulong key = Key(pack, id);
            TexImage t;
            if (cache.TryGetValue(key, out t)) return t;
            if (problems.ContainsKey(key)) return null;

            string path = FindFile(pack, id);
            if (path == null)
            {
                problems[key] = "file " + id.ToString("D4") + ".dds/.tga not found";
                return null;
            }
            try
            {
                t = Load(path);
                cache[key] = t;
                return t;
            }
            catch (Exception ex)
            {
                problems[key] = Path.GetFileName(path) + ": " + ex.Message;
                return null;
            }
        }

        public string Problem(uint pack, uint id)
        {
            string s;
            return problems.TryGetValue(Key(pack, id), out s) ? s : null;
        }

        public static TexImage Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            Bitmap src;
            if (ext == ".dds")
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 128 || bytes[0] != 'D' || bytes[1] != 'D' || bytes[2] != 'S')
                    throw new InvalidDataException("not a DDS file");
                try { src = DDSReaderSharp.ToBitmap(bytes); }
                catch (Exception) { throw new NotSupportedException("DDS pixel format not supported by DDSReaderSharp (it reads DXT1-5 and uncompressed formats)"); }
            }
            else if (ext == ".tga")
            {
                src = new TGASharpLib.TGA(path).ToBitmap();
            }
            else throw new NotSupportedException("only .dds and .tga are supported");

            if (src == null) throw new InvalidDataException("could not decode the image");
            using (src)
            {
                int w = src.Width, h = src.Height;
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
                    }
                    int[] px = new int[w * h];
                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        if (bd.Stride == w * 4) Marshal.Copy(bd.Scan0, px, 0, px.Length);
                        else for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * w, w);
                    }
                    finally { bmp.UnlockBits(bd); }
                    return TexImage.FromArgb(px, w, h, Path.GetFileName(path));
                }
            }
        }
    }
}
