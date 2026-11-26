using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RE4BinMerge
{
    public class MainForm : Form
    {
        public const string AppName = "RE4 UHD BIN + TPL Merge Tool";
        public const string AppVersion = "1.0";
        public const string RepoUrl = "https://github.com/YEMENI-modder/Re4UhdBinMerge";

        private class PartRow
        {
            public string Bin;
            public string Tpl;
            public byte RigidBone;
            public string Desc = "";
            public MeshData Mesh;
        }

        private readonly TextureStore store = new TextureStore();
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 12000, InitialDelay = 400 };

        private SplitContainer splitMain;
        private ListView list;
        private ImageList swatches;
        private PreviewControl preview;
        private TextBox txtOutput, txtLog;
        private CheckBox chkJoin, chkTransparent, chkLimit, chkObj, chkTextures;
        private RadioButton rbParts, rbMerged;
        private Button btnMerge, btnOpenFolder;
        private ToolStripStatusLabel statusLeft, statusRight;
        private MeshData mergedMesh;
        private string lastOutDir;
        private int bgIndex;
        private static readonly Color[] Backgrounds = { Color.FromArgb(30, 30, 36), Color.FromArgb(120, 124, 132), Color.FromArgb(238, 238, 240) };

        public MainForm()
        {
            Text = AppName + " " + AppVersion;
            Font = SystemFonts.MessageBoxFont;
            Width = 1280; Height = 800;
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete && list.Focused) RemoveSelected();
            };
            BuildUi();
            UpdatePreview();
            UpdateStatus();
        }

        // ------------------------------------------------------------------ UI

        private void BuildUi()
        {
            // ---------- swatches: the color of each part in the 3D view
            swatches = new ImageList { ImageSize = new Size(14, 14), ColorDepth = ColorDepth.Depth32Bit };
            for (int i = 0; i < 16; i++)
            {
                Bitmap b = new Bitmap(14, 14);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Transparent);
                    using (Brush br = new SolidBrush(PreviewControl.PartColor(i))) g.FillRectangle(br, 1, 1, 12, 12);
                    g.DrawRectangle(Pens.DimGray, 1, 1, 11, 11);
                }
                swatches.Images.Add(b);
            }

            // ---------- bottom status bar
            StatusStrip status = new StatusStrip { SizingGrip = true };
            statusLeft = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusRight = new ToolStripStatusLabel { TextAlign = ContentAlignment.MiddleRight };
            status.Items.Add(statusLeft);
            status.Items.Add(statusRight);

            // ---------- main tool bar
            ToolStrip bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 4), RenderMode = ToolStripRenderMode.System };
            bar.Items.Add(ToolButton("Add BIN + TPL", "Pick BIN files; the TPL of each one is searched automatically.", delegate { AddFiles(); }));
            bar.Items.Add(ToolButton("List + folder", "Load a BIN/TPL list (txt or the saved enemy list page) and the folder of the extracted model.", delegate { LoadListAndFolder(); }));
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(ToolButton("Open recipe", "Open a .merge.txt recipe.", delegate { OpenRecipe(); }));
            bar.Items.Add(ToolButton("Save recipe", "Save the part list and options as a .merge.txt recipe.", delegate { SaveRecipe(); }));
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(ToolButton("Textures folder", "Folder with the DDS / TGA textures of the model (for example the extracted 00000015 folder). Only used to show the model in the preview.", delegate { BrowseTextures(); }));
            ToolStripButton about = ToolButton("About", "Credits and licenses", delegate { ShowAbout(); });
            about.Alignment = ToolStripItemAlignment.Right;
            bar.Items.Add(about);

            // ---------- split: left = parts + options, right = 3D view
            splitMain = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            Load += delegate { splitMain.SplitterDistance = 560; };

            // ----- left side
            Panel left = splitMain.Panel1;
            left.Padding = new Padding(8, 8, 4, 4);

            GroupBox gbParts = new GroupBox { Text = "Parts  (tick the ones to merge, the first ticked one gives the skeleton)", Dock = DockStyle.Fill, Padding = new Padding(6) };

            list = new ListView();
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.GridLines = false;
            list.SmallImageList = swatches;
            list.Dock = DockStyle.Fill;
            list.Columns.Add("BIN", 140);
            list.Columns.Add("TPL", 118);
            list.Columns.Add("Info", 150);
            list.Columns.Add("Tex", 46);
            list.Columns.Add("Bone", 54);
            list.ItemChecked += delegate { if (rbParts != null && rbParts.Checked) UpdatePreview(); UpdateStatus(); };
            list.SelectedIndexChanged += delegate { if (rbParts != null && rbParts.Checked) UpdatePreview(); };

            FlowLayoutPanel rowBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 4, 0, 0) };
            rowBtns.Controls.Add(Btn("Up", "Move the selected part up (the first ticked part gives the skeleton).", delegate { Move(-1); }, 48));
            rowBtns.Controls.Add(Btn("Down", "Move the selected part down.", delegate { Move(1); }, 54));
            rowBtns.Controls.Add(Btn("Remove", "Remove the selected parts from the list (Del).", delegate { RemoveSelected(); }, 66));
            rowBtns.Controls.Add(Btn("Set TPL...", "Change the TPL of all selected parts. Hands and necks are shared, use the TPL of the body they are merged with.", delegate { SetTpl(); }, 82));
            rowBtns.Controls.Add(Btn("Rigid bone...", "Bone used by a part without weights (a rigid mesh) when another part is weighted.", delegate { SetRigid(); }, 92));
            rowBtns.Controls.Add(Btn("All", "Tick all parts", delegate { foreach (ListViewItem i in list.Items) i.Checked = true; }, 40));
            rowBtns.Controls.Add(Btn("None", "Untick all parts", delegate { foreach (ListViewItem i in list.Items) i.Checked = false; }, 48));

            gbParts.Controls.Add(list);
            gbParts.Controls.Add(rowBtns);

            // options
            GroupBox opts = new GroupBox { Text = "Merge options", Dock = DockStyle.Bottom, Height = 126, Padding = new Padding(8, 4, 8, 4) };
            TableLayoutPanel tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
            chkJoin = new CheckBox { Text = "Join identical materials", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
            chkTransparent = new CheckBox { Text = "Transparent materials last", AutoSize = true, Anchor = AnchorStyles.Left };
            chkLimit = new CheckBox { Text = "Limit weight maps to 255", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
            chkObj = new CheckBox { Text = "Write preview .obj", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
            tips.SetToolTip(chkJoin, "Materials that end up identical are joined into one group.");
            tips.SetToolTip(chkTransparent, "Put the materials that have an opacity texture at the end (draw order).");
            tips.SetToolTip(chkLimit, "The base game accepts only 255 weight-map combinations. If the merge needs more, the weights are rounded in steps.\nTurn it off if you use the Qingsheng DLL (Allocate more memory for bones).");
            tips.SetToolTip(chkObj, "Also write a *_preview.obj read back from the merged BIN.");
            tl.Controls.Add(chkJoin, 0, 0);
            tl.Controls.Add(chkTransparent, 1, 0);
            tl.Controls.Add(chkLimit, 0, 1);
            tl.Controls.Add(chkObj, 1, 1);

            Panel outRow = new Panel { Dock = DockStyle.Fill };
            Label lo = new Label { Text = "Output name:", AutoSize = true, Left = 0, Top = 8 };
            int outLeft = 100;
            Button bo = Btn("...", "Choose the output name (the .bin and .tpl extensions are added).", delegate { BrowseOutput(); }, 34);
            bo.Dock = DockStyle.Right;
            txtOutput = new TextBox { Left = outLeft, Top = 4, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            outRow.Controls.Add(lo);
            outRow.Controls.Add(txtOutput);
            outRow.Controls.Add(bo);
            outRow.Resize += delegate { txtOutput.Width = Math.Max(60, outRow.ClientSize.Width - outLeft - 40); };
            tl.Controls.Add(outRow, 0, 2);
            tl.SetColumnSpan(outRow, 2);
            opts.Controls.Add(tl);

            // action row
            FlowLayoutPanel act = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 6, 0, 4) };
            btnMerge = new Button { Text = "MERGE", Width = 150, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold) };
            btnMerge.FlatAppearance.BorderSize = 0;
            btnMerge.Click += delegate { DoMerge(); };
            tips.SetToolTip(btnMerge, "Merge the ticked parts into one BIN + one TPL.");
            btnOpenFolder = Btn("Open output folder", "Open the folder of the last merge.", delegate { OpenOutputFolder(); }, 140);
            btnOpenFolder.Height = 36;
            btnOpenFolder.Enabled = false;
            act.Controls.Add(btnMerge);
            act.Controls.Add(btnOpenFolder);

            GroupBox gbLog = new GroupBox { Text = "Log", Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(6, 2, 6, 6) };
            txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 8.5f), BackColor = Color.White };
            gbLog.Controls.Add(txtLog);

            left.Controls.Add(gbParts);
            left.Controls.Add(opts);
            left.Controls.Add(act);
            left.Controls.Add(gbLog);

            // ----- right side (3D view)
            Panel right = splitMain.Panel2;
            right.Padding = new Padding(4, 8, 8, 4);

            FlowLayoutPanel vbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 2, 0, 2), WrapContents = true };
            rbParts = new RadioButton { Text = "Parts", Checked = true, AutoSize = true, Margin = new Padding(3, 5, 3, 0) };
            rbMerged = new RadioButton { Text = "Merged result", AutoSize = true, Margin = new Padding(3, 5, 10, 0) };
            rbParts.CheckedChanged += delegate { UpdatePreview(); };
            chkTextures = new CheckBox { Text = "Textures", AutoSize = true, Margin = new Padding(3, 5, 10, 0) };
            chkTextures.CheckedChanged += delegate { OnTexturesToggled(); };
            tips.SetToolTip(chkTextures, "Show the DDS / TGA textures on the model (choose the folder with the Textures folder button).");
            tips.SetToolTip(rbParts, "Every part has its own color (same color as the square in the list).");
            tips.SetToolTip(rbMerged, "The merged BIN read back from the file (flat color per material, or textured).");
            vbar.Controls.Add(rbParts);
            vbar.Controls.Add(rbMerged);
            vbar.Controls.Add(chkTextures);
            vbar.Controls.Add(Btn("Front", "Front view", delegate { preview.SetYaw(0); }, 52));
            vbar.Controls.Add(Btn("Side", "Side view", delegate { preview.SetYaw(90); }, 52));
            vbar.Controls.Add(Btn("Back", "Back view", delegate { preview.SetYaw(180); }, 52));
            vbar.Controls.Add(Btn("Reset", "Reset rotation, zoom and position (double click on the view does the same).", delegate { preview.ResetView(); }, 52));
            vbar.Controls.Add(Btn("BG color", "Change the background color of the view.", delegate { CycleBackground(); }, 70));
            vbar.Controls.Add(Btn("Save PNG", "Save the current view as a PNG image (twice the window size).", delegate { SavePng(); }, 76));

            Label hint = new Label { Dock = DockStyle.Bottom, Height = 22, Text = "Left drag = rotate   |   right drag = move   |   wheel = zoom   |   double click = reset", ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft };
            preview = new PreviewControl { Dock = DockStyle.Fill };

            right.Controls.Add(preview);
            right.Controls.Add(hint);
            right.Controls.Add(vbar);

            Controls.Add(splitMain);
            Controls.Add(bar);
            Controls.Add(status);
        }

        private ToolStripButton ToolButton(string text, string tip, EventHandler click)
        {
            ToolStripButton b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip, Padding = new Padding(4, 2, 4, 2) };
            b.Click += click;
            return b;
        }

        private Button Btn(string text, string tip, EventHandler click, int width)
        {
            Button b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(width, 26), Padding = new Padding(4, 0, 4, 0), FlatStyle = FlatStyle.System };
            b.Click += click;
            if (tip != null) tips.SetToolTip(b, tip);
            return b;
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                AppName + " " + AppVersion + "\n" + RepoUrl + "\n\n" +
                "Made by YEMENI-modder.\n" +
                "The code was written entirely by Claude Sonnet 5.5 (Anthropic); the author only tested it.\n\n" +
                "Uses (all MIT license, see the Lib folder):\n" +
                "  RE4-UHD-BIN-TPL-TOOLS - JADERLINK (BIN / TPL read and write)\n" +
                "  SimpleEndianBinaryIO (as shipped in JADERLINK's tools)\n" +
                "  TGASharpLib - Alex Green (TGA reader)\n" +
                "  DDSReaderSharp - Alex Green, based on DDSReader.java by Kenji Sasaki (DDS reader)\n\n" +
                "MIT License - Copyright (c) 2026 YEMENI",
                "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void UpdateStatus()
        {
            if (statusLeft == null) return;
            int ticked = list.Items.Cast<ListViewItem>().Count(i => i.Checked);
            statusLeft.Text = list.Items.Count + " part(s) in the list, " + ticked + " ticked";
            statusRight.Text = store.HasFolder ? "Textures: " + store.Folder : "Textures: no folder chosen";
        }

        // ------------------------------------------------------------------ adding parts

        private void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object s, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length == 1 && Directory.Exists(files[0]))
            {
                SetTexturesFolder(files[0]);      // a dropped folder = the textures folder
                return;
            }
            if (files.Length == 1 && files[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                LoadRecipeFile(files[0]);
                return;
            }
            AddBins(files.Where(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToArray());
        }

        private void AddFiles()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Select BIN files (their TPL is searched automatically)";
                d.Filter = "RE4 BIN|*.bin;*.BIN";
                d.Multiselect = true;
                if (d.ShowDialog() != DialogResult.OK) return;
                AddBins(d.FileNames);
            }
        }

        private void AddBins(string[] bins)
        {
            foreach (string bin in bins)
            {
                string tpl = GuessTpl(bin);
                if (tpl == null)
                {
                    using (OpenFileDialog d = new OpenFileDialog())
                    {
                        d.Title = "TPL for " + Path.GetFileName(bin);
                        d.Filter = "RE4 TPL|*.tpl;*.TPL";
                        d.InitialDirectory = Path.GetDirectoryName(bin);
                        if (d.ShowDialog() != DialogResult.OK) continue;
                        tpl = d.FileName;
                    }
                }
                AddRow(new PartRow { Bin = bin, Tpl = tpl }, true);
            }
            RefreshRows();
            UpdatePreview();
        }

        // same name .tpl, otherwise the next number (pl08_000.BIN -> pl08_001.TPL); not always right, so it is only a guess
        private static string GuessTpl(string bin)
        {
            string dir = Path.GetDirectoryName(bin);
            string name = Path.GetFileNameWithoutExtension(bin);
            foreach (string ext in new[] { ".TPL", ".tpl" })
            {
                string p = Path.Combine(dir, name + ext);
                if (File.Exists(p)) return p;
            }
            Match m = Regex.Match(name, @"^(.*_)(\d+)$");
            if (m.Success)
            {
                int n = int.Parse(m.Groups[2].Value);
                string w = new string('0', m.Groups[2].Value.Length);
                // n+1, n-1, then the nearest lower TPL (head/neck/hands that follow their body BIN)
                List<int> order = new List<int> { n + 1, n - 1 };
                for (int k = n - 2; k >= 0 && k >= n - 40; k--) order.Add(k);
                foreach (int k in order)
                {
                    if (k < 0) continue;
                    foreach (string ext in new[] { ".TPL", ".tpl" })
                    {
                        string p = Path.Combine(dir, m.Groups[1].Value + k.ToString(w) + ext);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            return null;
        }

        private void AddRow(PartRow row, bool check)
        {
            try
            {
                row.Mesh = BinPreview.Load(row.Bin, row.Tpl);
                row.Mesh.Bind(store);
            }
            catch (Exception ex) { Log("Cannot read " + Path.GetFileName(row.Bin) + ": " + ex.Message); return; }

            ListViewItem it = new ListViewItem(Path.GetFileName(row.Bin));
            it.SubItems.Add(Path.GetFileName(row.Tpl));
            it.SubItems.Add(row.Desc.Length > 0 ? row.Desc + " / " + row.Mesh.VertexCount + "v" : row.Mesh.VertexCount + " vertices, " + row.Mesh.GroupCount + " mat");
            it.SubItems.Add("");
            it.SubItems.Add(row.RigidBone.ToString());
            it.Tag = row;
            it.Checked = check;
            list.Items.Add(it);
            UpdateTexColumn(it);

            if (txtOutput.Text.Length == 0)
            {
                txtOutput.Text = Path.Combine(Path.GetDirectoryName(row.Bin), "merged", Path.GetFileNameWithoutExtension(row.Bin) + "_merged");
            }
        }

        // swatch color = color of the part in the "Parts" view (position in the list)
        private void RefreshRows()
        {
            for (int i = 0; i < list.Items.Count; i++) list.Items[i].ImageIndex = i % 16;
        }

        // "Tex" column: how many of the textures the part uses were found in the textures folder
        private void UpdateTexColumn(ListViewItem it)
        {
            PartRow row = (PartRow)it.Tag;
            var used = row.Mesh.UsedTextures();
            if (used.Count == 0) { it.SubItems[3].Text = "-"; return; }
            if (!store.HasFolder) { it.SubItems[3].Text = "?"; return; }
            int found = used.Count(u => store.Get(u.Key, u.Value) != null);
            it.SubItems[3].Text = found + "/" + used.Count;
            it.SubItems[3].ForeColor = found == used.Count ? Color.DarkGreen : Color.Firebrick;
        }

        // list file (BIN - TPL txt, or the enemy list page saved as html/mhtml) + the folder with the extracted files
        private void LoadListAndFolder()
        {
            string listPath;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Select the list (.txt or the saved enemy list page .html / .mhtml)";
                d.Filter = "Lists|*.txt;*.html;*.htm;*.mhtml;*.mht|All files|*.*";
                if (d.ShowDialog() != DialogResult.OK) return;
                listPath = d.FileName;
            }
            string folder;
            using (FolderBrowserDialog f = new FolderBrowserDialog())
            {
                f.Description = "Folder with the extracted BIN / TPL files of this model (example: pl08_000.BIN or em2b_001.BIN)";
                if (f.ShowDialog() != DialogResult.OK) return;
                folder = f.SelectedPath;
            }

            // prefix = most common "xxx_" in front of the number
            Regex rx = new Regex(@"^(.*_)(\d+)\.(bin|tpl)$", RegexOptions.IgnoreCase);
            var groups = Directory.GetFiles(folder).Select(p => rx.Match(Path.GetFileName(p))).Where(x => x.Success)
                .GroupBy(x => x.Groups[1].Value).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0) { MessageBox.Show("No files like name_000.BIN found in that folder."); return; }
            string prefix = groups[0].Key;

            // numbers -> real file paths (any amount of leading zeros)
            Dictionary<int, string> bins = new Dictionary<int, string>();
            Dictionary<int, string> tpls = new Dictionary<int, string>();
            foreach (string p in Directory.GetFiles(folder))
            {
                Match x = rx.Match(Path.GetFileName(p));
                if (!x.Success || x.Groups[1].Value != prefix) continue;
                int n = int.Parse(x.Groups[2].Value);
                if (x.Groups[3].Value.ToLowerInvariant() == "bin") bins[n] = p; else tpls[n] = p;
            }

            var sections = ListParser.ParseAny(listPath);
            List<ListEntry> entries = null;
            string wanted = prefix.TrimEnd('_').ToLowerInvariant();    // em2b, pl08, ...
            foreach (var kv in sections) { if (kv.Key == wanted) { entries = kv.Value; break; } }
            if (entries == null && sections.Count == 1) entries = sections[0].Value;
            if (entries == null)
            {
                MessageBox.Show("The list has no section named '" + wanted + "'. Sections: " + string.Join(", ", sections.Select(s => s.Key).Distinct().Take(60)));
                return;
            }

            int added = 0, missing = 0, guessed = 0;
            foreach (ListEntry e in entries)
            {
                string bin;
                if (!bins.TryGetValue(e.Bin, out bin)) { missing++; continue; }
                string tpl = null;
                if (e.Tpl >= 0) tpls.TryGetValue(e.Tpl, out tpl);
                string note = "";
                if (tpl == null)
                {
                    // the list does not tell the TPL: guess from the numbers, the user can fix it with "Set TPL"
                    tpl = TplGuess.ByNumber(e.Bin, tpls);
                    if (tpl != null) { note = " [TPL guessed]"; guessed++; }
                }
                if (tpl == null) { missing++; continue; }
                AddRow(new PartRow { Bin = bin, Tpl = tpl, Desc = e.Description + (string.IsNullOrEmpty(e.TplNote) ? "" : " (" + e.TplNote + ")") + note }, added == 0);
                added++;
            }
            RefreshRows();
            Log("List loaded: " + added + " parts added, " + missing + " entries without file/TPL, " + guessed + " TPL guessed (check them with Set TPL). Tick the parts you want to merge (first one = skeleton).");
            UpdatePreview();
        }

        // ------------------------------------------------------------------ textures

        private void BrowseTextures()
        {
            using (FolderBrowserDialog f = new FolderBrowserDialog())
            {
                f.Description = "Folder with the DDS / TGA textures of the model (example: the extracted 00000015 folder with 0051.dds ...)";
                if (store.HasFolder) f.SelectedPath = store.Folder;
                if (f.ShowDialog() == DialogResult.OK) SetTexturesFolder(f.SelectedPath);
            }
        }

        public void SetTexturesFolder(string folder)
        {
            store.SetFolder(folder);
            ReBindAll();
            Log("Textures folder: " + folder);

            // report how many of the textures the parts ask for were found
            HashSet<ulong> seen = new HashSet<ulong>();
            int found = 0, total = 0;
            List<string> problems = new List<string>();
            foreach (MeshData m in AllMeshes())
            {
                foreach (var u in m.UsedTextures())
                {
                    if (!seen.Add(TextureStore.Key(u.Key, u.Value))) continue;
                    total++;
                    if (store.Get(u.Key, u.Value) != null) found++;
                    else problems.Add("  pack " + u.Key + " texture " + u.Value.ToString("D4") + ": " + store.Problem(u.Key, u.Value));
                }
            }
            if (total > 0)
            {
                Log("Textures found: " + found + " of " + total + " used by the parts.");
                foreach (string p in problems.Take(12)) Log(p);
            }
            chkTextures.Checked = true;
            OnTexturesToggled();
        }

        private IEnumerable<MeshData> AllMeshes()
        {
            foreach (ListViewItem it in list.Items) yield return ((PartRow)it.Tag).Mesh;
            if (mergedMesh != null) yield return mergedMesh;
        }

        private void ReBindAll()
        {
            foreach (MeshData m in AllMeshes()) m.Bind(store);
            foreach (ListViewItem it in list.Items) UpdateTexColumn(it);
            UpdateStatus();
        }

        private void OnTexturesToggled()
        {
            if (chkTextures.Checked && !store.HasFolder)
            {
                BrowseTextures();
                if (!store.HasFolder) { chkTextures.Checked = false; return; }
            }
            preview.ShowTextures = chkTextures.Checked && store.HasFolder;
            UpdatePreview();
        }

        // ------------------------------------------------------------------ list editing

        private void Move(int dir)
        {
            if (list.SelectedItems.Count != 1) return;
            ListViewItem it = list.SelectedItems[0];
            int i = it.Index, j = i + dir;
            if (j < 0 || j >= list.Items.Count) return;
            list.Items.RemoveAt(i);
            list.Items.Insert(j, it);
            it.Selected = true;
            RefreshRows();
            UpdatePreview();
        }

        private void RemoveSelected()
        {
            foreach (ListViewItem it in list.SelectedItems.Cast<ListViewItem>().ToList()) list.Items.Remove(it);
            RefreshRows();
            UpdatePreview();
            UpdateStatus();
        }

        // applies to all selected rows (hands/necks are shared: use the TPL of the body they are merged with)
        private void SetTpl()
        {
            if (list.SelectedItems.Count == 0) return;
            PartRow first = (PartRow)list.SelectedItems[0].Tag;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "TPL for the " + list.SelectedItems.Count + " selected part(s)";
                d.Filter = "RE4 TPL|*.tpl;*.TPL";
                d.InitialDirectory = Path.GetDirectoryName(first.Tpl);
                if (d.ShowDialog() != DialogResult.OK) return;
                foreach (ListViewItem it in list.SelectedItems)
                {
                    PartRow row = (PartRow)it.Tag;
                    row.Tpl = d.FileName;
                    it.SubItems[1].Text = Path.GetFileName(row.Tpl);
                    try
                    {
                        row.Mesh = BinPreview.Load(row.Bin, row.Tpl);   // the textures depend on the TPL
                        row.Mesh.Bind(store);
                    }
                    catch (Exception ex) { Log("Cannot read " + Path.GetFileName(row.Bin) + ": " + ex.Message); }
                    UpdateTexColumn(it);
                }
            }
            UpdatePreview();
        }

        private void SetRigid()
        {
            if (list.SelectedItems.Count != 1) return;
            ListViewItem it = list.SelectedItems[0];
            PartRow row = (PartRow)it.Tag;
            string v = Prompt("Bone ID used when this part has no weights\n(only matters if another part is weighted):", row.RigidBone.ToString());
            byte b;
            if (v != null && byte.TryParse(v, out b))
            {
                row.RigidBone = b;
                it.SubItems[4].Text = b.ToString();
            }
        }

        private static string Prompt(string text, string def)
        {
            using (Form f = new Form())
            {
                f.Text = "Rigid bone"; f.Width = 380; f.Height = 170; f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
                Label l = new Label { Text = text, Left = 10, Top = 10, Width = 350, Height = 40 };
                TextBox t = new TextBox { Left = 10, Top = 55, Width = 100, Text = def };
                Button ok = new Button { Text = "OK", Left = 270, Top = 90, DialogResult = DialogResult.OK };
                f.Controls.AddRange(new Control[] { l, t, ok });
                f.AcceptButton = ok;
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }

        // ------------------------------------------------------------------ recipe

        private void OpenRecipe()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Merge recipe|*.txt";
                if (d.ShowDialog() == DialogResult.OK) LoadRecipeFile(d.FileName);
            }
        }

        public void LoadRecipeFile(string path)
        {
            try
            {
                List<PartInput> parts = new List<PartInput>();
                MergeOptions o = new MergeOptions();
                string output = null;
                Recipe.Load(path, parts, o, ref output);
                list.Items.Clear();
                mergedMesh = null;
                if (!string.IsNullOrEmpty(o.TexturesFolder) && Directory.Exists(o.TexturesFolder)) store.SetFolder(o.TexturesFolder);
                foreach (PartInput p in parts) AddRow(new PartRow { Bin = p.BinPath, Tpl = p.TplPath, RigidBone = p.RigidBone }, true);
                RefreshRows();
                chkJoin.Checked = o.MergeSameMaterials;
                chkTransparent.Checked = o.TransparentLast;
                chkLimit.Checked = o.LimitWeightMaps;
                chkObj.Checked = o.ExportPreviewObj;
                txtOutput.Text = output;
                if (store.HasFolder) { chkTextures.Checked = true; preview.ShowTextures = true; }
                UpdatePreview();
                UpdateStatus();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Recipe error"); }
        }

        private void SaveRecipe()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Merge recipe|*.merge.txt";
                d.FileName = "recipe.merge.txt";
                if (d.ShowDialog() != DialogResult.OK) return;
                Recipe.Save(d.FileName, GetParts(true), GetOptions(), txtOutput.Text);
            }
        }

        // ------------------------------------------------------------------ merge

        private List<PartInput> GetParts(bool onlyChecked)
        {
            List<PartInput> parts = new List<PartInput>();
            foreach (ListViewItem it in list.Items)
            {
                if (onlyChecked && !it.Checked) continue;
                PartRow r = (PartRow)it.Tag;
                string name = Path.GetFileName(r.Bin) + (r.Desc.Length > 0 ? " [" + r.Desc + "]" : "");
                parts.Add(new PartInput { Name = name, BinPath = r.Bin, TplPath = r.Tpl, RigidBone = r.RigidBone });
            }
            return parts;
        }

        private MergeOptions GetOptions()
        {
            return new MergeOptions
            {
                MergeSameMaterials = chkJoin.Checked,
                TransparentLast = chkTransparent.Checked,
                LimitWeightMaps = chkLimit.Checked,
                ExportPreviewObj = chkObj.Checked,
                TexturesFolder = store.HasFolder ? store.Folder : null
            };
        }

        public void ClickMerge() { DoMerge(); }

        public void ShowMerged(bool textured)
        {
            rbMerged.Checked = true;
            if (textured && store.HasFolder) { chkTextures.Checked = true; preview.ShowTextures = true; }
            UpdatePreview();
        }

        private void BrowseOutput()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Output name (extension is added automatically)";
                d.FileName = Path.GetFileName(txtOutput.Text);
                if (d.ShowDialog() == DialogResult.OK) txtOutput.Text = Path.Combine(Path.GetDirectoryName(d.FileName), Path.GetFileNameWithoutExtension(d.FileName));
            }
        }

        private void DoMerge()
        {
            List<PartInput> parts = GetParts(true);
            if (parts.Count < 2) { MessageBox.Show("Tick at least 2 parts."); return; }
            if (txtOutput.Text.Trim().Length == 0) { MessageBox.Show("Choose an output name."); return; }

            txtLog.Clear();
            Cursor = Cursors.WaitCursor;
            try
            {
                StringWriter sw = new StringWriter();
                MergeResult r = BinMerger.Merge(parts, txtOutput.Text, GetOptions(), sw);
                Log(sw.ToString());
                Log(r.Success ? "OK" : "FINISHED WITH PROBLEMS (see report)");
                Log("BIN: " + r.BinPath);
                Log("TPL: " + r.TplPath);
                Log("Report: " + r.ReportPath);

                mergedMesh = BinPreview.Load(r.BinPath, r.TplPath);
                mergedMesh.Bind(store);
                lastOutDir = Path.GetDirectoryName(r.BinPath);
                btnOpenFolder.Enabled = true;
                rbMerged.Checked = true;
                UpdatePreview();
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show(ex.Message, "Merge failed");
            }
            finally { Cursor = Cursors.Default; }
        }

        private void OpenOutputFolder()
        {
            if (lastOutDir != null) Process.Start("explorer.exe", lastOutDir);
        }

        // ------------------------------------------------------------------ preview / log

        private void CycleBackground()
        {
            bgIndex = (bgIndex + 1) % Backgrounds.Length;
            preview.SetBackground(Backgrounds[bgIndex]);
        }

        private void SavePng()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "PNG image|*.png";
                d.FileName = "preview.png";
                if (d.ShowDialog() != DialogResult.OK) return;
                preview.SavePng(d.FileName);
                Log("Saved image: " + d.FileName);
            }
        }

        private void UpdatePreview()
        {
            if (preview == null || rbMerged == null) return;
            List<PreviewControl.Item> items = new List<PreviewControl.Item>();

            if (rbMerged.Checked)
            {
                if (mergedMesh != null)
                {
                    items.Add(new PreviewControl.Item { Mesh = mergedMesh, ColorByGroup = true });
                    preview.Message = "Merged: " + mergedMesh.VertexCount + " vertices, " + mergedMesh.GroupCount + " materials";
                }
                else preview.Message = "Press MERGE to see the result";
            }
            else
            {
                int idx = 0;
                foreach (ListViewItem it in list.Items)
                {
                    if (it.Checked || it.Selected)
                    {
                        PartRow r = (PartRow)it.Tag;
                        items.Add(new PreviewControl.Item { Mesh = r.Mesh, Color = PreviewControl.PartColor(idx) });
                    }
                    idx++;
                }
                preview.Message = items.Count + " part(s) shown";
            }
            preview.SetItems(items);
        }

        private void Log(string s)
        {
            txtLog.AppendText(s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine);
        }
    }
}
