# com.arisen.terrain

Backend-neutral terrain domain package for Arisen Engine.

This package owns terrain source and cooked schemas, tile identity, runtime
residency and queries, ECS terrain data, and deterministic LOD planning. It
must remain independent from concrete render pipelines, editor UI, and RHI
backends.

## Authoring source v2

- `TerrainRoot` (`.aristerrain`) stores the metadata GUID, double-precision
  world placement, X/Z sample spacing, height range, height source, shared-edge
  tile resolution, signed tile origin, layer-set reference, and persisted
  generated tile records. Version 2 optionally references a dimension-matched
  `.ariweights` `Rgba8Hex` raster; version 1 remains readable and resolves to
  channel-zero weights.
- `TerrainLayerSet` (`.ariterrainlayers`) preserves an ordered list of one to
  four layers. Every layer has explicit package/GUID references for albedo,
  normal, and ORM `Texture2D` inputs. Version 2 also stores tint,
  roughness/metallic multipliers, normal strength, and X/Z world tiling;
  version-1 layers receive compatible defaults.
- The only supported height input is binary PGM `P5` with `MaxValue: 65535`.
  Samples are big-endian 16-bit scalar heights; no color-space conversion is
  applied. The first raster row maps to local Z row zero.
- A generated tile key is exactly `x=<signed X>;z=<signed Z>`. Its GUID derives
  from the terrain root GUID, owning package ID, child kind `terrain-tile`, and
  that key. Runtime LOD never participates in source identity.

The source loaders reject unknown/duplicate YAML fields, stale generated tile
GUIDs, non-tileable dimensions, unsupported border/height formats, malformed
PGM headers, truncation, and trailing raster data before cooking begins.

## Creation and import

`TerrainImportPlanner` accepts a strict PGM source, indexed layer set,
double-precision world bounds, tile resolution, and signed tile origin. Its
preview is read-only and reports deterministic root/tile identity, package
`Assets` output paths, sample spacing, and optional active-world cell mapping.

`TerrainImportEmitter` replans before commit and rejects stale previews, path
escapes, foreign generated outputs, and destructive identity, grid, or world
layout changes without explicit confirmation. Source and metadata writes use a
backup-backed transaction. Unchanged tile coordinates retain their GUIDs,
owned stale tiles are removed on confirmed grid shrink, legacy flat generated
outputs migrate without identity changes, and failed installation restores the
previous file set.

## Cooked runtime

- `runtime.terrain-root.v2` (`.ariterrainroot`, cooked format 2) embeds root placement and
  sampling metadata, the ordered layer/texture dependency table, canonical
  material parameters, signed tile records, four-way neighbor GUIDs, per-tile
  bounds, payload sizes, and full-file SHA-256 hashes.
- `runtime.terrain-tile.v1` (`.ariterraintile`) stores one independently
  deployable tile with little-endian `R16` quantized heights, explicit height
  offset/scale, duplicated shared edges, four-channel normalized weights, and
  a deterministic per-LOD maximum geometric-error hierarchy.
- Both containers use fixed headers, an endian marker, aligned section
  directories, bounded counts/offsets, and SHA-256 over the directory and
  payload. Unknown required sections, overlap, nonzero padding, noncanonical
  identities, malformed numeric data, invalid weights/errors, and corruption
  fail before cooked data is returned.
- Root cooking compares every adjacent height and weight border before writing.
  Authored channels use exact largest-remainder normalization and always sum to
  255; all-zero samples fall back to layer zero.
  Byte-identical artifacts are left in place so unchanged tile timestamps and
  deployment inputs remain reusable.
- `TerrainRuntimeAssetCooker` is registered by this package for `TerrainRoot`
  and generated `TerrainTile` requests. Root cook output closes over every tile
  and unique layer texture without adding terrain parsing to `ArisenBuildTool`.

## Scene and world-cell ownership

- Required scene schema `TerrainTile` uses stable TypeId `0x54455252`, version
  1. The terrain package registers its codec through the resources-owned scene
  extension registry and unregisters it on package unload.
- Source and cooked payloads carry root/tile/layer identities, package IDs,
  signed tile coordinates, exact double world placement, visibility/shadow
  flags, and the quality preference. They validate against the source terrain
  root during authoring/cooking and against `runtime.terrain-root.v2` in a
  cooked-only runtime.
- The ECS component is blittable and stores only stable runtime identity,
  placement, and flags. Editor selection and selected render LOD are not ECS
  state.
- Tile GUID is an exclusive scene-instance ownership key. A second active cell
  cannot expose the same tile; successful unload releases ownership for a
  deterministic reload. Positive interior X/Z borders use half-open ownership,
  while outer root borders remain inclusive.

## Runtime queries and LOD planning

- Prepared roots and tiles publish through `ITerrainRuntimeDataStore` with a
  monotonically increasing generation. Releasing an old handle cannot remove
  its replacement. Published tile data contains immutable fixed patches of at
  most 16 sample intervals, conservative min/max bounds, and localized
  geometric-error levels.
- `ITerrainQueryService.Query` accepts a double-precision `WorldPosition` and
  returns `InvalidPosition`, `OutsideTerrain`, `Unavailable`, or `Available`.
  It performs no loading. An available result requires the resolved tile to be
  resident and actively owned by ECS, and includes bilinear height, gradient
  normal, normalized four-channel weights, tile identity, and generation.
- Positive interior X/Z borders resolve to the positive tile. This is the same
  deterministic ownership rule used by scene components; outer root borders
  remain queryable.
- `ITerrainLodPlanner.Plan` consumes a double-world camera, render origin,
  projection and viewport parameters, plus bounded LOD settings. It emits a
  reusable ordered span of generation-qualified patch records containing
  double-world bounds, origin-relative float bounds, selected sample step,
  screen/geometric error, and four-edge stitch masks.
- Selection applies hysteresis, optional frustum culling, a maximum one-level
  adjacent-patch delta, and nearest-first retention when the patch budget is
  exceeded. Warming the planner establishes reusable candidate/output arrays;
  steady-state planning allocates no managed objects per patch.

## Diagnostics snapshots

`ITerrainDiagnostics` exposes one immutable, bounded snapshot published during
render setup through `ITerrainDiagnosticsPublisher`. The snapshot joins cooked
root, layer, and tile identity; selected LOD patches; world bounds and errors;
CPU/GPU residency and owner attribution; dirty/reload-failure state; four-way
neighbor availability; exact shared-height seam checks; and the current camera
terrain query. Roots, tiles, patches, resources, owners, and diagnostic text
all have explicit caps. Editor readers atomically acquire a completed snapshot
and never inspect mutable ECS, renderer, or RHI state.
