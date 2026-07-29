# 🌿 Terrain Grass System

**🌐 Язык / Language:** **English** · [Русский](README.ru.md)

---

Optimized procedural grass for **Unity Terrain**.

Implements the approach from the talk *"Procedural Grass in Ghost of Tsushima"*:

- Blades are generated on the GPU in a compute shader along a Bézier curve.
- The world is split into tiles; visible tiles are culled on the CPU (frustum + distance).
- Each blade is additionally culled on the GPU (frustum + max-distance).
- Runtime generation and drawing are recorded in a custom **URP RenderGraph** pass after opaque geometry.
- Optional camera-depth occlusion rejects grass hidden behind opaque objects before drawing.
- Two LOD meshes with stable stochastic switching — a blade is never drawn in both LODs at once.
- Clump-noise fields drive grass height, color and orientation.
- Wind — a Perlin texture + per-blade phase.
- Up to eight interaction sources can bend grass and protect the player from depth occlusion.
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
| **Render pipeline** | URP with RenderGraph enabled. Compatibility Mode (Render Graph disabled) is not supported at runtime. |
| **GPU** | Shader Model 5.0+ (compute shaders, indirect indexed draws, `StructuredBuffer` in the vertex stage). |

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
│   ├── GrassInteractionSource.cs       marks a transform as a grass-pusher
│   ├── GrassInteractionManager.cs      source registry + ShaderGraph globals
│   ├── GrassRenderPassFeature.cs       URP RenderGraph generate/draw passes
│   ├── GrassRenderer.cs                GPU buffers, compute, indirect draw
│   └── GrassTerrain.cs                 wrapper component for Unity Terrain
│
├── Shaders/
│   ├── GrassCommon.hlsl                structs and helpers (shared)
│   ├── GrassWind.hlsl                  wind sampling + GrassWind_Apply graph node
│   ├── GrassInteraction.hlsl           source push + GrassInteraction_Apply graph node
│   ├── GrassCompute.compute            CSGenerate + CSBuildArgs
│   ├── GrassBlade.shader               URP HLSL shader (default working one)
│   └── GrassBladeGraph.hlsl            Custom Function nodes for ShaderGraph
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

### Step 4. Add the URP Renderer Feature

Open every **Universal Renderer Data** asset used by a camera that should render grass. Click **Add Renderer Feature** and add **Grass Render Pass Feature**. Keep its event at **After Rendering Opaques**.

Also make sure URP **Compatibility Mode (Render Graph disabled)** is off. There is intentionally no runtime `LateUpdate` fallback: without this Renderer Feature, grass does not render in Play Mode or a player build.

### Step 5. Attach the components to the terrain

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

### Step 6. Add wind *(optional)*

On any GameObject in the scene add the **`TerrainGrassSystem → Grass → Grass Wind`** component and assign `Wind Noise` (the same Perlin noise or a separate one — the key is that it be seamless). Without this component the grass simply does not sway.

### Step 7. Paint the grass

The mask is empty by default, so there is no grass yet. In the terrain inspector, `GrassTerrain` adds a **"TerrainGrassSystem/Paint Grass"** brush to the terrain brush row. Select it and paint (see the [Painting](#-painting-the-mask) section).

### Step 8. Play

Hit **Play**. At runtime the grass is rendered only through URP RenderGraph. Without entering Play mode it remains visible through the original `[ExecuteAlways]` / `LateUpdate` Scene View preview; that path is editor-only and is not included in player builds.

---

## 🧩 Component reference

### GrassTerrain

The main component. Attached to the object with `Terrain`, it links all assets and supplies each camera frame to the RenderGraph pass.

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
| **Base Height** | `0.8` | Base blade height, m. |
| **Height Variance** | `0.4` | Height variation by **clump noise** — neighbors change together, forming visible patches of tall/short grass. |
| **Height Randomness** | `0.2` | Random **per-blade** height spread, m. Independent of the noise: each blade rolls its own. |

#### Width

| Field | Default | Description |
|---|---|---|
| **Base Width** | `0.04` | Base width at the root, m. |
| **Width Variance** | `0.02` | Random width spread. |
| **Width Height Coupling** | `0.581` (0..1) | How much the width scales with blade height. 0 = width is independent; 1 = a short blade becomes proportionally thinner. |

#### Shape

| Field | Default | Description |
|---|---|---|
| **Base Tilt** | `-0.4` | Forward tilt of the tip, radians. |
| **Tilt Variance** | `0.1` | Random tilt spread. |
| **Slope Follow** | `0` (0..1) | How much the blade follows the terrain slope. 0 = always straight up; 1 = perpendicular to the surface. |
| **Facing Variance** | `3` | Bounded random rotation around the vertical, radians, added to the selected base direction. |
| **Facing Randomness** | `0.05` (0..1) | Blends direction between clump noise and a fully random per-blade angle. 0 = follow the noise; 1 = ignore the noise. |
| **Bend** | `0` | Bend of the middle Bézier control point (curvature). Positive = arches forward. |

#### Density

| Field | Default | Description |
|---|---|---|
| **Density Multiplier** | `1.0` (0..4) | Global density multiplier (multiplied by `mask.r`). |
| **Max Blades Per Cell** | `16` (1..16) | Maximum blades in a cell when channel B (Clamp) is fully painted. At B=0 — the base 1–2; at B=1 the count grows toward this maximum. ⚠️ Buffer load grows linearly — raise `MaxHighLodBlades` / `MaxLowLodBlades` in step. |

#### Short-blade optimizations

| Field | Default | Description |
|---|---|---|
| **Fold Height** | `0` | Blades shorter than this (m) render as **a single mesh folded into a V** — one instance = two short sub-blades. `0` = disabled. Typically 30–50% of Base Height when enabled. |
| **Small Blade Height** | `0.1` | Unfolded blades shorter than this (m) move to the low-LOD mesh (1 triangle instead of ~7). Folded ones are untouched. `0` = disabled. |

#### Wind

| Field | Default | Description |
|---|---|---|
| **Wind Height Falloff** | `0.75` (0..1) | Attenuates wind for short blades. At 1, sway scales roughly with squared height ratio; at 0, all heights receive the same base sway. |

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
| **Tile Size** | `10` (≥2) | Tile size in meters. Smaller = more precise culling, but more CPU tile checks and descriptors. |
| **Blades Per Tile Axis** | `135` (4..256) | Candidate cells per axis inside a tile. Total per tile = N×N before mask, density and GPU culling. |

#### Culling

| Field | Default | Description |
|---|---|---|
| **Tile Cull Distance** | `130` (≥2) | Tiles beyond this distance are not uploaded or generated. |
| **Max Blade Distance** | `150` (≥2) | Per-blade maximum: farther candidates are rejected on the GPU. |
| **Frustum Padding Degrees** | `8` (0..20) | Expands the culling frustum to avoid bare side strips during fast camera rotation. |
| **Enable Depth Occlusion** | `false` | Uses the current opaque camera depth to reject hidden blades before they enter the draw buffers. |
| **Depth Occlusion Bias** | `0.25` (≥0) | World-space safety margin. Increase it if grass pops at depth edges. |
| **Depth Occlusion Radius Scale** | `0.8` (0..2) | Scale of the blade sphere used for the depth test. Lower values cull more aggressively. |
| **Depth Occlusion Sample Radius Pixels** | `2` (0..4) | Cross-sample radius around the blade centre. More samples make silhouette edges safer. |

#### LOD

| Field | Default | Description |
|---|---|---|
| **High Lod Distance** | `4.1` (≥0) | Centre of the switch between the high-LOD mesh (4 segments) and low-LOD mesh (1 triangle). |
| **Lod Blend Band** | `8` (≥0.1) | Width of the stable stochastic transition and the fade near max distance. Each blade is assigned to only one LOD. |

#### Distance Thinning

| Field | Default | Description |
|---|---|---|
| **Thinning Start Distance** | `30` (≥0) | From this distance grass starts thinning out. Closer — no thinning; high-LOD is never touched. |
| **Thinning Strength** | `1` (0..1) | How aggressively it thins toward max-distance. 0 = none; 1 = maximum (almost all far low-LOD blades drop out). |

#### Clumping noise

| Field | Default | Description |
|---|---|---|
| **Clump Scale** | `0` | World-space scale of the clump noise. Smaller = larger clumps; `0` samples one constant point. |

#### Buffer Capacity

| Field | Default | Description |
|---|---|---|
| **Max High Lod Blades** | `600 000` (≥1024) | Ceiling of the high-LOD buffer per frame (across all tiles). Overflow → blades are lost. |
| **Max Low Lod Blades** | `800 000` (≥1024) | Same for low-LOD. |

---

## 🔄 Runtime rendering through RenderGraph

`GrassRenderPassFeature` adds two ordered passes after opaque geometry:

1. **Generate pass.** The CPU collects visible terrain tiles and uploads their descriptors. One batched compute dispatch samples the heightmap, mask and noise, rejects candidates by distance/frustum and optionally camera depth, selects one LOD, and appends surviving blades to high/low GPU buffers. A tiny compute kernel then builds indirect draw arguments from the GPU counters — there is no CPU readback.
2. **Draw pass.** RenderGraph attaches the current camera color and depth targets, then records one indirect forward draw per LOD.

This is better than using `LateUpdate` at runtime because the work belongs to the camera's render frame, runs after opaque depth is available, has explicit color/depth dependencies, and cannot be submitted accidentally for unrelated cameras. It also enables depth occlusion and avoids an out-of-pipeline runtime fallback. The original `LateUpdate` + `Graphics.RenderMeshIndirect` path is preserved only for Edit-mode Scene View preview and is stripped from player builds.

---

## 🎨 Terrain mask (`Grass Mask`)

An RGBA texture the size of the terrain (or smaller — it stretches over UV). Channels:

| Channel | Purpose |
|---|---|
| **R** | **Placement / density** (0..1). 0 — no grass; >0 — it grows. The only required channel for basic grass. |
| **G** | **Height multiplier** (0..1). 1 — full height from `GrassType`; 0 — the minimum short-blade height. If the final height is below a non-zero `Fold Height`, the folded-blade optimization is used. Use R, not G, to remove grass. |
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

Profile on the target hardware: cost depends heavily on mask coverage, resolution, blade height and depth complexity. The renderer already batches all visible tiles into one compute dispatch, performs cheap rejection before height/noise sampling, builds draw arguments on the GPU, and never reads blade counts back to the CPU.

**Tuning (in descending order of effect):**

1. `Max Blade Distance` ↓
2. `Blades Per Tile Axis` ↓
3. `Thinning Strength` ↑ and/or `Thinning Start Distance` ↓
4. `Tile Cull Distance` ↓
5. Enable `Depth Occlusion` when large opaque objects hide substantial grass
6. Reduce `Base Height`, `Max Blades Per Cell`, and densely painted Clamp (B) areas to lower overdraw/buffer pressure

For mobile, start around `Blades Per Tile Axis = 48–64` and `Max Blade Distance = 25–40`, then measure. Keep buffer capacities close to the maximum population you actually need: capacity reserves GPU memory, while visible survivors determine draw cost.

---

## 🛠 Troubleshooting

**Grass does not render at all**
- Add `Grass Render Pass Feature` to the active Universal Renderer Data and keep RenderGraph enabled. There is no runtime `LateUpdate` fallback.
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
- The RenderGraph forward pass receives URP lighting and existing scene shadows, but procedural grass is not currently submitted into URP shadow maps. A separate shadow-caster renderer feature would be required for grass to cast shadows.

---

## 🧪 ShaderGraph variant

The blade graph is intentionally split into two Custom Function nodes so that geometry and wind can be wired, swapped, or disabled independently:

| Node | File | Function name | Role |
|---|---|---|---|
| **GrassBlade_Vertex** | `GrassBladeGraph.hlsl` | `GrassBlade_Vertex` | Blade geometry — position (no wind), normal, color |
| **GrassWind_Apply** | `GrassWind.hlsl` | `GrassWind_Apply` | Wind offset — adds horizontal sway |
| *(opt.)* **GrassBlade_Masks** | `GrassBladeGraph.hlsl` | `GrassBlade_Masks` | Separate AO + tip mask |

For the LowLOD graph use `GrassBlade_VertexBillboard` instead of `GrassBlade_Vertex`; its outputs and wiring are identical.

### Step-by-step

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

4. **Node 1 — GrassBlade_Vertex (geometry):**
   Custom Function → Mode = File, Source = `GrassBladeGraph.hlsl`, Name = `GrassBlade_Vertex`.
   - **Inputs:** `PositionOS` (Vector3), `InstanceID` (Float), `CamFacingT` (Float), `CamFacingMaxA` (Float), `CurvedNAmt` (Float), `AOStrength` (Float), `TipBoost` (Float)
   - **Outputs:** `PositionWS` (Vector3), `NormalWS` (Vector3), `ColorBase` (Vector3), `UV` (Vector2), `WindBase` (Float)

5. **Node 2 — GrassWind_Apply (wind):**
   Custom Function → Mode = File, Source = `GrassWind.hlsl`, Name = `GrassWind_Apply`.
   - **Inputs:** `WindBase` (Float), `V` (Float)
   - **Outputs:** `WindOffset` (Vector3)

6. **Connections:**

   ```
   Position (Object Space) ──────────────────────────────────────► PositionOS
   Split(Position OS).G    ──────────────────────────────────────► V  (wind node)
   Instance ID             ──────────────────────────────────────► InstanceID
   Properties              ──────────────────────────────────────► matching inputs

   GrassBlade_Vertex.WindBase ────────────────────────────────────► WindBase (wind node)

   GrassBlade_Vertex.PositionWS ──┐
                                  ├─ Add ──────────────────────────► Vertex Position
   GrassWind_Apply.WindOffset   ──┘

   GrassBlade_Vertex.NormalWS  ───────────────────────────────────► Vertex Normal
   GrassBlade_Vertex.ColorBase ───────────────────────────────────► Base Color
   _Smoothness                 ───────────────────────────────────► Smoothness
   ```

   > The procedural indirect draw uses an identity model matrix, so Object Space ≡ World Space — no Transform node is needed.

   > **To disable wind:** skip the GrassWind_Apply node and wire `PositionWS` directly to Vertex Position.

7. **(opt.) Node 3 — GrassBlade_Masks** — separate AO and tip-mask outputs instead of the pre-mixed `ColorBase`.
   - **Inputs:** `V` (Float, from Split on Position(OS).G), `AOStrength` (Float)
   - **Outputs:** `AO` (Float), `TipMask` (Float)

   Typical color wiring with this node:

   ```
   GrassType_Color ─┐
                    ├─ Lerp ──┐
   TipColor ────────┘ T:TipMask ├─ Multiply ──┐
                                │             ├─ Multiply ── Base Color
                           AO ──┘             │
                                     GrassTint ┘  (Color property)
   ```

   When using this scheme, do **not** also connect `ColorBase` from Node 1 — AO/TipBoost would be applied twice.

8. Create a Material from the graph, assign it in `GrassTerrain`. After verifying, `GrassBlade.shader` can be deleted.

ShaderGraph may generate ShadowCaster/DepthOnly passes, but the current grass Renderer Feature records only the forward pass. Generated ShadowCaster code alone does not make procedural grass cast into URP shadow maps.

---

## 🌾 Grass interaction (sources)

Grass bends away from explicitly registered moving transforms — player, cubes, capsules, and similar objects. A Unity Collider is not required.

### Scene setup

1. **Add `GrassInteractionSource`** to each object that should push the grass. The default HLSL grass path reads these sources directly during RenderGraph generation; no manager component is required. Up to **8 sources** are processed.
2. Set **Radius** to match the object's approximate footprint:

   | Object type | Suggested Radius |
   |---|---|
   | Player capsule (r=0.3 m) | `0.5` |
   | Small cube (1×1 m) | `0.7` |
   | Large enemy | `1.0–1.5` |

3. Keep **Exclude From Depth Occlusion** enabled for the player. Its conservative sphere stops the player's depth from punching a hole in grass behind the character:

   | Field | Purpose |
   |---|---|
   | **Depth Occlusion Center** | Local-space centre of the protected sphere, normally around the torso. |
   | **Depth Occlusion Radius** | Protected radius; `0` reuses the interaction Radius. |

   Disable the option for props that should genuinely hide grass. To protect an object without bending grass, use `Radius = 0` and a positive `Depth Occlusion Radius`.

4. **Adjust `Interaction Strength`** (`_InteractionMaxPush`) on the grass material — the maximum tip displacement at the source centre. Default: `0.5 m`.

Using explicit sources avoids per-frame physics overlap queries and is cheaper and more precise than discovering player objects through a `LayerMask`.

### ShaderGraph wiring (if using GrassBladeGraph.hlsl)

Add one `GrassInteractionManager` to the scene when using the ShaderGraph interaction node. It publishes the registered sources as global shader values for that graph; the default HLSL shader does not need it.

Add a **third** Custom Function node: Mode = File, Source = `GrassInteraction.hlsl`, Name = `GrassInteraction_Apply`.

| | |
|---|---|
| **Inputs** | `BladeRootWS` (Vector3) — from the new `BladeRootWS` output of `GrassBlade_Vertex` |
| | `V` (Float) — Split(Position OS).G, same node as for wind |
| | `MaxPush` (Float) — exposed property, default `0.5` |
| **Output** | `InteractionOffset` (Vector3) |

Final vertex position wiring:

```
GrassBlade_Vertex.PositionWS ──┐
                               ├─ Add ──┐
GrassWind_Apply.WindOffset   ──┘        ├─ Add ──► Vertex Position
GrassInteraction_Apply.InteractionOffset┘
```

To disable interaction: remove the node and skip the second Add.

---

## 🔌 Extending

**Streaming large worlds (multiple terrains):**
- `GrassRenderer` — one per terrain. If the world is split into 10×10 terrain tiles → put a `GrassTerrain` on each, they do not interfere with one another.

**Different mask format / density source:**
- In `GrassCompute.compute` edit `SampleGrassMask` and the channel reinterpretation in `CSGenerate` (placement / heightMul / densityMul).
