# Warp and blend for projection — what the field does, and what Patterns does

*Round 32 research. The ask: for true projector blending Patterns needs warp and mapping;
how do others do it, what is the technique, how can it be simplified into the workflow —
and what happens in the middle of a 2 × 2 blend where all four projectors overlap.*

## 1. What the tools in the field do

Every projection tool solves the same three problems: **geometry** (put each projector's
picture where it belongs on the surface), **the join** (make two overlapping pictures read
as one), and **black** (make the overlaps' doubled black disappear). Auto-calibration adds a
fourth: a camera doing the first two by itself.

| Tool | Geometry | The join | Black | Camera |
|---|---|---|---|---|
| **Christie Twist** (projector-side) | corner-pin, then a grid/mesh warp of control points with bezier or linear interpolation; up to six projectors per canvas | 4-side edge blends with adjustable width and curve | black-level ("brightness uniformity") uplift of the non-overlap area | Mystique (optional) |
| **Disguise** (media server) | per-output warp: corner-pin, then a mesh; the surface itself is a 3D model, so the warp is a projection of the model | "Dynamic Blend": the blend zones are computed from the projectors' frusta on the model; soft-edge with gamma | black-level per output | QuickCal (points on the model), OmniCal (structured light cameras) |
| **Pixera** (media server) | corner-pin, FFD (free-form deformation — a bezier lattice), mesh; per-output | soft-edge blends with curve, width and gamma per edge | black level per output | camera-based calibration (partner tools) |
| **Resolume Arena** | "Advanced Output": slices with corner-pin and a bezier/mesh warp per slice | edge blending per slice edge: width, curve ("power"), luminance and gamma | none in the output stage (fixed in the projectors) | none |
| **MadMapper** | quads, and a mesh warp of any density on each surface, with bezier handles | soft-edge per quad edge | none | none |
| **VIOSO** (calibration) | camera-computed warp meshes exported to the server or the projector | camera-computed blend maps | **black level compensation** as its own pass: the non-overlap regions are lifted to the overlaps' floor, with feathered edges | yes — its whole point |
| **Scalable Display** (calibration) | camera-computed | camera-computed | black-level ("dark level") uplift | yes |
| **Epson / Panasonic / Barco** (projector-side) | keystone, corner-pin, curved-surface bends, then a grid | edge blending with width, start position and a gamma curve | "black level adjustment" of the non-overlap area, per zone — Epson exposes the 2 × 2 case explicitly with separate levels for the two-way bands and the four-way corner | Epson's camera-based Multi-Projection tools |

Sources read for this: Resolume's edge-blending manual page (resolume.com/support/en/edge-blending),
VIOSO's black-level compensation page (docs.vioso.com/calibration/blacklevel-compensation),
Epson's black-level adjustment pages for its edge-blending projectors, the DEXON and
Scalable Display write-ups on soft-edge blending, Christie Twist's product pages, Disguise's
help on Dynamic Blend, QuickCal and OmniCal (help.disguise.one), Pixera's help on warping,
FFD and soft-edge (help.pixera.one), the MadMapper mesh-warping guide, and the US patents on
black-level blending for tiled projection (the "raised pedestal" method every tool
implements in some form).

## 2. The technique

### 2.1 Geometry: corner-pin, bends, mesh

A projector never lands square. The corrections, in order of how often they are enough:

1. **Corner-pin (keystone).** Four corners dragged; the mapping is a **homography**
   (perspective), so straight lines stay straight inside the picture. This is exactly the
   correction for a flat screen and a projector off-axis. Patterns has had this since the
   rig work (`WarpMath.QuadWarp`, Heckbert's unit-square-to-quad).
2. **Edge bends.** A curved screen, a cyclorama, a lens's pincushion or barrel: the edges
   bow. The tools give each edge bezier handles. One number per edge — how far the middle
   bows out — is what an operator actually turns.
3. **Mesh / FFD.** A grid of control points (5 × 5, 9 × 9, more) each dragged, with linear
   or bezier interpolation between. Needed for a dome, a sphere, an irregular set piece, or
   when a cheap lens's distortion is not a clean bow.
4. **Camera calibration.** Structured light (gray codes or dots) shot by a camera gives the
   warp mesh and the blend map of every projector at once, to the pixel. This is what turns
   an afternoon of alignment into ten minutes, and it is the one thing every serious rig now
   assumes.

### 2.2 The join: the soft edge

Where two pictures overlap, each fades across the zone and the other fades the opposite way.
The rule that makes the join invisible: **the two fades must add up to one in light, not in
signal.** A projector's light is signal to the power gamma (≈2.2), so a fade that is linear
in signal is not linear in light and the join shows as a dark or bright band. Hence every
tool's "gamma" or "power" on the blend curve: the signal sent is the wanted light to the
power 1/γ. Patterns does this (`BlendMath.Weight(curve, t, gamma)`: the curve is the light,
raised to 1/γ to make the signal), so the two sides' weights add to one in light.

The curve's shape (S-curve, cosine, linear) matters less than that both sides use the same
one, mirrored, over the same width — a join where the widths differ by a pixel or the curves
differ shows as a fine line. Patterns' blend audit reads exactly this (`BlendAudit`).

### 2.3 The 2 × 2: what happens in the middle

In a 2 × 2 the middle is a square where **all four projectors overlap** (the size of the
overlap in each axis). Three things are true of it:

- **Light.** If every projector's blend is *separable* — a horizontal fade times a vertical
  fade — the four contributions in the corner sum to one exactly:
  (L + R) × (T + B) = 1 × 1, because each factor is a two-sided fade that sums to one. So
  the corner needs *nothing special* for the picture itself, as long as each projector's
  corner region is the product of its two edge fades, not one of them. Patterns draws the
  two bands as separate multiplies, so the corner is the product by construction; the test
  `AGridOfFourGetsASideAndAnEdgeEachAndNoCornerOfItsOwn` proves that a pure-corner overlap
  (the square each diagonal pair shares) gets no zone of its own.
- **Black.** Black does not fade. Each projector's black is light (a few tenths of a percent
  of white on a good laser, more on a lamp, much more on an LCD). In the two-way bands the
  floor is two blacks; in the four-way square it is *four*. On a dark scene the room sees a
  brighter cross with a brighter square where the cross meets — this is the thing people
  mean when they say "the middle of my 2 × 2 looks wrong".
- **Alignment.** Four pictures must agree at one point. A pixel of error in any of the four
  shows twice (once per axis) — the middle is where a mesh or a camera pays for itself.

### 2.4 Black-level matching

The cure every tool implements: **raise the black everywhere else to the brightest floor.**
The floor of the deepest overlap (two blacks in a row, four in a grid's corner) becomes the
canvas's black, and each projector adds a *pedestal* in each region of its picture so that
region's floor reaches it. Per projector, per region:

    pedestal (in light) = black × (deepest − coverage) / coverage

where *coverage* is how many projectors reach that region (1 outside the zones, 2 in a band,
4 in the corner) and *deepest* is the largest coverage on the canvas that projector is part
of. Check the 2 × 2: the single region gets 3 blacks (1 + 3 = 4); each two-way band gets 1
black from each of its two projectors (2 + 2 × 1 = 4); the corner gets nothing (4). A row
gets 1 black outside (1 + 1 = 2) and nothing in the band. The pedestal is added light, so it
goes through the inverse gamma like the fade does, and the whole canvas sits on one floor.
The price is contrast: the canvas's black is now four blacks bright. On lasers that is
invisible; on lamps it is the reason a 2 × 2 blend is a last resort and a 2 × 1 or a wider
lens is preferred.

The edges of the pedestal regions are hard steps, and that is right: the black floor steps
there too. Feathering them (VIOSO and Epson allow it) only hides a misaligned zone edge.

## 3. What Patterns does now (round 32)

- **Corner-pin** as before: the keystone is a perspective.
- **Edge bends**, new: four numbers on the Screens page — how far each edge bows outward at
  its middle, in the projector's own pixels. They make the twelve control points of one
  cubic Coons patch (`WarpMesh.Cubics`) that the pipeline draws the *finished* picture
  through — content, blend zones and black pedestal alike — under the keystone (so straight
  lines inside stay straight) and the rotation. Curved screens, cycloramas and lens bows are
  covered; a pixel-exact join on a dome is not (that is a mesh, §4).
- **Black-level matching**, new: one slider per screen, *Black level* (percent of white).
  `BlackLevel.Cells` tiles the projector's picture into the nine regions the zones make and
  counts the projectors reaching each; `BlackLevel.Signal` adds the pedestal by the rule
  above through the blend gamma; the pipeline adds it as light (`SKBlendMode.Plus`) after
  the fades. The Screens page's Edge blend readback says what a black scene will show
  ("the corner where four projectors meet as a brighter square") and what the pedestal is
  doing. The number is found on a black picture: slide until the bands and the square go.
- **Arrange as a blend grid**, new: columns × rows and an overlap; every screen that is on
  — projectors, or planned screens standing in for them — is laid out with the overlaps
  and put on automatic blend, so the zones follow the overlaps and every join comes out
  equal (`BlendGridLayout`). A 2 × 2 is one press, then the audit reads every join.
- **The walk-up**: put the Projection blend pattern up (its grid, hue-coded regions,
  hatched overlap zones with centre lines, the curve drawn as opposing ramps, and a grey
  check that exposes a doubled brightness), line each projector's grid up with its
  neighbour's using the corners and the bends, then the black picture and the black-level
  slider. Save it as a look — the rig travels with the show.

## 4. What is next, in order of worth

1. **A mesh** (a 5 × 5 to 17 × 17 grid of points, bezier between them) on top of the
   patch: the dome, the sphere, the set piece. The pipeline already draws through a patch;
   a mesh is a grid of patches (`DrawPatch` per cell, or `DrawVertices` over a fine
   triangulation), and the page needs a draggable grid over the preview.
2. **Camera calibration**: structured light out of the outputs, a webcam or a USB camera
   in, the mesh and the blend map computed. The pieces exist (the capture inputs, the
   pattern renderer, the mesh) — the solver is the work. The AI's place here is to *read*
   the calibration (which projector is the weak one, which join drifted) rather than to run
   it.
3. **Per-projector colour matching** beyond the trims: a white-point and a gamut match by
   measurement (a colorimeter on the capture input, or by eye on the grey check).
4. **Blend zone start and shape per edge** (Epson's "start position"): the fade beginning
   inside the overlap rather than at its edge, for projectors whose brightness falls off at
   the frame.
