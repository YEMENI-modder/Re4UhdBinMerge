using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RE4BinMerge
{
    /// <summary>
    /// Tiny software 3D viewer (z-buffer rasterizer, no GPU / OpenGL needed).
    /// Left drag = rotate, right drag = pan, wheel = zoom, double click = reset the view.
    /// </summary>
    public class PreviewControl : Control
    {
        public class Item
        {
            public MeshData Mesh;
            public Color Color;
            public bool ColorByGroup;
        }

        private readonly List<Item> items = new List<Item>();
        private float yaw, pitch;      // degrees
        private float zoom = 1f;
        private float panX, panY;      // in control pixels
        private Point lastMouse;
        private MouseButtons dragButton = MouseButtons.None;

        // bounding sphere of everything shown (so the model never changes size while it rotates)
        private float centerX, centerY, centerZ, diagonal = 1f;

        private static readonly Color[] GroupColors = new Color[]
        {
            Color.FromArgb(230,25,75), Color.FromArgb(60,180,75), Color.FromArgb(67,99,216), Color.FromArgb(245,130,49),
            Color.FromArgb(145,30,180), Color.FromArgb(70,240,240), Color.FromArgb(240,50,230), Color.FromArgb(188,246,12),
            Color.FromArgb(250,190,190), Color.FromArgb(0,128,128), Color.FromArgb(230,190,255), Color.FromArgb(170,110,40),
            Color.FromArgb(255,250,200), Color.FromArgb(128,0,0), Color.FromArgb(170,255,195), Color.FromArgb(128,128,0)
        };

        public static Color PartColor(int i) { return GroupColors[i % GroupColors.Length]; }

        /// <summary>Draw the diffuse textures (needs MeshData.Bind first). Materials without a texture file are grey.</summary>
        public bool ShowTextures
        {
            get { return showTextures; }
            set { showTextures = value; Invalidate(); }
        }
        private bool showTextures;

        public string Message = "";

        public PreviewControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(30, 30, 36);
        }

        public void SetBackground(Color c) { BackColor = c; Invalidate(); }

        public void SetItems(List<Item> newItems)
        {
            items.Clear();
            items.AddRange(newItems);
            ComputeBounds();
            Invalidate();
        }

        public void SetYaw(float degrees) { yaw = degrees; pitch = 0; Invalidate(); }

        public void ResetView()
        {
            yaw = 0; pitch = 0; zoom = 1f; panX = 0; panY = 0;
            Invalidate();
        }

        private void ComputeBounds()
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            bool any = false;
            foreach (Item it in items)
            {
                float[] p = it.Mesh.Pos;
                for (int i = 0; i + 2 < p.Length; i += 3)
                {
                    any = true;
                    if (p[i] < minX) minX = p[i]; if (p[i] > maxX) maxX = p[i];
                    if (p[i + 1] < minY) minY = p[i + 1]; if (p[i + 1] > maxY) maxY = p[i + 1];
                    if (p[i + 2] < minZ) minZ = p[i + 2]; if (p[i + 2] > maxZ) maxZ = p[i + 2];
                }
            }
            if (!any) { centerX = centerY = centerZ = 0; diagonal = 1; return; }
            centerX = (minX + maxX) / 2; centerY = (minY + maxY) / 2; centerZ = (minZ + maxZ) / 2;
            float dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            diagonal = Math.Max(1e-3f, (float)Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }

        protected override void OnMouseDown(MouseEventArgs e) { dragButton = e.Button; lastMouse = e.Location; Focus(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { dragButton = MouseButtons.None; base.OnMouseUp(e); }
        protected override void OnDoubleClick(EventArgs e) { ResetView(); base.OnDoubleClick(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragButton == MouseButtons.Left)
            {
                yaw += (e.X - lastMouse.X) * 0.8f;
                pitch += (e.Y - lastMouse.Y) * 0.5f;
                if (pitch > 89f) pitch = 89f;
                if (pitch < -89f) pitch = -89f;
                lastMouse = e.Location;
                Invalidate();
            }
            else if (dragButton == MouseButtons.Right || dragButton == MouseButtons.Middle)
            {
                panX += e.X - lastMouse.X;
                panY += e.Y - lastMouse.Y;
                lastMouse = e.Location;
                Invalidate();
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            zoom *= e.Delta > 0 ? 1.15f : 1f / 1.15f;
            if (zoom < 0.2f) zoom = 0.2f;
            if (zoom > 12f) zoom = 12f;
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            Graphics g = pe.Graphics;
            int W = Width, H = Height;
            if (W < 10 || H < 10) return;
            if (items.Count == 0)
            {
                g.Clear(BackColor);
                using (Brush b = new SolidBrush(Color.Gray))
                    g.DrawString(Message.Length > 0 ? Message : "Nothing to show", Font, b, 10, 10);
                return;
            }
            using (Bitmap bmp = RenderBitmap(W, H))
                g.DrawImageUnscaled(bmp, 0, 0);
            using (Brush b = new SolidBrush(Color.FromArgb(210, 210, 210)))
                g.DrawString(Message, Font, b, 8, 6);
        }

        /// <summary>Saves what the viewer shows at twice the window size (PNG).</summary>
        public void SavePng(string path)
        {
            using (Bitmap bmp = RenderBitmap(Math.Max(10, Width) * 2, Math.Max(10, Height) * 2))
                bmp.Save(path, ImageFormat.Png);
        }

        /// <summary>Renders the current view into a new bitmap of the given size.</summary>
        public Bitmap RenderBitmap(int W, int H)
        {
            Bitmap result = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            int[] pix = new int[W * H];
            int bg = BackColor.ToArgb();
            for (int i = 0; i < pix.Length; i++) pix[i] = bg;
            float[] zbuf = new float[W * H];
            for (int i = 0; i < zbuf.Length; i++) zbuf[i] = float.MinValue;

            double ry = yaw * Math.PI / 180.0, rx = pitch * Math.PI / 180.0;
            float cs = (float)Math.Cos(ry), sn = (float)Math.Sin(ry);
            float cp = (float)Math.Cos(rx), sp = (float)Math.Sin(rx);

            float k = Width > 0 ? (float)W / Width : 1f;   // pan is stored in control pixels
            float scale = 0.92f * Math.Min(W, H) / diagonal * zoom;
            float ox = W / 2f + panX * k, oy = H / 2f + panY * k;

            foreach (Item item in items)
            {
                MeshData m = item.Mesh;
                float[] p = m.Pos;
                float[] t = new float[p.Length];
                for (int i = 0; i + 2 < p.Length; i += 3)
                {
                    float x = p[i] - centerX, y = p[i + 1] - centerY, z = p[i + 2] - centerZ;
                    float xr = x * cs + z * sn;
                    float zr = -x * sn + z * cs;
                    t[i] = xr;
                    t[i + 1] = y * cp - zr * sp;
                    t[i + 2] = y * sp + zr * cp;
                }

                bool tex = showTextures && m.Diffuse != null;
                for (int f = 0; f < m.TriCount; f++)
                {
                    int v0 = m.Tri[f * 3], v1 = m.Tri[f * 3 + 1], v2 = m.Tri[f * 3 + 2];
                    int i0 = v0 * 3, i1 = v1 * 3, i2 = v2 * 3;
                    if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= t.Length || i1 >= t.Length || i2 >= t.Length) continue;

                    // flat shading from the face normal (two-sided)
                    float ax = t[i1] - t[i0], ay = t[i1 + 1] - t[i0 + 1], az = t[i1 + 2] - t[i0 + 2];
                    float bx = t[i2] - t[i0], by = t[i2 + 1] - t[i0 + 1], bz = t[i2 + 2] - t[i0 + 2];
                    float nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
                    float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    float d = 0.5f;
                    if (len > 1e-6f) d = Math.Abs((nx * 0.3f + ny * 0.4f + nz * 1f) / (len * 1.1180f));

                    int grp = m.TriGroup[f];
                    TexImage dtex = null, otex = null;
                    if (tex && grp < m.Diffuse.Length)
                    {
                        dtex = m.Diffuse[grp];
                        if (m.Opacity != null && grp < m.Opacity.Length) otex = m.Opacity[grp];
                    }

                    float shade = 0.25f + 0.75f * d;
                    float tshade = 0.55f + 0.45f * d;     // textures stay brighter than the flat colors
                    Color bc = item.ColorByGroup ? GroupColors[grp % GroupColors.Length] : item.Color;
                    if (tex && dtex == null) bc = Color.FromArgb(150, 150, 150);   // no texture file for this material
                    int flat = (255 << 24) | ((int)(bc.R * shade) << 16) | ((int)(bc.G * shade) << 8) | (int)(bc.B * shade);

                    float x0 = t[i0] * scale + ox, y0 = -t[i0 + 1] * scale + oy, z0 = t[i0 + 2];
                    float x1 = t[i1] * scale + ox, y1 = -t[i1 + 1] * scale + oy, zz1 = t[i1 + 2];
                    float x2 = t[i2] * scale + ox, y2 = -t[i2 + 1] * scale + oy, zz2 = t[i2 + 2];

                    float det = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
                    if (Math.Abs(det) < 1e-9f) continue;

                    float u0 = 0, tv0 = 0, u1 = 0, tv1 = 0, u2 = 0, tv2 = 0;
                    int lvl = 0, olvl = 0;
                    if (dtex != null)
                    {
                        u0 = m.UV[v0 * 2]; tv0 = m.UV[v0 * 2 + 1];
                        u1 = m.UV[v1 * 2]; tv1 = m.UV[v1 * 2 + 1];
                        u2 = m.UV[v2 * 2]; tv2 = m.UV[v2 * 2 + 1];
                        float uvArea = Math.Abs((u1 - u0) * (tv2 - tv0) - (u2 - u0) * (tv1 - tv0)) * 0.5f;
                        float scrArea = Math.Abs(det) * 0.5f;
                        lvl = MipLevel(uvArea * dtex.Width * dtex.Height, scrArea);
                        if (otex != null) olvl = MipLevel(uvArea * otex.Width * otex.Height, scrArea);
                    }

                    int minx = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
                    int maxx = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
                    int miny = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))));
                    int maxy = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));

                    for (int py = miny; py <= maxy; py++)
                    {
                        float fy = py + 0.5f;
                        for (int px = minx; px <= maxx; px++)
                        {
                            float fx = px + 0.5f;
                            float w0 = ((y1 - y2) * (fx - x2) + (x2 - x1) * (fy - y2)) / det;
                            float w1 = ((y2 - y0) * (fx - x2) + (x0 - x2) * (fy - y2)) / det;
                            float w2 = 1f - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            float z = w0 * z0 + w1 * zz1 + w2 * zz2;
                            int idx = py * W + px;
                            if (z <= zbuf[idx]) continue;

                            if (dtex == null)
                            {
                                zbuf[idx] = z; pix[idx] = flat;
                                continue;
                            }

                            float u = w0 * u0 + w1 * u1 + w2 * u2;
                            float v = w0 * tv0 + w1 * tv1 + w2 * tv2;
                            if (otex != null)
                            {
                                // opacity map: grey mask, below half = transparent (the pixel is skipped)
                                int o = otex.Sample(olvl, u, v);
                                if (((o >> 16) & 255) < 128) continue;
                            }
                            int c = dtex.Sample(lvl, u, v);
                            int r = (int)(((c >> 16) & 255) * tshade), gg = (int)(((c >> 8) & 255) * tshade), bb = (int)((c & 255) * tshade);
                            zbuf[idx] = z;
                            pix[idx] = (255 << 24) | (r << 16) | (gg << 8) | bb;
                        }
                    }
                }
            }

            BitmapData bd = result.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (bd.Stride == W * 4) Marshal.Copy(pix, 0, bd.Scan0, pix.Length);
                else for (int y = 0; y < H; y++) Marshal.Copy(pix, y * W, bd.Scan0 + y * bd.Stride, W);
            }
            finally { result.UnlockBits(bd); }
            return result;
        }

        // mip level so that one screen pixel covers about one texel
        private static int MipLevel(float texelArea, float screenArea)
        {
            if (screenArea <= 1e-6f || texelArea <= 1e-6f) return 0;
            double l = 0.5 * Math.Log(texelArea / screenArea, 2.0);
            return l < 0 ? 0 : (int)(l + 0.5);
        }
    }
}
