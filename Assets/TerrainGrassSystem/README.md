# 🌿 TerrainGrassSystem

Оптимизированная процедурная трава для **Unity Terrain**.

Реализует подход из доклада *«Procedural Grass in Ghost of Tsushima»*:

- Травинки генерируются на GPU в compute-шейдере по кривой Безье.
- Мир делится на тайлы; видимые тайлы культируются на CPU (frustum + дистанция).
- Каждая травинка дополнительно отсеивается на GPU (frustum + max-distance).
- Два LOD-меша с плавным кросс-фейдом через сжатие высоты на границах.
- Шум-куртины влияют на высоту, цвет и направление травы.
- Ветер — Perlin-текстура + per-blade фаза.
- Изогнутые «фейковые» нормали и поворот к камере без дополнительной геометрии.
- Художники задают параметры через ScriptableObject `GrassType`.

> 📸 **СКРИН СЮДА:** общий вид травы на террейне в сцене (hero-шот). *(строку удалить после вставки скрина)*

---

## 📋 Требования

| | |
|---|---|
| **Unity** | 6.x с URP 17.x (проверено на `6000.3.8f1`, URP `17.3.0`) |
| **GPU** | Shader Model 5.0+ (Compute-шейдеры, `RenderMeshIndirect`, `StructuredBuffer` в vertex stage). Подходят все актуальные GPU, включая мобильные с Vulkan/Metal. |

---

## 📁 Состав модуля

```
TerrainGrassSystem/
├── Runtime/                            (asmdef: TerrainGrassSystem.Grass)
│   ├── GrassBlade.cs                   C#-зеркало HLSL-структур
│   ├── GrassType.cs                    ScriptableObject параметров травы
│   ├── GrassTerrainSettings.cs         ScriptableObject настроек тайлов/LOD
│   ├── GrassBladeMesh.cs               генерация unit-mesh для двух LOD
│   ├── GrassWind.cs                    компонент ветра
│   ├── GrassRenderer.cs                ядро: буферы, indirect draw
│   └── GrassTerrain.cs                 компонент-обвязка для Unity Terrain
│
├── Shaders/
│   ├── GrassCommon.hlsl                структуры и хелперы (общие)
│   ├── GrassWind.hlsl                  семплинг ветра
│   ├── GrassCompute.compute            CSGenerate + CSBuildArgs
│   ├── GrassBlade.shader               URP HLSL-шейдер (рабочий по умолчанию)
│   └── GrassBladeGraph.hlsl            Custom Function для ShaderGraph
│
└── Editor/                             (asmdef: TerrainGrassSystem.Grass.Editor)
    ├── GrassNoiseGenerator.cs          окно генерации шума и маски
    └── GrassPaintTool.cs               кисть «Paint Grass» для террейна
```

---

## 🚀 Быстрый старт: настройка сцены

### Шаг 1. Сгенерировать стартовые текстуры

Открой окно генератора: **`Tools → GrassSystem → Noise & Mask Generator`**.

> 📸 **СКРИН СЮДА:** окно «Grass Noise & Mask» с выпадающим списком Texture Type и полем Folder. *(строку удалить после вставки скрина)*

В окне есть выпадающий список **Texture Type** — от выбора зависит наполнение окна:

1. **Clump Noise** — бесшовный Perlin-шум для куртин (высота / цвет / направление). Настрой `Scale`, `Octaves`, `Persistence`, `Lacunarity`, тон и нажми **Generate Clump Noise**.
2. **Density Mask** — пустая маска плотности. Выбери размер (рекомендуется 1024) и нажми **Generate Blank Density Mask**. По умолчанию маска пустая (R = 0): трава появится только там, где ты её закрасишь.

Поле **Folder** + кнопка **Browse…** задают папку экспорта (всегда внутри `Assets/`). Сгенерированные текстуры импортируются с правильными настройками автоматически (без сжатия, linear, нужный wrap-mode).

### Шаг 2. Создать ассеты-конфиги

В окне Project: **ПКМ → Create → TerrainGrassSystem → Grass → …**

- **Grass Type** — ровно один на террейн (параметры внешнего вида травы).
- **Terrain Settings** — настройки тайлов / LOD / производительности.

### Шаг 3. Создать материал

- **ПКМ → Create → Material**, назови, например, `M_Grass`.
- Назначь шейдер **`TerrainGrassSystem/Grass/Blade`**.
- *(Если хочешь ShaderGraph-вариант — см. раздел [ShaderGraph](#-вариант-с-shadergraph).)*

### Шаг 4. Повесить компоненты на террейн

На GameObject с компонентом **`Terrain`** добавь **`TerrainGrassSystem → Grass → Grass Terrain`** и заполни поля (см. [справку ниже](#grassterrain)):

| Поле | Что назначить |
|---|---|
| Compute Shader | `GrassCompute` |
| High Lod Material | `M_Grass` |
| Low Lod Material | `M_Grass` *(можно тот же)* |
| Grass Mask | сгенерированная маска |
| Clump Noise | сгенерированный Perlin-шум |
| Type | ассет `GrassType` |
| Settings | ассет `GrassTerrainSettings` |

> 📸 **СКРИН СЮДА:** инспектор компонента Grass Terrain со всеми заполненными полями. *(строку удалить после вставки скрина)*

### Шаг 5. Добавить ветер *(опционально)*

Добавь на любой GameObject в сцене компонент **`TerrainGrassSystem → Grass → Grass Wind`** и назначь `Wind Noise` (тот же Perlin-шум или отдельный — главное бесшовный). Без этого компонента трава просто не качается.

### Шаг 6. Покрасить траву

Маска по умолчанию пустая, поэтому травы пока нет. Открой инспектор террейна — у `GrassTerrain` появляется кисть **«TerrainGrassSystem/Paint Grass»** в ряду кистей террейна. Выбери её и крась (см. раздел [Покраска](#-покраска-маски)).

### Шаг 7. Play

Жми **Play**. Трава видна и в Scene View без запуска — компоненты помечены `[ExecuteAlways]`.

---

## 🧩 Справка по компонентам

### GrassTerrain

Главный компонент. Вешается на объект с `Terrain`, связывает все ассеты и каждый кадр запускает генерацию.

| Поле | Тип | Описание |
|---|---|---|
| **Compute Shader** | ComputeShader | Шейдер генерации — ассет `GrassCompute`. |
| **High Lod Material** | Material | Материал на шейдере `TerrainGrassSystem/Grass/Blade` для ближнего LOD. |
| **Low Lod Material** | Material | Материал для дальнего LOD. Может быть тем же, что и High — отдельный позволяет тюнить отдельно. |
| **Grass Mask** | Texture2D | RGBA-маска, рисуется кистью Paint Grass. R = размещение/плотность, G = множитель высоты, B = clamp/пучкование, A = резерв. *(см. [Маска](#-маска-террейна-grass-mask))* |
| **Clump Noise** | Texture2D | Бесшовный Perlin-шум. Задаёт куртины: вариацию высоты, цвета и направления травы. **Обязателен** — без него трава не рендерится, хотя на *размещение* он не влияет. |
| **Type** | GrassType | Один тип травы на весь террейн. Локальные вариации задаются маской и шумом. |
| **Settings** | GrassTerrainSettings | Настройки тайлов, LOD, дистанций, ёмкости буферов. |
| **Override Camera** | Camera | *(только Play-mode)* Камера для расчёта LOD/culling вместо `Camera.main`. В Edit-mode всегда побеждает Scene View-камера. |

---

### GrassWind

Источник ветра. Один на сцену; рендереры находят его автоматически (`FindFirstObjectByType`). Работает и в Edit-mode.

| Поле | Тип | По умолч. | Описание |
|---|---|---|---|
| **Wind Noise** | Texture2D | — | Бесшовный Perlin-шум. R — основной порыв, G — вторая октава. |
| **Strength** | float ≥ 0 | `0.15` | Сила ветра (амплитуда наклона травинок). |
| **Frequency** | float | `0.06` | Мировая частота семплинга шума. Меньше = более крупные «волны» порывов. |
| **Scroll Speed** | float | `0.6` | Скорость прокрутки основной октавы шума. |
| **Gust Speed** | float | `0.9` | Скорость прокрутки второй октавы. Несовпадение с основной делает ветер «живым». |
| **Direction Degrees** | 0..360 | `45` | Направление ветра в градусах (в плоскости XZ). |

---

### GrassType

Параметры внешнего вида травы. Один ассет на весь террейн; разнообразие даёт маска + clump-шум.

#### Height (высота)

| Поле | По умолч. | Описание |
|---|---|---|
| **Base Height** | `0.6` | Базовая высота травинки, м. |
| **Height Variance** | `0.2` | Вариация высоты по **clump-шуму** — соседи меняются вместе, образуя видимые пятна высокой/низкой травы. |
| **Height Randomness** | `0.1` | Случайный разброс высоты **per-blade**, м. Независим от шума: каждая травинка тянет свой ролл. |

#### Width (ширина)

| Поле | По умолч. | Описание |
|---|---|---|
| **Base Width** | `0.04` | Базовая ширина у корня, м. |
| **Width Variance** | `0.01` | Случайный разброс ширины. |
| **Width Height Coupling** | `0` (0..1) | Насколько ширина масштабируется от высоты травинки. 0 = ширина независима (legacy); 1 = низкая травинка становится пропорционально тоньше. |

#### Shape (форма)

| Поле | По умолч. | Описание |
|---|---|---|
| **Base Tilt** | `0.35` | Наклон вершины вперёд, радианы. |
| **Tilt Variance** | `0.15` | Случайный разброс наклона. |
| **Slope Follow** | `0.5` (0..1) | Насколько травинка повторяет наклон рельефа. 0 = всегда строго вверх; 1 = перпендикулярно поверхности. |
| **Facing Variance** | `0.5` | Случайный поворот вокруг вертикали, радианы. Добавляется поверх направления из шума. 0 → соседи смотрят одинаково (видны «полосы» и проплешины при вращении камеры); ~0.5 (≈28°) — хорошая локальная вариация; π — полностью случайно. |
| **Facing Randomness** | `0` (0..1) | Бленд направления между clump-шумом и полностью случайным per-blade углом. 0 = следуют шуму; 1 = шум игнорируется. |
| **Bend** | `0.15` | Прогиб средней контрольной точки Безье (кривизна). Положительный — арка вперёд. |

#### Density (плотность)

| Поле | По умолч. | Описание |
|---|---|---|
| **Density Multiplier** | `1.0` (0..4) | Глобальный множитель плотности (домножается на `mask.r`). |
| **Max Blades Per Cell** | `8` (1..16) | Максимум травинок в ячейке при полностью закрашенном канале B (Clamp). При B=0 — базовые 1–2; при B=1 счётчик растёт к этому максимуму. ⚠️ Нагрузка на буферы растёт линейно — поднимай `MaxHighLodBlades` / `MaxLowLodBlades` синхронно. |

#### Short-blade optimizations (оптимизация коротких травинок)

| Поле | По умолч. | Описание |
|---|---|---|
| **Fold Height** | `0.3` | Травинки короче этого (м) рендерятся как **один меш, сложенный в V** — одна инстанция = две короткие суб-травинки. `0` = выключить. Типично 30–50% от Base Height. |
| **Small Blade Height** | `0.2` | Несложенные травинки короче этого (м) уходят на low-LOD меш (1 треугольник вместо ~7). Сложенные не трогаются. `0` = выключить. |

#### Color (цвет)

| Поле | Описание |
|---|---|
| **Base Color** | Цвет у корня. |
| **Tip Color** | Цвет у вершины. Между ними linear-градиент + ещё один lerp по clump-шуму (канал G). |

---

### GrassTerrainSettings

Настройки производительности и раскладки. Можно шарить между несколькими террейнами.

#### Tile Layout

| Поле | По умолч. | Описание |
|---|---|---|
| **Tile Size** | `16` (≥2) | Размер тайла в метрах. Меньше = точнее culling, но больше диспатчей за кадр. |
| **Blades Per Tile Axis** | `96` (4..256) | Травинок по оси внутри тайла. Всего на тайл = N×N. Сетка джиттерится, чтобы не выглядеть решёткой. |

#### Culling

| Поле | По умолч. | Описание |
|---|---|---|
| **Tile Cull Distance** | `80` (≥2) | Тайлы дальше этой дистанции вообще не генерируются. |
| **Max Blade Distance** | `60` (≥2) | Per-blade максимум: травинки дальше отбрасываются на GPU. |

#### LOD

| Поле | По умолч. | Описание |
|---|---|---|
| **High Lod Distance** | `18` (≥0) | Ближе этой дистанции — high-LOD меш (4 сегмента). |
| **Lod Blend Band** | `6` (≥0.1) | Ширина зоны кросс-фейда у переходов LOD и у max-distance. Больше = мягче, но больше overdraw. |

#### Distance Thinning (прореживание по дистанции)

| Поле | По умолч. | Описание |
|---|---|---|
| **Thinning Start Distance** | `30` (≥0) | С этой дистанции трава начинает редеть. Ближе — не прореживается; high-LOD не трогается никогда. |
| **Thinning Strength** | `0` (0..1) | Насколько агрессивно редеет к max-distance. 0 = нет; 1 = максимум (почти все дальние low-LOD травинки выпадают). |

#### Clumping noise

| Поле | По умолч. | Описание |
|---|---|---|
| **Clump Scale** | `0.05` | Мировой масштаб clump-шума. Меньше = более крупные куртины. |

#### Buffer Capacity

| Поле | По умолч. | Описание |
|---|---|---|
| **Max High Lod Blades** | `600 000` (≥1024) | Потолок high-LOD буфера на кадр (по всем тайлам). Переполнение → травинки теряются. |
| **Max Low Lod Blades** | `800 000` (≥1024) | То же для low-LOD. |

---

## 🎨 Маска террейна (`Grass Mask`)

RGBA-текстура размером с террейн (или меньше — растягивается по UV). Каналы:

| Канал | Назначение |
|---|---|
| **R** | **Размещение / плотность** (0..1). 0 — травы нет; >0 — растёт. Единственный обязательный канал для базовой травы. |
| **G** | **Множитель высоты** (0..1). 1 — полная высота из `GrassType`; 0 — нет травы. При G < 0.5 включается folded-blade оптимизация. |
| **B** | **Clamp / пучкование** (0..1). ОПЦИОНАЛЬНО. Где закрашен — растёт число травинок из одной точки + небольшой буст высоты (+25%) и яркости (+20%) на максимуме. Среднее число травинок на ячейку: B=0 → 1–2 (база), B=1 → к `Max Blades Per Cell`. |
| **A** | Зарезервирован. |

> 📸 **СКРИН СЮДА:** пример маски (можно показать раскраску по каналам R/G/B). *(строку удалить после вставки скрина)*

**Folded blade** — когда итоговая высота травинки опускается ниже `GrassType.FoldHeight`, compute эмитит одну «сложенную пополам» травинку. Шейдер (`ApplyBladeFold` в `GrassCommon.hlsl`) изгибает меш в точке `v=0.5` — получается V-образная фигура: середина меша становится корнем, а оба конца — вершинами двух коротких суб-травинок. Одна инстанция = две видимые травинки «бесплатно». `FoldHeight = 0` выключает оптимизацию.

---

## 🖌 Покраска маски

Рисовать удобнее всего прямо из инспектора террейна. У `GrassTerrain` в ряду кистей террейна появляется **«TerrainGrassSystem/Paint Grass»**.

> 📸 **СКРИН СЮДА:** инспектор террейна с активной кистью Paint Grass (виден выбор Mode и Mask Utilities). *(строку удалить после вставки скрина)*

**Режимы (Mode):**

| Режим | Канал | Что делает |
|---|---|---|
| **Placement (R)** | R | Где растёт трава. Базовый режим, единственный нужный для дефолта. |
| **Height (G)** | G | Локально варьировать высоту травы (0 = нет, 1 = полная). |
| **Spacing / Clamp (B)** | B | Пучкование: где закрасил — пучки из нескольких травинок с одного корня. |

**Управление:**

- Зажатый **Ctrl** во время рисования — вычитает значение того же канала (Ctrl + Placement = убрать траву, и т.д.).
- **Ctrl + Z** / **Ctrl + Shift + Z** (или **Ctrl + Y**) — undo / redo, пока активна кисть. История in-memory, живёт до domain reload, до 16 штрихов.

**Mask Utilities:**

- **Clear All Grass** — обнулить весь канал размещения.
- **Save Now** — принудительно сохранить PNG маски (иначе сохраняется отложенно после штриха).

---

## ⚙️ Окно «Noise & Mask Generator»

**`Tools → GrassSystem → Noise & Mask Generator`**

Поле **Texture Type** переключает наполнение окна между двумя генераторами:

### Clump Noise (Perlin)

| Параметр | Описание |
|---|---|
| **Output Size** | Размер текстуры (64…4096). |
| **Seed** | Зерно. Кнопка **Re-roll Seed** даёт новый паттерн при тех же настройках. |
| **Scale** | Плотность фич: больше = мельче паттерн. |
| **Octaves** | Число слоёв fBm. Больше = богаче/«шумнее». |
| **Persistence** | Громкость каждого следующего слоя. Низкая = гладко, высокая = шумно. |
| **Lacunarity** | Рост частоты на слой. 2 = стандартный fBm. |
| **Contrast** | Резкость переходов свет/тень. 1 = нейтрально, 0 = плоско, >1 = жёстко. |
| **Brightness** | Сдвиг яркости после контраста. |

Внизу — **живой превью** и кнопка **Generate Clump Noise**.

### Density Mask

| Параметр | Описание |
|---|---|
| **Size** | Размер маски (рекомендуется 1024). |

Кнопка **Generate Blank Density Mask** создаёт пустую маску (R=0 → травы нет, пока не покрасишь).

**Export Folder** (общее для обоих типов) — папка внутри `Assets/`, куда сохраняется PNG. Кнопка **Browse…** открывает выбор папки сразу в `Assets` текущего проекта.

---

## 🚄 Производительность

Дефолтные настройки целятся в ~100 000 видимых травинок на 30-метровой панораме.

**Ориентиры:**

- GPU-кадр на RTX 3060 — ~1.2 мс на compute + ~0.8 мс на рендер при 100k травинок.
- На мобильных: `Blades Per Tile Axis` → 48–64, `Max Blade Distance` → 25 м.
- Длинная трава (`Base Height` > 1 м) сильно повышает overdraw — режь `High Lod Distance` / `Max Blade Distance` первым делом.

**Тюнинг (по убыванию эффекта):**

1. `Max Blade Distance` ↓
2. `Blades Per Tile Axis` ↓
3. `Tile Cull Distance` ↓
4. `Lod Blend Band` ↓ (меньше overdraw в зоне кросс-фейда)

---

## 🛠 Troubleshooting

**Трава вообще не рендерится**
- В `GrassTerrain` обязательны: Compute Shader, оба материала, маска, шум, Type, Settings. Если хоть одно поле пустое — компонент молча скипает кадр.
- Маска пустая по умолчанию — **покрась траву** кистью Placement.
- `Camera.main` не найдена → назначь `Override Camera` или поставь тег MainCamera.

**Трава мерцает / пропадает на расстоянии**
- `Lod Blend Band` слишком мал относительно `High Lod Distance` / `Max Blade Distance`. Подними до 4–8 м.
- Переполняется `Max High Lod Blades` / `Max Low Lod Blades` — глянь в RenderDoc или подними потолок.

**Трава «лежит» на одной высоте, игнорируя рельеф**
- Compute читает `terrain.terrainData.heightmapTexture`. Убедись, что Terrain не смещён нестандартным Transform и `terrain.transform.position` совпадает с используемым origin.

**Ветер не двигается в Scene View**
- `GrassWind` в Edit-mode использует `EditorApplication.timeSinceStartup`. Если статично — проверь, есть ли в сцене активный объект с `GrassWind` и не отключён ли его GameObject.

**После переключения LOD травинки «лопаются»**
- Слишком резкий `Lod Blend Band`. Увеличь (~1/3 от `High Lod Distance`).

**Сильный overdraw / падает FPS**
- См. раздел [Производительность](#-производительность). Главные виновники — `Max Blade Distance` и `Blades Per Tile Axis`.

**Тени от травы отсутствуют**
- В ручном шейдере тег ShadowCasting уже выставлен.
- В Universal Renderer Asset проверь, что Cast/Receive Shadows не отключены глобально.

---

## 🧪 Вариант с ShaderGraph

`GrassBladeGraph.hlsl` содержит Custom Function `GrassBlade_Vertex_float`. Чтобы заменить ручной HLSL-шейдер на ShaderGraph:

1. **Create → Shader Graph → URP → Lit Shader Graph**, имя `GrassBlade`.
2. **Graph Inspector → Graph Settings:** Material = Lit, Surface Type = Opaque, Render Face = Both.
3. **Properties (exposed, Float):**

   | Property | Значение |
   |---|---|
   | `_CamFacingThreshold` | `0.8` (edge-on cos threshold) |
   | `_CamFacingMaxAngle` | `8.0` (макс. угол твиста на вершине, °) |
   | `_CurvedNormalAmount` | `0.7` |
   | `_AOStrength` | `0.5` |
   | `_TipBoost` | `1.2` |
   | `_Smoothness` | `0.05` |

4. **Custom Function нода:** Mode = File, Source = `GrassBladeGraph.hlsl`, Name = `GrassBlade_Vertex`.
   - **Inputs:** `PositionOS` (Vector3), `InstanceID` (Float), `CamFacingT` (Float), `CamFacingMaxA` (Float), `CurvedNAmt` (Float), `AOStrength` (Float), `TipBoost` (Float)
   - **Outputs:** `PositionWS` (Vector3), `NormalWS` (Vector3), `ColorBase` (Vector3), `UV` (Vector2)
5. **Подключения:**
   - Position (Object Space) → `PositionOS`
   - Instance ID → `InstanceID`
   - Property-ноды → соответствующие входы
   - `PositionWS` → блок Vertex Position (напрямую, без Transform-ноды)
   - `NormalWS` → блок Vertex Normal (напрямую)
   - `ColorBase` → блок Base Color
   - `_Smoothness` → блок Smoothness

   > `Graphics.RenderMeshIndirect` использует единичную model-матрицу, поэтому Object Space ≡ World Space — Transform (World → Object) был бы no-op.

6. **(опц.) Custom Function `GrassBlade_Masks`** — отдельные выходы AO и маски кончиков (вместо пред-смешанного `ColorBase`). Полезно, если миксуешь цвет в графе сам.
   - **Inputs:** `V` (Float, из Split по Position(OS).G), `AOStrength` (Float)
   - **Outputs:** `AO` (Float), `TipMask` (Float)

   Типичная разводка цвета:

   ```
   GrassType_Color ─┐
                    ├─ Lerp ──┐
   TipColor ────────┘ T:TipMask ├─ Multiply ──┐
                                │             ├─ Multiply ── Base Color
                           AO ──┘             │
                                     GrassTint ┘  (Color property)
   ```

   Если используешь эту схему — **не** подключай `ColorBase` из первой ноды в Base Color, иначе AO/TipBoost умножатся дважды.

7. Создай Material из графа, назначь в `GrassTerrain`. После проверки `GrassBlade.shader` можно удалить.

ShaderGraph автоматически генерирует ShadowCaster и DepthOnly проходы.

---

## 🔌 Расширение

**Взаимодействие персонажа (примятие):**
- В `GrassWind` добавь `Vector4 _GrassImpactSource` (xyz = world pos, w = radius).
- В `GrassBlade.shader` / `GrassBladeGraph.hlsl` добавь отклонение `blade.position` в зависимости от `dist(worldPos, impact)`.
- Из кода каждый кадр: `Shader.SetGlobalVector("_GrassImpactSource", ...)`.

**Streaming больших миров (несколько террейнов):**
- `GrassRenderer` — один на террейн. Делишь мир на 10×10 terrain-тайлов → ставь `GrassTerrain` на каждый, они не мешают друг другу.

**Другой формат маски / источник density:**
- В `GrassCompute.compute` правь `SampleGrassMask` и переинтерпретацию каналов в `CSGenerate` (placement / heightMul / densityMul).
