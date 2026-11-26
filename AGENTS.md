# AGENTS.md - guide for AI assistants and contributors

Read this first. It explains what the project is, how it is laid out, how to build and test it without Windows, and what not to break.

## What this is
**RE4 UHD BIN + TPL Merge Tool** (repo: `YEMENI-modder/Re4UhdBinMerge`). A Windows tool for the Resident Evil 4 (2007 PC, "UHD" mod format) that merges several model parts (body, head, neck, hands, head cover ...) - each a `.BIN` mesh with a `.TPL` texture table - into ONE `.BIN` + ONE `.TPL` that the game can load as a single model.
Only the **UHD** BIN/TPL format is supported. Not the original/Wii/PS formats.

- Target: C# on **.NET Framework 4.8**, WinForms, single `.exe`, no NuGet packages (all libraries are vendored in `src/Lib/`).
- Authorship: the code was written entirely by Claude Sonnet 5.5 (Anthropic); the owner (YEMENI-modder) did the testing and direction. Keep the MIT license and the credits.

## Layout
| Path | Purpose |
|---|---|
| `src/` | **all C# code and the csproj** (`src/RE4_UHD_BIN_MERGE.csproj`); the files below are inside it |
| `Program.cs` | entry point: GUI when no args or `--gui`, otherwise command line (recipe or `-o out parts...`) |
| `BinMerger.cs` | **the core**: `BinMerger.Merge(parts, output, options, log)` -> `MergeResult`. No UI code here |
| `Recipe.cs` | read/write `.merge.txt` recipes (`Output`, `Part: bin, tpl[, bone=N]`, options, `Textures`) |
| `Preview.cs` | `MeshData` + `BinPreview.Load(bin, tpl)`: triangles, UVs, per-material texture ids; OBJ export |
| `PreviewControl.cs` | software z-buffer 3D viewer (no GPU/OpenGL), mip-mapped bilinear texturing, opacity alpha-test |
| `TextureStore.cs` | finds/decodes DDS and TGA (`{id:0000}.dds\|tga`, or `{pack:00000000}/...`), builds mips |
| `MainForm.cs` | the whole window (built in code, no designer) |
| `Lib/` (in `src/`) | vendored MIT libraries - see `THIRD_PARTY_LICENSES.md`. Do not edit except to fix real bugs |
| `Examples/` | sample recipes (game files are NOT in the repo) |
| `docs/` | screenshots + sample merge reports |
| `.github/workflows/build.yml` | CI: builds on Windows, uploads zip, attaches it to a release on `v*` tags |

## How the merge works (BinMerger.cs)
1. Read every part (BIN + TPL) with JADERLINK's decoders (`UhdBinDecoder`, `UhdTplDecoder`).
2. **TPL**: textures of all parts are put in one list; identical entries (same PackID + TextureID) are joined. Each material's `diffuse_map` / `opacity_map` index is remapped (`0xFF` = no texture). A material can only address 0-254, so more than 255 textures triggers a warning.
3. **Bones**: the FIRST ticked part defines the skeleton; bones are matched by BoneID. A rigid part (no weights) gets a fixed bone (`bone=N` in the recipe, GUI "Rigid bone...") when other parts are weighted.
4. **Weight maps**: combined; the base game accepts at most 255 maps. With `LimitWeightMaps` the weights are rounded in steps until they fit (small skinning change). Turn off only for the Qingsheng DLL.
5. Materials that become identical are joined (`MergeSameMaterials`); `TransparentLast` puts materials with an opacity map at the end.
6. Writes BIN + TPL + a text report (+ optional `_preview.obj`), then **reads the files back** to verify. Keep that verification.

## Build
Windows / CI: `dotnet build src/RE4_UHD_BIN_MERGE.csproj -c Release` (needs the .NET SDK; the csproj targets `net48`, `UseWindowsForms`, `LangVersion latest`).

### Build and test on Linux (how the author's AI did it)
- Mono's `mcs` CANNOT compile this code (it fails on tuple arrays in the JADERLINK library). Use Roslyn from the .NET SDK against Mono's 4.8 reference assemblies:
  ```bash
  apt-get install -y dotnet-sdk-8.0 mono-devel libmono-system-windows-forms4.0-cil libgdiplus xvfb imagemagick
  REF=/usr/lib/mono/4.8-api
  dotnet /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll -nologo -langversion:latest -target:winexe -out:RE4_UHD_BIN_MERGE.exe \
    -noconfig -nostdlib -r:$REF/mscorlib.dll -r:$REF/System.dll -r:$REF/System.Core.dll -r:$REF/System.Drawing.dll \
    -r:$REF/System.Windows.Forms.dll -r:$REF/Facades/System.Runtime.dll -r:$REF/Facades/System.ValueTuple.dll $(find src -name "*.cs")
  ```
- CLI test: `mono RE4_UHD_BIN_MERGE.exe Examples/em15_female.merge.txt` (needs the game's `em15_*.BIN/TPL` next to the recipe).
- GUI test + screenshot: `Xvfb :99 &`, `DISPLAY=:99 mono RE4_UHD_BIN_MERGE.exe --gui recipe.merge.txt --auto-merge --show-merged`, then `import -window root shot.png`.
- Mono draws WinForms with different fonts than Windows; leave room in layouts (buttons use `AutoSize`).

## Conventions and gotchas
- Keep `BinMerger` free of UI code; the GUI and the CLI both call it.
- WinForms layout is code-only. `Dock`: add the `Fill` control first, then the edge controls.
- Preview UVs: `v = 0` is the TOP row of the texture (OBJ export writes `1 - v`).
- Textures are only for the preview; the merge never reads or writes texture files.
- Supported texture formats: DXT1-5 and uncompressed DDS, TGA. DX10-header DDS (BC7 ...) is not supported by DDSReaderSharp.
- **Never commit game assets** (`*.BIN`, `*.TPL`, `*.dds`, `*.tga`); `.gitignore` blocks them. Screenshots are fine.
- Not tested yet: loading the merged model inside the game, and merges of enemies other than Leon (pl08) and em15.

## Ideas for contributors
Rigid-part bone picker in the preview, drag-sort in the list, more enemies in the examples, DX10 DDS support, a headless "render PNG" CLI option.
