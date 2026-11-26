using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RE4BinMerge
{
    internal static class Program
    {
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

        private const string Usage =
@"RE4 UHD BIN+TPL MERGE TOOL
(uses the BIN/TPL code of JADERLINK, MIT license)

No arguments = opens the window (GUI).
  --gui [recipe.merge.txt] [--textures folder] [--auto-merge] [--show-merged]
      opens the window with a recipe loaded (the other options are handy for screenshots).

COMMAND LINE
  RE4_UHD_BIN_MERGE.exe recipe.merge.txt
  RE4_UHD_BIN_MERGE.exe -o pl08_merged  body.bin body.tpl  head.bin head.tpl  hand.bin hand.tpl
  options: --transparent-last   --no-join   --no-obj   --no-weight-limit

RECIPE FORMAT (.merge.txt)
  # comment
  Output: pl08_merged                 (name without extension, relative to the recipe folder)
  MergeSameMaterials: true            (default true)
  TransparentLast: false              (default false)
  LimitWeightMaps: true               (default true: round weights if more than 255 combinations)
  PreviewObj: true                    (default true)
  Textures: 00000015                  (optional, GUI preview only: folder with the DDS/TGA files)
  Part: pl0800.bin, pl0801.tpl                 (BIN, TPL - the FIRST part decides the skeleton)
  Part: pl0814.bin, pl0813.tpl, bone=12        (bone=N : bone for rigid parts when others are weighted)
";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--gui")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm form = new MainForm();

                // --gui [recipe.merge.txt] [--textures folder] [--auto-merge] [--show-merged]
                string recipe = null, textures = null;
                bool autoMerge = false, showMerged = false;
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--textures" && i + 1 < args.Length) textures = args[++i];
                    else if (args[i] == "--auto-merge") autoMerge = true;
                    else if (args[i] == "--show-merged") showMerged = true;
                    else if (!args[i].StartsWith("-")) recipe = args[i];
                }
                form.Shown += delegate
                {
                    if (recipe != null) form.LoadRecipeFile(recipe);
                    if (textures != null) form.SetTexturesFolder(textures);
                    if (autoMerge) form.ClickMerge();
                    if (showMerged) form.ShowMerged(true);
                };
                Application.Run(form);
                return 0;
            }

            try { AttachConsole(-1); } catch (Exception) { }
            return RunCli(args);
        }

        private static int RunCli(string[] args)
        {
            try
            {
                List<PartInput> parts = new List<PartInput>();
                MergeOptions options = new MergeOptions();
                string output = null;

                if (args.Length == 1 && !args[0].StartsWith("-"))
                {
                    if (args[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        Recipe.Load(args[0], parts, options, ref output);
                    }
                    else
                    {
                        Console.WriteLine(Usage);
                        return 1;
                    }
                }
                else
                {
                    List<string> files = new List<string>();
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] == "-o" && i + 1 < args.Length) { output = args[++i]; }
                        else if (args[i] == "--transparent-last") options.TransparentLast = true;
                        else if (args[i] == "--no-join") options.MergeSameMaterials = false;
                        else if (args[i] == "--no-obj") options.ExportPreviewObj = false;
                        else if (args[i] == "--no-weight-limit") options.LimitWeightMaps = false;
                        else files.Add(args[i]);
                    }
                    if (files.Count % 2 != 0) throw new ArgumentException("Files must come in pairs: BIN then TPL.");
                    for (int i = 0; i < files.Count; i += 2)
                    {
                        parts.Add(new PartInput { BinPath = files[i], TplPath = files[i + 1] });
                    }
                    if (output == null && parts.Count > 0)
                    {
                        output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(parts[0].BinPath)),
                            Path.GetFileNameWithoutExtension(parts[0].BinPath) + "_merged");
                    }
                }

                if (parts.Count < 2) throw new ArgumentException("At least 2 parts are needed.");

                MergeResult r = BinMerger.Merge(parts, output, options, Console.Out);

                Console.WriteLine();
                Console.WriteLine(r.Success ? "OK" : "FINISHED WITH PROBLEMS (see report)");
                Console.WriteLine("BIN    : " + r.BinPath);
                Console.WriteLine("TPL    : " + r.TplPath);
                Console.WriteLine("Report : " + r.ReportPath);
                if (r.ObjPath != null) Console.WriteLine("Preview: " + r.ObjPath);
                return r.Success ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                return 3;
            }
        }
    }
}
