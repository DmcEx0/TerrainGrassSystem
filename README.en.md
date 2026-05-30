# 🌿 Terrain Grass System

**🌐 Язык / Language:** [Русский](README.md) · **English**

---

Optimized procedural grass for **Unity Terrain**.

Implements the approach from the talk *"Procedural Grass in Ghost of Tsushima"*:

- Blades are generated on the GPU in a compute shader along a Bézier curve.
- The world is split into tiles; visible tiles are culled on the CPU (frustum + distance).
- Each blade is additionally culled on the GPU (frustum + max-distance).
- Two LOD meshes with smooth cross-fade via height compression at the boundaries.
- Clump-noise fields drive grass height, color and orientation.
- Wind — a Perlin texture + per-blade phase.
- Curved "fake" normals and camera-facing rotation without extra geometry.
- Artists tune parameters through a `GrassType` ScriptableObject.

<img src="img/1.png" alt="Overview of grass on the terrain" width="1056">

---

## 📦 Installation (UPM)

The package lives in the `Assets/TerrainGrassSystem` subfolder, so the git URL specifies `?path=`.

Package Manager → **Add package from git URL…**:

```
https://github.com/DmcEx0/TerrainGrassSystem.git?path=/Assets/TerrainGrassSystem
```

or manually in the project's `Packages/manifest.json`:

```json
"com.terraingrasssystem.grass": "https://github.com/DmcEx0/TerrainGrassSystem.git?path=/Assets/TerrainGrassSystem",
```

---

## 📋 Requirements

| | |
|---|---|
| **Unity** | 6.x with URP 17.x (tested on `6000.3.8f1`, URP `17.3.0`) |
| **GPU** | Shader Model 5.0+ (compute shaders, `RenderMeshIndirect`, `StructuredBuffer` in the vertex stage). All current GPUs qualify, including mobile with Vulkan/Metal. |

---

## 📁 Module layout

```
TerrainGrassSystem/
├── Runtime/                            (asmdef: TerrainGrassSystem.Grass)
│   ├── GrassBlade.cs                   C# mirror of the HLSL structs
│   ├── GrassType.cs                    ScriptableObject of grass parameters
│   ├── GrassTerrainSettings.cs         ScriptableObject of tile/LOD settings
│   ├── GrassBladeMesh.cs               unit-mesh generation for the two LODs
│   ├── GrassWind.cs                    wind component
│   ├── GrassRenderer.cs                core: buffers, indirect draw
│   └── GrassTerrain.cs                 wrapper component for Unity Terrain
│
├── Shaders/
│   ├── GrassCommon.hlsl                structs and helpers (shared)
│   ├── GrassWind.hlsl                  wind sampling
│   ├── GrassCompute.compute            CSGenerate + CSBuildArgs
│   ├── GrassBlade.shader               URP HLSL shader (default working one)
│   └── GrassBladeGraph.hlsl            Custom Function for ShaderGraph
│
└── Editor/                             (asmdef: TerrainGrassSystem.Grass.Editor)
    ├── GrassNoiseGenerator.cs          noise & mask generation window
    └── GrassPaintTool.cs               "Paint Grass" brush for the terrain
```

---

## 🚀 Quick start: scene setup

### Step 1. Generate the starting textures

Open the generator window: **`Tools → GrassSystem → Noise & Mask Generator`**.

![Grass Noise & Mask window](img/2.png)

The window has a **Texture Type** dropdown — its value determines the window contents:

1. **Clump Noise** — seamless Perlin noise for clumps (height / color / orientation). Set `Scale`, `Octaves`, `Persistence`, `Lacunarity`, tone, and click **Generate Clump Noise**.
2. **Density Mask** — an empty density mask. Pick a size (1024 recommended) and click **Generate Blank Density Mask**. By default the mask is empty (R = 0): grass appears only where it is painted.

The **Folder** field + **Browse…** button set the export folder (always inside `Assets/`). Generated textures are imported with the correct settings automatically (no compression, linear, the proper wrap-mode).

### Step 2. Create the config assets

In the Project window: **RMB → Create → TerrainGrassSystem → Grass → …**

- **Grass Type** — exactly one per terrain (grass appearance parameters).
- **Terrain Settings** — tile / LOD / performance settings.

### Step 3. Create a material

- **RMB → Create → Material**, name it e.g. `M_Grass`.
- Assign the shader **`TerrainGrassSystem/Grass/Blade`**.
- *(For the ShaderGraph variant — see the [ShaderGraph](#-shadergraph-variant) section.)*

### Step 4. Attach the components to the terrain

On the GameObject with the **`Terrain`** component, add **`TerrainGrassSystem → Grass → Grass Terrain`** and fill in the fields (see the [reference below](#grassterrain)):

| Field | What to assign |
|---|---|
| Compute Shader | `GrassCompute` |
| High Lod Material | `M_Grass` |
| Low Lod Material | `M_Grass` *(can be the same)* |
| Grass Mask | the generated mask |
| Clump Noise | the generated Perlin noise |
| Type | the `GrassType` asset |
| Settings | the `GrassTerrainSettings` asset |

![Grass Terrain inspector](img/3.png)

### Step 5. Add wind *(optional)*

On any GameObject in the scene add the **`TerrainGrassSystem → Grass → Grass Wind`** component and assign `Wind Noise` (the same Perlin noise or a separate one — the key is that it be seamless). Without this component the grass simply does not sway.

### Step 6. Paint the grass

The mask is empty by default, so there is no grass yet. In the terrain inspector, `GrassTerrain` adds a **"TerrainGrassSystem/Paint Grass"** brush to the terrain brush row. Select it and paint (see the [Painting](#-painting-the-mask) section).

### Step 7. Play

Hit **Play**. Grass is also visible in the Scene View without entering Play mode — the components are marked `[ExecuteAlways]`.

---

## 🧩 Component reference

### GrassTerrain

The main component. Attached to the object with `Terrain`, it links all the assets and dispatches generation every frame.

| Field | Type | Description |
|---|---|---|
| **Compute Shader** | ComputeShader | The generation shader — the `GrassCompute` asset. |
| **High Lod Material** | Material | Material on the `TerrainGrassSystem/Grass/Blade` shader for the near LOD. |
| **Low Lod Material** | Material | Material for the far LOD. May be the same as High — a separate one lets you tune it independently. |
| **Grass Mask** | Texture2D | RGBA mask, painted with the Paint Grass brush. R = placement/density, G = height multiplier, B = clamp/clumping, A = reserved. *(see [Mask](#-terrain-mask-grass-mask))* |
| **Clump Noise** | Texture2D | Seamless Perlin noise. Defines clumps: variation of grass height, color and orientation. **Required** — without it grass does not render, although it does not affect *placement*. |
| **Type** | GrassType | One grass type for the whole terrain. Local variation comes from the mask and noise. |
| **Settings** | GrassTerrainSettings | Tile, LOD, distance and buffer-capacity settings. |
| **Override Camera** | Camera | *(Play-mode only)* Camera used for LOD/culling instead of `Camera.main`. In Edit mode the Scene View camera always wins. |

---

### GrassWind

The wind source. One per scene; renderers find it automatically (`FindFirstObjectByType`). Works in Edit mode too.

| Field | Type | Default | Description |
|---|---|---|---|
| **Wind Noise** | Texture2D | — | Seamless Perlin noise. R — main gust, G — second octave. |
| **Strength** | float ≥ 0 | `0.15` | Wind strength (amplitude of blade tilt). |
| **Frequency** | float | `0.06` | World-space sampling frequency of the noise. Lower = larger gust "waves". |
| **Scroll Speed** | float | `0.6` | Scroll speed of the main noise octave. |
| **Gust Speed** | float | `0.9` | Scroll speed of the second octave. A mismatch with the main one makes the wind feel "alive". |
| **Direction Degrees** | 0..360 | `45` | Wind direction in degrees (in the XZ plane). |

---

### GrassType

Grass appearance parameters. One asset for the whole terrain; variety comes from the mask + clump noise.

#### Height

| Field | Default | Description |
|---|---|---|
| **Base Height** | `0.6` | Base blade height, m. |
| **Height Variance** | `0.2` | Height variation by **clump noise** — neighbors change together, forming visible patches of tall/short grass. |
| **Height Randomness** | `0.1` | Random **per-blade** height spread, m. Independent of the noise: each blade rolls its own. |

#### Width

| Field | Default | Description |
|---|---|---|
| **Base Width** | `0.04` | Base width at the root, m. |
| **Width Variance** | `0.01` | Random width spread. |
| **Width Height Coupling** | `0` (0..1) | How much the width scales with blade height. 0 = width is independent (legacy); 1 = a short blade becomes proportionally thinner. |

#### Shape

| Field | Default | Description |
|---|---|---|
| **Base Tilt** | `0.35` | Forward tilt of the tip, radians. |
| **Tilt Variance** | `0.15` | Random tilt spread. |
| **Slope Follow** | `0.5` (0..1) | How much the blade follows the terrain slope. 0 = always straight up; 1 = perpendicular to the surface. |
| **Facing Variance** | `0.5` | Random rotation around the vertical, radians. Added on top of the noise-driven direction. 0 → neighbors face the same way (visible "stripes" and bald spots when rotating the camera); ~0.5 (≈28°) is good local variation; π = fully random. |
| **Facing Randomness** | `0` (0..1) | Blends direction between clump noise and a fully random per-blade angle. 0 = follow the noise; 1 = ignore the noise. |
| **Bend** | `0.15` | Bend of the middle Bézier control point (curvature). Positive = arches forward. |

#### Density

| Field | Default | Description |
|---|---|---|
| **Density Multiplier** | `1.0` (0..4) | Global density multiplier (multiplied by `mask.r`). |
| **Max Blades Per Cell** | `8` (1..16) | Maximum blades in a cell when channel B (Clamp) is fully painted. At B=0 — the base 1–2; at B=1 the count grows toward this maximum. ⚠️ Buffer load grows linearly — raise `MaxHighLodBlades` / `MaxLowLodBlades` in step. |

#### Short-blade optimizations

| Field | Default | Description |
|---|---|---|
| **Fold Height** | `0.3` | Blades shorter than this (m) render as **a single mesh folded into a V** — one instance = two short sub-blades. `0` = disabled. Typically 30–50% of Base Height. |
| **Small Blade Height** | `0.2` | Unfolded blades shorter than this (m) move to the low-LOD mesh (1 triangle instead of ~7). Folded ones are untouched. `0` = disabled. |

#### Color

| Field | Description |
|---|---|
| **Base Color** | Color at the root. |
| **Tip Color** | Color at the tip. Between them is a linear gradient + one more lerp by clump noise (channel G). |

---

### GrassTerrainSettings

Performance and layout settings. Can be shared between several terrains.

#### Tile Layout

| Field | Default | Description |
|---|---|---|
| **Tile Size** | `16` (≥2) | Tile size in meters. Smaller = more precise culling, but more dispatches per frame. |
| **Blades Per Tile Axis** | `96` (4..256) | Blades per axis inside a tile. Total per tile = N×N. The grid is jittered so it does not look like a lattice. |

#### Culling

| Field | Default | Description |
|---|---|---|
| **Tile Cull Distance** | `80` (≥2) | Tiles beyond this distance are not generated at all. |
| **Max Blade Distance** | `60` (≥2) | Per-blade maximum: blades farther away are discarded on the GPU. |

#### LOD

| Field | Default | Description |
|---|---|---|
| **High Lod Distance** | `18` (≥0) | Closer than this distance — the high-LOD mesh (4 segments). |
| **Lod Blend Band** | `6` (≥0.1) | Width of the cross-fade zone at LOD transitions and at max-distance. Larger = softer, but more overdraw. |

#### Distance Thinning

| Field | Default | Description |
|---|---|---|
| **Thinning Start Distance** | `30` (≥0) | From this distance grass starts thinning out. Closer — no thinning; high-LOD is never touched. |
| **Thinning Strength** | `0` (0..1) | How aggressively it thins toward max-distance. 0 = none; 1 = maximum (almost all far low-LOD blades drop out). |

#### Clumping noise

| Field | Default | Description |
|---|---|---|
| **Clump Scale** | `0.05` | World-space scale of the clump noise. Smaller = larger clumps. |

#### Buffer Capacity

| Field | Default | Description |
|---|---|---|
| **Max High Lod Blades** | `600 000` (≥1024) | Ceiling of the high-LOD buffer per frame (across all tiles). Overflow → blades are lost. |
| **Max Low Lod Blades** | `800 000` (≥1024) | Same for low-LOD. |

---

## 🎨 Terrain mask (`Grass Mask`)

An RGBA texture the size of the terrain (or smaller — it stretches over UV). Channels:

| Channel | Purpose |
|---|---|
| **R** | **Placement / density** (0..1). 0 — no grass; >0 — it grows. The only required channel for basic grass. |
| **G** | **Height multiplier** (0..1). 1 — full height from `GrassType`; 0 — no grass. At G < 0.5 the folded-blade optimization kicks in. |
| **B** | **Clamp / clumping** (0..1). OPTIONAL. Where painted — the number of blades growing from one point increases + a small height boost (+25%) and brightness boost (+20%) at the maximum. Average blades per cell: B=0 → 1–2 (base), B=1 → toward `Max Blades Per Cell`. |
| **A** | Reserved. |

![Grass mask example](img/4.png)

**Folded blade** — when a blade's final height drops below `GrassType.FoldHeight`, compute emits a single "folded in half" blade. The shader (`ApplyBladeFold` in `GrassCommon.hlsl`) bends the mesh at point `v=0.5` — yielding a V shape: the middle of the mesh becomes the root, and both ends become the tips of two short sub-blades. One instance = two visible blades "for free". `FoldHeight = 0` disables the optimization.

---

## 🖌 Painting the mask

The easiest way to paint is right from the terrain inspector. `GrassTerrain` adds **"TerrainGrassSystem/Paint Grass"** to the terrain brush row.

<img src="img/5.png" alt="Paint Grass brush in the terrain inspector" width="1715">

**Modes:**

| Mode | Channel | What it does |
|---|---|---|
| **Placement (R)** | R | Where grass grows. The base mode, the only one needed by default. |
| **Height (G)** | G | Locally vary grass height (0 = none, 1 = full). |
| **Spacing / Clamp (B)** | B | Clumping: where painted — tufts of several blades from one root. |

**Controls:**

- Holding **Ctrl** while painting — subtracts the value of the same channel (Ctrl + Placement = remove grass, and so on).
- **Ctrl + Z** / **Ctrl + Shift + Z** (or **Ctrl + Y**) — undo / redo while the brush is active. The history is in-memory, lives until a domain reload, up to 16 strokes.

**Mask Utilities:**

- **Clear All Grass** — zero out the entire placement channel.
- **Save Now** — force-save the mask PNG (otherwise it is saved deferred after a stroke).

---

## ⚙️ "Noise & Mask Generator" window

**`Tools → GrassSystem → Noise & Mask Generator`**

The **Texture Type** field switches the window contents between two generators:

### Clump Noise (Perlin)

| Parameter | Description |
|---|---|
| **Output Size** | Texture size (64…4096). |
| **Seed** | Seed. The **Re-roll Seed** button yields a new pattern at the same settings. |
| **Scale** | Feature density: larger = finer pattern. |
| **Octaves** | Number of fBm layers. More = richer/"noisier". |
| **Persistence** | Volume of each subsequent layer. Low = smooth, high = noisy. |
| **Lacunarity** | Frequency growth per layer. 2 = standard fBm. |
| **Contrast** | Sharpness of light/dark transitions. 1 = neutral, 0 = flat, >1 = hard. |
| **Brightness** | Brightness shift after contrast. |

At the bottom — a **live preview** and the **Generate Clump Noise** button.

### Density Mask

| Parameter | Description |
|---|---|
| **Size** | Mask size (1024 recommended). |

The **Generate Blank Density Mask** button creates an empty mask (R=0 → no grass until it is painted).

**Export Folder** (shared by both types) — a folder inside `Assets/` where the PNG is saved. The **Browse…** button opens a folder picker straight in the current project's `Assets`.

---

## 🚄 Performance

The default settings target ~100,000 visible blades across a 30-meter panorama.

**Reference figures:**

- GPU frame on an RTX 3060 — ~1.2 ms on compute + ~0.8 ms on render at 100k blades.
- On mobile: `Blades Per Tile Axis` → 48–64, `Max Blade Distance` → 25 m.
- Tall grass (`Base Height` > 1 m) greatly increases overdraw — trim `High Lod Distance` / `Max Blade Distance` first.

**Tuning (in descending order of effect):**

1. `Max Blade Distance` ↓
2. `Blades Per Tile Axis` ↓
3. `Tile Cull Distance` ↓
4. `Lod Blend Band` ↓ (less overdraw in the cross-fade zone)

---

## 🛠 Troubleshooting

**Grass does not render at all**
- In `GrassTerrain` these are required: Compute Shader, both materials, mask, noise, Type, Settings. If even one field is empty — the component silently skips the frame.
- The mask is empty by default — **paint the grass** with the Placement brush.
- `Camera.main` not found → assign `Override Camera` or set the MainCamera tag.

**Grass flickers / disappears at distance**
- `Lod Blend Band` is too small relative to `High Lod Distance` / `Max Blade Distance`. Raise it to 4–8 m.
- `Max High Lod Blades` / `Max Low Lod Blades` is overflowing — check in RenderDoc or raise the ceiling.

**Grass "lies" at one height, ignoring the terrain**
- Compute reads `terrain.terrainData.heightmapTexture`. Make sure the Terrain is not offset by a non-standard Transform and that `terrain.transform.position` matches the origin used.

**Wind does not move in Scene View**
- `GrassWind` in Edit mode uses `EditorApplication.timeSinceStartup`. If it is static — check whether there is an active object with `GrassWind` in the scene and that its GameObject is not disabled.

**Blades "pop" after a LOD switch**
- `Lod Blend Band` is too abrupt. Increase it (~1/3 of `High Lod Distance`).

**Heavy overdraw / FPS drops**
- See the [Performance](#-performance) section. The main culprits are `Max Blade Distance` and `Blades Per Tile Axis`.

**No shadows from the grass**
- In the manual shader the ShadowCasting tag is already set.
- In the Universal Renderer Asset, check that Cast/Receive Shadows are not disabled globally.

---

## 🧪 ShaderGraph variant

`GrassBladeGraph.hlsl` contains the Custom Function `GrassBlade_Vertex_float`. To replace the manual HLSL shader with ShaderGraph:

1. **Create → Shader Graph → URP → Lit Shader Graph**, name it `GrassBlade`.
2. **Graph Inspector → Graph Settings:** Material = Lit, Surface Type = Opaque, Render Face = Both.
3. **Properties (exposed, Float):**

   | Property | Value |
   |---|---|
   | `_CamFacingThreshold` | `0.8` (edge-on cos threshold) |
   | `_CamFacingMaxAngle` | `8.0` (max twist angle at the tip, °) |
   | `_CurvedNormalAmount` | `0.7` |
   | `_AOStrength` | `0.5` |
   | `_TipBoost` | `1.2` |
   | `_Smoothness` | `0.05` |

4. **Custom Function node:** Mode = File, Source = `GrassBladeGraph.hlsl`, Name = `GrassBlade_Vertex`.
   - **Inputs:** `PositionOS` (Vector3), `InstanceID` (Float), `CamFacingT` (Float), `CamFacingMaxA` (Float), `CurvedNAmt` (Float), `AOStrength` (Float), `TipBoost` (Float)
   - **Outputs:** `PositionWS` (Vector3), `NormalWS` (Vector3), `ColorBase` (Vector3), `UV` (Vector2)
5. **Connections:**
   - Position (Object Space) → `PositionOS`
   - Instance ID → `InstanceID`
   - Property nodes → the matching inputs
   - `PositionWS` → the Vertex Position block (directly, no Transform node)
   - `NormalWS` → the Vertex Normal block (directly)
   - `ColorBase` → the Base Color block
   - `_Smoothness` → the Smoothness block

   > `Graphics.RenderMeshIndirect` uses an identity model matrix, so Object Space ≡ World Space — a Transform (World → Object) would be a no-op.

6. **(opt.) Custom Function `GrassBlade_Masks`** — separate AO and tip-mask outputs (instead of the pre-mixed `ColorBase`). Useful for mixing color yourself in the graph.
   - **Inputs:** `V` (Float, from a Split on Position(OS).G), `AOStrength` (Float)
   - **Outputs:** `AO` (Float), `TipMask` (Float)

   A typical color wiring:

   ```
   GrassType_Color ─┐
                    ├─ Lerp ──┐
   TipColor ────────┘ T:TipMask ├─ Multiply ──┐
                                │             ├─ Multiply ── Base Color
                           AO ──┘             │
                                     GrassTint ┘  (Color property)
   ```

   With this scheme do **not** connect `ColorBase` from the first node into Base Color, otherwise AO/TipBoost get multiplied twice.

7. Create a Material from the graph, assign it in `GrassTerrain`. After verifying, `GrassBlade.shader` can be deleted.

ShaderGraph automatically generates the ShadowCaster and DepthOnly passes.

---

## 🔌 Extending

**Character interaction (trampling):**
- In `GrassWind` add `Vector4 _GrassImpactSource` (xyz = world pos, w = radius).
- In `GrassBlade.shader` / `GrassBladeGraph.hlsl` add a deflection of `blade.position` based on `dist(worldPos, impact)`.
- From code each frame: `Shader.SetGlobalVector("_GrassImpactSource", ...)`.

**Streaming large worlds (multiple terrains):**
- `GrassRenderer` — one per terrain. If the world is split into 10×10 terrain tiles → put a `GrassTerrain` on each, they do not interfere with one another.

**Different mask format / density source:**
- In `GrassCompute.compute` edit `SampleGrassMask` and the channel reinterpretation in `CSGenerate` (placement / heightMul / densityMul).
