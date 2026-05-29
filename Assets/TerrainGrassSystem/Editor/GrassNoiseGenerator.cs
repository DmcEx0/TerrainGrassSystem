using System.IO;
using UnityEditor;
using UnityEngine;

namespace TerrainGrassSystem.Editor
{
    // Generates two starter textures the runtime needs:
    //   - a tileable Perlin noise (for clumping AND for wind)
    //   - a blank density mask (R = 1, GBA = type 0)
    // Both are saved as PNG and re-imported with sensible import settings.
    public class GrassNoiseGenerator : EditorWindow
    {
        static readonly int[] kSizes = { 64, 128, 256, 512, 1024, 2048, 4096 };
        static readonly string[] kSizeLabels = { "64", "128", "256", "512", "1024", "2048", "4096" };
        const string kDefaultFolder = "Assets/";

        // The kind of texture this window produces. The selection drives which
        // controls are shown below.
        public enum TextureType { ClumpNoise, DensityMask }

        // Base frequencies — preserved from the original code so the three
        // channels keep their distinct character (R = large clumps for
        // height, G = mid for colour, B = fine for facing direction). The
        // Scale slider multiplies all three uniformly.
        static readonly float[] kBaseFreqs = { 4f, 9f, 16f };

        // ---- Shared params -----------------------------------------------------

        [SerializeField] TextureType _textureType  = TextureType.ClumpNoise;
        // Project-relative folder ("Assets/...") textures are exported into.
        [SerializeField] string      _exportFolder = kDefaultFolder;

        // ---- Noise generation params (serialized so window state sticks) ----

        [SerializeField] int   _noiseSizeIndex = 2;   // 256
        [SerializeField] int   _noiseSeed      = 1337;
        [SerializeField] float _scale          = 1f;  // multiplies base frequency
        [SerializeField] int   _octaves        = 3;   // fBm layers
        [SerializeField] float _persistence    = 0.5f;
        [SerializeField] float _lacunarity     = 2f;
        [SerializeField] float _contrast       = 1f;  // 1 = neutral, 0 = flat, >1 = harsh
        [SerializeField] float _brightness     = 0f;  // -1..1 additive shift

        // ---- Mask params ----

        [SerializeField] int _maskSizeIndex = 4;      // 1024

        // ---- Live preview ----

        const int kPreviewSize = 192;
        Texture2D _preview;
        bool      _previewDirty = true;

        // Cached params from the last preview rebuild so we know when to
        // regenerate (every slider drag fires OnGUI multiple times).
        NoiseParams _previewLastParams;
        int         _previewLastSeed = int.MinValue;

        public struct NoiseParams
        {
            public float scale;
            public int   octaves;
            public float persistence;
            public float lacunarity;
            public float contrast;
            public float brightness;

            public bool Equals(NoiseParams o) =>
                scale == o.scale && octaves == o.octaves && persistence == o.persistence
                && lacunarity == o.lacunarity && contrast == o.contrast && brightness == o.brightness;
        }

        [MenuItem("Tools/GrassSystem/Noise & Mask Generator")]
        public static void Open()
        {
            var w = GetWindow<GrassNoiseGenerator>(true, "Grass Noise & Mask", true);
            w.minSize = new Vector2(360, 560);
        }

        void OnDisable()
        {
            if (_preview != null) { DestroyImmediate(_preview); _preview = null; }
        }

        // ---- GUI ----------------------------------------------------------------

        Vector2 _scroll;

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // Texture type drives the whole layout below it.
            _textureType = (TextureType)EditorGUILayout.EnumPopup(
                new GUIContent("Texture Type", "Which kind of texture to generate. Changes the controls shown below."),
                _textureType);

            DrawExportFolderField();

            EditorGUILayout.Space(8);

            switch (_textureType)
            {
                case TextureType.ClumpNoise:  DrawClumpNoiseSection(); break;
                case TextureType.DensityMask: DrawDensityMaskSection(); break;
            }

            EditorGUILayout.EndScrollView();
        }

        // Export folder picker — shared by both texture types.
        void DrawExportFolderField()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Export", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _exportFolder = EditorGUILayout.TextField(
                    new GUIContent("Folder", "Project-relative folder textures are exported into. Must live under Assets/."),
                    _exportFolder);

                if (GUILayout.Button("Browse…", EditorStyles.miniButton, GUILayout.Width(70)))
                {
                    string picked = BrowseForProjectFolder();
                    if (!string.IsNullOrEmpty(picked))
                    {
                        _exportFolder = picked;
                        GUI.FocusControl(null); // refresh the text field display
                    }
                }
            }

            if (!IsValidProjectFolder(_exportFolder))
                EditorGUILayout.HelpBox("Folder must be inside the project's Assets directory.", MessageType.Warning);
        }

        void DrawClumpNoiseSection()
        {
            EditorGUILayout.LabelField("Clump Noise (Perlin)", EditorStyles.boldLabel);
            _noiseSizeIndex = EditorGUILayout.Popup("Output Size", _noiseSizeIndex, kSizeLabels);
            _noiseSeed      = EditorGUILayout.IntField(new GUIContent("Seed", "Re-roll to get a different pattern with the same settings."), _noiseSeed);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Re-roll Seed", EditorStyles.miniButton, GUILayout.Width(120)))
                    _noiseSeed = Random.Range(1, int.MaxValue);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Shape", EditorStyles.boldLabel);
            _scale       = EditorGUILayout.Slider(new GUIContent("Scale",       "Density of features — higher = finer pattern."),                                _scale,       0.1f, 8f);
            _octaves     = EditorGUILayout.IntSlider(new GUIContent("Octaves",  "Number of stacked layers. More layers = richer / busier noise."),               _octaves,     1, 6);
            _persistence = EditorGUILayout.Slider(new GUIContent("Persistence", "How loud each higher layer is. Low = smooth, high = noisy."),                   _persistence, 0f, 1f);
            _lacunarity  = EditorGUILayout.Slider(new GUIContent("Lacunarity",  "Frequency growth per layer. 2 = standard fBm."),                                _lacunarity,  1.1f, 4f);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Tone", EditorStyles.boldLabel);
            _contrast   = EditorGUILayout.Slider(new GUIContent("Contrast",   "Sharpness of light/dark transitions. 1 = neutral, 0 = flat grey, >1 = harsh."), _contrast,   0f, 3f);
            _brightness = EditorGUILayout.Slider(new GUIContent("Brightness", "Shifts the whole texture lighter/darker after contrast."),                       _brightness, -0.5f, 0.5f);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reset to defaults", EditorStyles.miniButton, GUILayout.Width(140)))
                {
                    _scale = 1f; _octaves = 3; _persistence = 0.5f; _lacunarity = 2f;
                    _contrast = 1f; _brightness = 0f;
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Preview (RGB shown as colour, channels are independent)", EditorStyles.boldLabel);
            DrawPreview();

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(!IsValidProjectFolder(_exportFolder)))
            {
                if (GUILayout.Button("Generate Clump Noise", GUILayout.Height(24)))
                    GenerateClumpNoise(_exportFolder, kSizes[_noiseSizeIndex], _noiseSeed, BuildParams());
            }
        }

        void DrawDensityMaskSection()
        {
            EditorGUILayout.LabelField("Blank Density Mask", EditorStyles.boldLabel);
            _maskSizeIndex = EditorGUILayout.Popup("Size", _maskSizeIndex, kSizeLabels);

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(!IsValidProjectFolder(_exportFolder)))
            {
                if (GUILayout.Button("Generate Blank Density Mask", GUILayout.Height(24)))
                    GenerateBlankMask(_exportFolder, kSizes[_maskSizeIndex]);
            }
        }

        void DrawPreview()
        {
            EnsurePreview();
            var rect = GUILayoutUtility.GetRect(kPreviewSize, kPreviewSize, GUILayout.ExpandWidth(false));
            // Center horizontally.
            rect.x = Mathf.Max(rect.x, (position.width - kPreviewSize) * 0.5f);
            EditorGUI.DrawPreviewTexture(rect, _preview);
        }

        NoiseParams BuildParams() => new NoiseParams
        {
            scale       = _scale,
            octaves     = _octaves,
            persistence = _persistence,
            lacunarity  = _lacunarity,
            contrast    = _contrast,
            brightness  = _brightness,
        };

        void EnsurePreview()
        {
            if (_preview == null)
            {
                _preview = new Texture2D(kPreviewSize, kPreviewSize, TextureFormat.RGBA32, false, true)
                { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                _previewDirty = true;
            }

            var current = BuildParams();
            if (!_previewDirty && current.Equals(_previewLastParams) && _previewLastSeed == _noiseSeed)
                return;

            BuildPerlinPixels(_preview, kPreviewSize, _noiseSeed, current);
            _previewLastParams = current;
            _previewLastSeed   = _noiseSeed;
            _previewDirty      = false;
        }

        // ---- Public generators -------------------------------------------------

        public static void GenerateClumpNoise(string folder, int size, int seed, NoiseParams p)
        {
            EnsureFolder(folder);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/GrassClumpNoise_{size}.png");

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            BuildPerlinPixels(tex, size, seed, p);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer != null)
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.sRGBTexture = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            PingGenerated(path);
        }

        public static void GenerateBlankMask(string folder, int size)
        {
            EnsureFolder(folder);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/GrassDensityMask_{size}.png");

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[size * size];
            // R = placement (EMPTY — no grass until painted).
            // G = height multiplier (FULL — when grass is painted it grows at full height).
            // B = clamp / tufting (EMPTY — opt-in via the Clamp brush; default
            //     is single-blade grass, no tufts).
            // A = reserved.
            for (int i = 0; i < pixels.Length; ++i) pixels[i] = new Color32(0, 255, 0, 0);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);

            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer != null)
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.sRGBTexture = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            PingGenerated(path);
        }

        // ---- Folder helpers ----------------------------------------------------

        // Selects the generated asset in the Project window so the user sees
        // where the texture landed.
        static void PingGenerated(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }
            Debug.Log($"[Grass] Exported texture to {path}");
        }

        // Opens a native folder picker and converts the absolute result back to
        // a project-relative "Assets/..." path. Returns null if the user cancels
        // or picks a folder outside the project.
        static string BrowseForProjectFolder()
        {
            // Always open at the project's Assets folder.
            string abs = EditorUtility.OpenFolderPanel("Choose export folder", Application.dataPath, "");
            if (string.IsNullOrEmpty(abs)) return null;

            abs = abs.Replace('\\', '/');
            string projectRoot = Application.dataPath.Replace('\\', '/'); // ".../Assets"
            projectRoot = projectRoot.Substring(0, projectRoot.Length - "Assets".Length); // ".../"

            if (!abs.StartsWith(projectRoot, System.StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog(
                    "Folder outside project",
                    "Textures must be exported into the project's Assets folder.\n\nPlease pick a folder inside the project.",
                    "OK");
                return null;
            }

            string rel = abs.Substring(projectRoot.Length).TrimEnd('/');
            return rel;
        }

        static bool IsValidProjectFolder(string assetsRelativePath)
        {
            return !string.IsNullOrEmpty(assetsRelativePath)
                && (assetsRelativePath == "Assets"
                    || assetsRelativePath.StartsWith("Assets/", System.StringComparison.Ordinal));
        }

        static void EnsureFolder(string assetsRelativePath)
        {
            if (AssetDatabase.IsValidFolder(assetsRelativePath)) return;
            var parts = assetsRelativePath.Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; ++i)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        // ---- Noise math --------------------------------------------------------

        static void BuildPerlinPixels(Texture2D dest, int size, int seed, NoiseParams p)
        {
            var pixels = new Color32[size * size];

            // Three independent offsets — keeps the channels decorrelated even
            // when their base frequencies happen to align.
            Random.InitState(seed);
            float ox = Random.value * 1000f;
            float oy = Random.value * 1000f;
            float[] dxs = { 0f, 100f, 250f };
            float[] dys = { 0f,  70f,  30f };

            for (int y = 0; y < size; ++y)
            for (int x = 0; x < size; ++x)
            {
                float u = x / (float)size;
                float v = y / (float)size;

                float r = Fbm(u, v, kBaseFreqs[0] * p.scale, p.octaves, p.persistence, p.lacunarity, ox + dxs[0], oy + dys[0]);
                float g = Fbm(u, v, kBaseFreqs[1] * p.scale, p.octaves, p.persistence, p.lacunarity, ox + dxs[1], oy + dys[1]);
                float b = Fbm(u, v, kBaseFreqs[2] * p.scale, p.octaves, p.persistence, p.lacunarity, ox + dxs[2], oy + dys[2]);

                r = Tone(r, p.contrast, p.brightness);
                g = Tone(g, p.contrast, p.brightness);
                b = Tone(b, p.contrast, p.brightness);

                pixels[y * size + x] = new Color32(
                    (byte)(r * 255f),
                    (byte)(g * 255f),
                    (byte)(b * 255f),
                    255);
            }

            dest.SetPixels32(pixels);
            dest.Apply(false, false);
        }

        // Fractal Brownian motion — stacks `octaves` of tileable Perlin with
        // amplitude shrinking by `persistence` and frequency growing by
        // `lacunarity` each step. Result is normalised to 0..1.
        static float Fbm(float u, float v, float baseFreq, int octaves, float persistence, float lacunarity, float ox, float oy)
        {
            float sum   = 0f;
            float amp   = 1f;
            float freq  = baseFreq;
            float total = 0f;

            for (int o = 0; o < octaves; ++o)
            {
                sum   += amp * Octave(u, v, freq, ox, oy);
                total += amp;
                amp   *= persistence;
                freq  *= lacunarity;
            }
            return total > 1e-6f ? Mathf.Clamp01(sum / total) : 0f;
        }

        // S-curve around 0.5 then bias. Contrast 0 -> flat grey, 1 -> identity,
        // >1 -> values pushed toward 0 or 1.
        static float Tone(float x, float contrast, float brightness)
        {
            return Mathf.Clamp01((x - 0.5f) * contrast + 0.5f + brightness);
        }

        // Tileable Perlin via 4-corner blend.
        static float Octave(float u, float v, float freq, float ox, float oy)
        {
            float x = u * freq;
            float y = v * freq;

            float a = Mathf.PerlinNoise(x + ox,          y + oy);
            float b = Mathf.PerlinNoise(x + ox - freq,   y + oy);
            float c = Mathf.PerlinNoise(x + ox,          y + oy - freq);
            float d = Mathf.PerlinNoise(x + ox - freq,   y + oy - freq);

            float top = Mathf.Lerp(a, b, u);
            float bot = Mathf.Lerp(c, d, u);
            return Mathf.Clamp01(Mathf.Lerp(top, bot, v));
        }
    }
}
