using System.Collections.Generic;
using UnityEditor;
using UnityEditor.TerrainTools;
using UnityEngine;

namespace TerrainGrassSystem.Editor
{
    // Custom Terrain paint tool. Adds a "Paint Grass" icon to the Terrain
    // inspector's tool row — next to Paint Texture / Paint Trees / Paint
    // Details. When active, click & drag on the terrain to paint into the
    // GrassMask of the TerrainGrassSystem GrassTerrain attached to the same
    // GameObject.
    //
    // Undo/redo: each completed stroke (paint or Fill/Clear) is snapshotted
    // and can be reverted with Ctrl+Z while this tool is active. Ctrl+Shift+Z
    // and Ctrl+Y redo. History is in-memory only and lives for the editor
    // session (cleared on domain reload). Capped at kMaxUndoDepth strokes.
    public class GrassPaintTool : TerrainPaintTool<GrassPaintTool>
    {
        enum PaintMode
        {
            Placement, // R
            Height,    // G
            Spacing,   // B
        }
        static readonly string[] kModeLabels =
        {
            "Placement (R)",
            "Height (G)",
            "Spacing / Clamp (B)",
        };

        [SerializeField] PaintMode _mode = PaintMode.Placement;

        float _lastBrushSize = 10f;
        bool _ctrlHeld;
        Color[] _pixelScratch;

        // Deferred PNG persistence — see TickIdleFlush.
        string _maskAssetPath;
        bool   _strokeDirty;
        double _lastPaintTime;
        bool   _tickHooked;

        // Brush texture cache. Unity terrain brushes are often non-readable on
        // CPU, so we blit each new brush into a CPU-readable Texture2D and
        // sample it in ApplyBrush.
        Texture   _brushSrc;
        Texture2D _brushReadable;

        // ---- Undo ------------------------------------------------------------

        sealed class StrokeSnapshot
        {
            public string maskPath;
            public byte[] before;
            public byte[] after;
        }

        readonly List<StrokeSnapshot> _undoHistory = new();
        int _undoCursor;          // 0..Count; Count means "at the latest state"
        StrokeSnapshot _activeStroke;
        const int kMaxUndoDepth = 16;

        public override string GetName()        => "TerrainGrassSystem/Paint Grass";
        public override string GetDescription() => "Paint into the GrassTerrain mask: Placement (R), Height (G), Spacing (B). Hold Ctrl while painting to subtract the same channel. Ctrl+Z undoes the last stroke.";

        // ---- Inspector --------------------------------------------------------

        public override void OnInspectorGUI(Terrain terrain, IOnInspectorGUI editContext)
        {
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Mode", EditorStyles.boldLabel);
            _mode = (PaintMode)EditorGUILayout.Popup("Paint", (int)_mode, kModeLabels);
            EditorGUILayout.HelpBox(ModeHelp(_mode), MessageType.None);
            EditorGUILayout.HelpBox("Hold Ctrl while painting to subtract the current channel.", MessageType.Info);

            var gt = terrain != null ? terrain.GetComponent<GrassTerrain>() : null;
            if (gt == null)
                EditorGUILayout.HelpBox("Add a GrassTerrain component to this GameObject and assign a GrassMask before painting.", MessageType.Warning);
            else if (gt.GrassMask == null)
                EditorGUILayout.HelpBox("GrassTerrain.GrassMask is empty. Generate one via Tools/TerrainGrassSystem/Grass/Noise & Mask Generator — it starts blank, so the terrain stays grass-free until you paint.", MessageType.Warning);
            else
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Mask Utilities", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Clear All Grass", GUILayout.Height(22)))
                        ClearAllGrass(gt.GrassMask);
                    if (GUILayout.Button("Save Now", GUILayout.Height(22)))
                        FlushStroke();
                }

                EditorGUILayout.LabelField($"Undo history: {_undoCursor} / {_undoHistory.Count}   (Ctrl+Z / Ctrl+Shift+Z)", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Brush", EditorStyles.boldLabel);
            editContext.ShowBrushesGUI(5);

            if (EditorGUI.EndChangeCheck()) Save(true);
        }

        static string ModeHelp(PaintMode m) => m switch
        {
            PaintMode.Placement => "Paints the R channel — where grass is allowed to grow. Ctrl-paint removes grass.",
            PaintMode.Height    => "Paints the G channel — per-area grass height multiplier (0 = none, 1 = full).",
            PaintMode.Spacing   => "Paints the B channel — Clamp / tufting. By default each cell gets 1-2 blades (natural variation). Where B is painted, the count ramps up to 3-4 blades per root and they get a small height/brightness boost.",
            _ => string.Empty,
        };

        // ---- Scene GUI --------------------------------------------------------

        public override void OnSceneGUI(Terrain terrain, IOnSceneGUI editContext)
        {
            // Intercept Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y while the paint tool is
            // active. We swallow the event so Unity's own undo doesn't also
            // fire — without this, Unity could try to undo an unrelated
            // selection or transform change.
            var e = Event.current;
            if (e.type == EventType.KeyDown && (e.control || e.command))
            {
                if (e.keyCode == KeyCode.Z)
                {
                    if (e.shift) DoRedo(); else DoUndo();
                    e.Use();
                    return;
                }
                if (e.keyCode == KeyCode.Y && !e.shift)
                {
                    DoRedo();
                    e.Use();
                    return;
                }
            }

            // Track Ctrl so OnPaint can temporarily switch to erase. Modifier
            // state arrives via KeyDown/KeyUp and via the .control flag on
            // mouse events; reading e.control covers both.
            _ctrlHeld = e.control || e.command;

            // Force a repaint on modifier key changes so the preview brush
            // tint updates immediately.
            if (e.type == EventType.KeyDown || e.type == EventType.KeyUp)
                SceneView.RepaintAll();

            _lastBrushSize = editContext.brushSize;
            TerrainPaintUtilityEditor.ShowDefaultPreviewBrush(
                terrain, editContext.brushTexture, editContext.brushStrength);

            DrawBrushRadiusOnTerrain(terrain, editContext.brushStrength);
        }

        // Explicit on-terrain ring showing the brush radius. The default
        // textured projection above is subtle on dark/uniform terrains, so we
        // overlay a Handles disc that follows the surface normal under the
        // cursor. Inner faint disc visualizes the falloff core scaled by
        // brush strength.
        void DrawBrushRadiusOnTerrain(Terrain terrain, float brushStrength)
        {
            var e = Event.current;
            if (terrain == null) return;
            if (e.type != EventType.Repaint && e.type != EventType.MouseMove
                && e.type != EventType.MouseDrag && e.type != EventType.MouseDown) return;

            var collider = terrain.GetComponent<Collider>();
            if (collider == null) return;

            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (!collider.Raycast(ray, out var hit, 10000f)) return;

            // Force scene repaint while the cursor moves so the ring tracks
            // the mouse smoothly between paint events.
            if (e.type == EventType.MouseMove) SceneView.RepaintAll();

            float radius = Mathf.Max(0.01f, _lastBrushSize * 0.5f);

            Color ringColor = _ctrlHeld
                ? new Color(1.00f, 0.30f, 0.30f, 0.95f)   // red — erase
                : new Color(1.00f, 0.90f, 0.20f, 0.95f);  // yellow — paint

            var prev = Handles.color;
            Handles.color = ringColor;
            Handles.DrawWireDisc(hit.point, hit.normal, radius, 2f);

            // Inner faint ring shows the high-strength core of the brush
            // falloff. Skip if strength is tiny — visually noisy otherwise.
            if (brushStrength > 0.02f)
            {
                Handles.color = new Color(ringColor.r, ringColor.g, ringColor.b, 0.35f);
                Handles.DrawWireDisc(hit.point, hit.normal, radius * Mathf.Sqrt(brushStrength), 1f);
            }

            Handles.color = prev;
        }

        // ---- OnPaint ----------------------------------------------------------

        public override bool OnPaint(Terrain terrain, IOnPaint editContext)
        {
            if (terrain == null) return false;
            var gt = terrain.GetComponent<GrassTerrain>();
            if (gt == null || gt.GrassMask == null) return false;
            if (!EnsureMaskWritable(gt.GrassMask, out string path)) return false;

            _maskAssetPath = path;
            _lastPaintTime = EditorApplication.timeSinceStartup;

            // Pick up modifier state directly from the event too — OnSceneGUI
            // may not have run for every paint tick during a fast drag.
            var e = Event.current;
            if (e != null) _ctrlHeld = e.control || e.command;

            BeginStrokeIfNeeded(gt.GrassMask, path);
            ApplyBrush(terrain, gt.GrassMask, editContext.uv, _lastBrushSize,
                       editContext.brushStrength, editContext.brushTexture);

            HookIdleFlush();
            return true;
        }

        // ---- Painting core ----------------------------------------------------

        void ApplyBrush(Terrain terrain, Texture2D mask, Vector2 uv,
                        float brushSizeMeters, float strength01, Texture brushTex)
        {
            var data = terrain.terrainData;
            Vector3 size = data.size;

            float halfM   = brushSizeMeters * 0.5f;
            float pxPerMx = mask.width  / size.x;
            float pxPerMz = mask.height / size.z;

            float cxf = uv.x * mask.width;
            float czf = uv.y * mask.height;
            float rx  = halfM * pxPerMx;
            float rz  = halfM * pxPerMz;

            int x0 = Mathf.Max(0, Mathf.FloorToInt(cxf - rx));
            int x1 = Mathf.Min(mask.width  - 1, Mathf.CeilToInt(cxf + rx));
            int z0 = Mathf.Max(0, Mathf.FloorToInt(czf - rz));
            int z1 = Mathf.Min(mask.height - 1, Mathf.CeilToInt(czf + rz));
            int w = x1 - x0 + 1;
            int h = z1 - z0 + 1;
            if (w <= 0 || h <= 0) return;

            int count = w * h;
            if (_pixelScratch == null || _pixelScratch.Length < count)
                _pixelScratch = new Color[count];

            var src = mask.GetPixels(x0, z0, w, h);
            System.Array.Copy(src, _pixelScratch, count);

            float perEvent = strength01 * 0.20f;
            float invRx    = 1f / Mathf.Max(0.001f, rx);
            float invRz    = 1f / Mathf.Max(0.001f, rz);

            // Get a CPU-readable copy of the selected brush texture so we can
            // sample it per pixel. Falls back to a quadratic round falloff if
            // no brush is available.
            Texture2D brush = EnsureBrushReadable(brushTex);

            for (int j = 0; j < h; ++j)
            for (int i = 0; i < w; ++i)
            {
                float dx = (x0 + i + 0.5f - cxf) * invRx;
                float dz = (z0 + j + 0.5f - czf) * invRz;

                float falloff;
                if (brush != null)
                {
                    // Brush UV: map (dx, dz) in [-1, +1] to [0, 1] across the
                    // brush square. Flip V so brush orientation matches the
                    // scene-view preview.
                    float bu = dx * 0.5f + 0.5f;
                    float bv = 1f - (dz * 0.5f + 0.5f);
                    if (bu < 0f || bu > 1f || bv < 0f || bv > 1f) continue;
                    // Sample R; Unity's stock brushes are grayscale with the
                    // mask in every channel, so .r works for all of them.
                    falloff = brush.GetPixelBilinear(bu, bv).r;
                    if (falloff <= 0f) continue;
                }
                else
                {
                    float r2 = dx * dx + dz * dz;
                    if (r2 >= 1f) continue;
                    falloff = 1f - r2;
                }

                float delta = perEvent * falloff;

                int idx = j * w + i;
                Color c = _pixelScratch[idx];
                ApplyModeDelta(ref c, delta);
                _pixelScratch[idx] = c;
            }

            if (_pixelScratch.Length != count)
            {
                var exact = new Color[count];
                System.Array.Copy(_pixelScratch, exact, count);
                mask.SetPixels(x0, z0, w, h, exact);
            }
            else
            {
                mask.SetPixels(x0, z0, w, h, _pixelScratch);
            }

            mask.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _strokeDirty = true;
        }

        // Returns a CPU-readable copy of the given brush texture, blitting it
        // through a temporary RenderTexture so we can sample non-readable
        // textures (Unity's stock brushes are not readable). The result is
        // cached and only rebuilt when the brush instance changes.
        Texture2D EnsureBrushReadable(Texture src)
        {
            if (src == null) return null;
            if (ReferenceEquals(src, _brushSrc) && _brushReadable != null) return _brushReadable;

            int w = src.width;
            int h = src.height;
            if (w <= 0 || h <= 0) return null;

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.Linear);
            Graphics.Blit(src, rt);

            if (_brushReadable != null && (_brushReadable.width != w || _brushReadable.height != h))
            {
                Object.DestroyImmediate(_brushReadable);
                _brushReadable = null;
            }
            if (_brushReadable == null)
            {
                _brushReadable = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
                {
                    hideFlags  = HideFlags.HideAndDontSave,
                    wrapMode   = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }

            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            _brushReadable.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            _brushReadable.Apply(updateMipmaps: false);
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);

            _brushSrc = src;
            return _brushReadable;
        }

        void ApplyModeDelta(ref Color c, float delta)
        {
            // Ctrl flips the sign — the same channel the brush targets is
            // subtracted instead of added.
            float signed = _ctrlHeld ? -delta : delta;
            switch (_mode)
            {
                case PaintMode.Placement: c.r = Mathf.Clamp01(c.r + signed); break;
                case PaintMode.Height:    c.g = Mathf.Clamp01(c.g + signed); break;
                case PaintMode.Spacing:   c.b = Mathf.Clamp01(c.b + signed); break;
            }
        }

        void ClearAllGrass(Texture2D mask)
        {
            if (!EnsureMaskWritable(mask, out string path)) return;
            _maskAssetPath = path;

            // Capture before/after for undo. ClearAll counts as one stroke.
            byte[] before = mask.GetRawTextureData();

            var pixels = mask.GetPixels();
            for (int i = 0; i < pixels.Length; ++i)
            {
                var c = pixels[i];
                c.r = 0f;
                pixels[i] = c;
            }
            mask.SetPixels(pixels);
            mask.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            PushUndoEntry(new StrokeSnapshot
            {
                maskPath = path,
                before   = before,
                after    = mask.GetRawTextureData(),
            });

            _strokeDirty = true;
            FlushStroke();
        }

        // ---- Stroke / undo plumbing ------------------------------------------

        void BeginStrokeIfNeeded(Texture2D mask, string path)
        {
            if (_activeStroke != null) return;
            _activeStroke = new StrokeSnapshot
            {
                maskPath = path,
                before   = mask.GetRawTextureData(),
            };
        }

        void FinalizeActiveStroke()
        {
            if (_activeStroke == null) return;
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(_activeStroke.maskPath);
            if (mask != null)
            {
                _activeStroke.after = mask.GetRawTextureData();
                PushUndoEntry(_activeStroke);
            }
            _activeStroke = null;
        }

        void PushUndoEntry(StrokeSnapshot snap)
        {
            // Drop the redo tail — any new edit invalidates redo state.
            if (_undoHistory.Count > _undoCursor)
                _undoHistory.RemoveRange(_undoCursor, _undoHistory.Count - _undoCursor);

            _undoHistory.Add(snap);
            _undoCursor = _undoHistory.Count;

            while (_undoHistory.Count > kMaxUndoDepth)
            {
                _undoHistory.RemoveAt(0);
                _undoCursor--;
            }
        }

        void DoUndo()
        {
            // If a stroke is still in flight, finalize it first so it becomes
            // its own undo entry. Two undo presses then revert it.
            FinalizeActiveStroke();
            FlushStroke();

            if (_undoCursor <= 0)
            {
                SceneView.lastActiveSceneView?.ShowNotification(new GUIContent("Paint Grass: nothing to undo"), 0.8f);
                return;
            }
            _undoCursor--;
            ApplyAndSave(_undoHistory[_undoCursor], _undoHistory[_undoCursor].before);
        }

        void DoRedo()
        {
            if (_undoCursor >= _undoHistory.Count)
            {
                SceneView.lastActiveSceneView?.ShowNotification(new GUIContent("Paint Grass: nothing to redo"), 0.8f);
                return;
            }
            var snap = _undoHistory[_undoCursor];
            ApplyAndSave(snap, snap.after);
            _undoCursor++;
        }

        static void ApplyAndSave(StrokeSnapshot snap, byte[] data)
        {
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(snap.maskPath);
            if (mask == null) return;
            EnsureMaskWritable(mask, out _);
            mask.LoadRawTextureData(data);
            mask.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            System.IO.File.WriteAllBytes(snap.maskPath, mask.EncodeToPNG());
            AssetDatabase.ImportAsset(snap.maskPath);
        }

        // ---- Mask file housekeeping ------------------------------------------

        static bool EnsureMaskWritable(Texture2D mask, out string path)
        {
            path = AssetDatabase.GetAssetPath(mask);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("GrassPaintTool: GrassMask must be an imported PNG asset, not a runtime texture.");
                return false;
            }
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return true;

            bool dirty = false;
            if (!imp.isReadable) { imp.isReadable = true; dirty = true; }
            if (imp.textureCompression != TextureImporterCompression.Uncompressed)
            {
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                dirty = true;
            }
            if (imp.sRGBTexture) { imp.sRGBTexture = false; dirty = true; }
            if (dirty) imp.SaveAndReimport();
            return true;
        }

        void HookIdleFlush()
        {
            if (_tickHooked) return;
            EditorApplication.update += TickIdleFlush;
            _tickHooked = true;
        }

        void TickIdleFlush()
        {
            // Wait until 0.25s after the last paint event. Then finalize any
            // in-flight stroke (records before/after into undo history) and
            // write the PNG once.
            if (_activeStroke == null && !_strokeDirty)
            {
                UnhookIdleFlush();
                return;
            }
            if (EditorApplication.timeSinceStartup - _lastPaintTime < 0.25) return;

            FinalizeActiveStroke();
            FlushStroke();
            UnhookIdleFlush();
        }

        void UnhookIdleFlush()
        {
            if (!_tickHooked) return;
            EditorApplication.update -= TickIdleFlush;
            _tickHooked = false;
        }

        void FlushStroke()
        {
            if (!_strokeDirty || string.IsNullOrEmpty(_maskAssetPath)) return;
            _strokeDirty = false;

            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(_maskAssetPath);
            if (mask == null) return;
            System.IO.File.WriteAllBytes(_maskAssetPath, mask.EncodeToPNG());
            AssetDatabase.ImportAsset(_maskAssetPath);
        }
    }
}
