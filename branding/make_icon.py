"""SpecTuner icon builder.

Builds the icon scene from nothing in Blender and renders it. Run headless:

  blender -b --factory-startup -P make_icon.py -- [options]

Options (all optional):
  --concept NAME     gauge | dpad | chip | pad   (default: gauge, the chosen design; the others are
                     the rejected explorations, kept so their renders can be reproduced)
  --variant NAME     full | small          (small = thicker, simpler shapes for <= 32 px)
  --size N           render one PNG of N px (with --out) instead of the full set
  --out PATH         output PNG for --size
  --all              render the full deliverable set (default when --size is absent)
  --samples N        Cycles samples per pixel (default 64; images are supersampled anyway)
  --blend-dir DIR    also save the two scenes as icon.blend and icon-small.blend in DIR, for
                     inspecting them in Blender (default: not saved). Use a scratch folder outside
                     the repository: a .blend file records details of the machine that saved it

With --all the script writes, next to itself:
  icon-{1024,512,256,128,64,48,32,24,16}.png  (<= 32 px from the simplified "small" variant)
  icon.ico                                    (16-256 px; DIB entries up to 64 px, PNG entries for 128 and 256)
  logo-mark.svg, logo-mark-color.svg          (flat mark, generated from the same constants as the 3D gauge)
  contact-sheet.png                           (review sheet; the flat marks are rendered from the SVG files)
Intermediate renders go to the system temp folder and are removed afterwards.
Every image is rendered at up to 8x its size and box-filtered down, so small sizes stay crisp.
"""

import math
import os
import struct
import sys
import tempfile

import bpy
import bmesh
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
TMP_RENDER = os.path.join(tempfile.gettempdir(), f"spectuner_icon_render_{os.getpid()}.png")

# ---------------------------------------------------------------- palette (sRGB hex)
PALETTE = {
    "tile": "#232733",      # graphite tile
    "tile_edge": "#3a4152", # lighter rim (via material gradient)
    "track": "#3b4150",     # unlit part of the gauge
    "accent": "#ff7a2f",    # SpecTuner orange (UI accent)
    "accent_hot": "#ffa63d",# warm end of the gauge
    "white": "#f2f3f6",     # needle hub / d-pad
    "metal": "#b9bfcc",
}


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def hex_rgba(h, a=1.0):
    h = h.lstrip("#")
    r, g, b = (int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))
    return (srgb_to_linear(r), srgb_to_linear(g), srgb_to_linear(b), a)


# ---------------------------------------------------------------- args
def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opts = {"concept": "gauge", "variant": "full", "size": None, "out": None, "all": False, "samples": 64, "blend-dir": None}
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--all":
            opts["all"] = True
            i += 1
            continue
        key = a.lstrip("-")
        val = argv[i + 1]
        opts[key] = int(val) if key in ("size", "samples") else val
        i += 2
    if opts["size"] is None:
        opts["all"] = True
    return opts


# ---------------------------------------------------------------- scene helpers
def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.use_denoising = True
    scene.cycles.pixel_filter_type = "BOX"
    scene.cycles.filter_width = 1.0
    scene.cycles.max_bounces = 6
    scene.render.film_transparent = True
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "8"
    world = bpy.data.worlds.new("World")
    scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    bg.inputs["Color"].default_value = hex_rgba("#8a90a0")
    bg.inputs["Strength"].default_value = 0.35
    return scene


def material(name, hex_color, rough=0.4, metal=0.0, emit=0.0, coat=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    p = m.node_tree.nodes.get("Principled BSDF")
    p.inputs["Base Color"].default_value = hex_rgba(hex_color)
    p.inputs["Roughness"].default_value = rough
    p.inputs["Metallic"].default_value = metal
    if coat:
        p.inputs["Coat Weight"].default_value = coat
        p.inputs["Coat Roughness"].default_value = 0.15
    if emit:
        p.inputs["Emission Color"].default_value = hex_rgba(hex_color)
        p.inputs["Emission Strength"].default_value = emit
    return m


def gradient_material(name, hex_a, hex_b, rough=0.35, emit=0.0, axis="X"):
    """Linear gradient across object-space X (or Y), used for the gauge arc and the tile."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    p = nt.nodes.get("Principled BSDF")
    tc = nt.nodes.new("ShaderNodeTexCoord")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    mr = nt.nodes.new("ShaderNodeMapRange")
    mr.inputs["From Min"].default_value = -1.0
    mr.inputs["From Max"].default_value = 1.0
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = hex_rgba(hex_a)
    ramp.color_ramp.elements[1].color = hex_rgba(hex_b)
    nt.links.new(tc.outputs["Object"], sep.inputs[0])
    nt.links.new(sep.outputs[axis], mr.inputs["Value"])
    nt.links.new(mr.outputs["Result"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], p.inputs["Base Color"])
    p.inputs["Roughness"].default_value = rough
    if emit:
        nt.links.new(ramp.outputs["Color"], p.inputs["Emission Color"])
        p.inputs["Emission Strength"].default_value = emit
    return m


def link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


def smooth(obj, angle=35):
    bpy.context.view_layer.objects.active = obj
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    obj.select_set(True)
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(angle))


def prism(name, outline, z0, z1, mat, bevel=0.03, segs=5):
    """Extrude a 2D outline (list of (x, y), CCW) between z0 and z1 and round its rims."""
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bot = [bm.verts.new((x, y, z0)) for x, y in outline]
    top = [bm.verts.new((x, y, z1)) for x, y in outline]
    bm.faces.new(top)
    bm.faces.new(list(reversed(bot)))
    n = len(outline)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((bot[i], bot[j], top[j], top[i]))
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    obj = link(bpy.data.objects.new(name, me))
    obj.data.materials.append(mat)
    if bevel > 0:
        mod = obj.modifiers.new("bevel", "BEVEL")
        mod.width = bevel
        mod.segments = segs
        mod.limit_method = "ANGLE"
        mod.angle_limit = math.radians(50)
        mod.profile = 0.5
    smooth(obj)
    return obj


def rounded_rect(w, h, r, n=12):
    pts = []
    corners = [(w / 2 - r, h / 2 - r, 0), (-w / 2 + r, h / 2 - r, 90),
               (-w / 2 + r, -h / 2 + r, 180), (w / 2 - r, -h / 2 + r, 270)]
    for cx, cy, a0 in corners:
        for k in range(n + 1):
            a = math.radians(a0 + 90 * k / n)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


def circle_pts(cx, cy, r, n=64):
    return [(cx + r * math.cos(2 * math.pi * k / n), cy + r * math.sin(2 * math.pi * k / n)) for k in range(n)]


def arc_band(name, r, width, a_start, a_end, z0, z1, mat, bevel=0.025, n=96):
    """Flat band along an arc with round end caps (angles in degrees, CCW from +X)."""
    ri, ro = r - width / 2, r + width / 2
    steps = max(8, int(n * abs(a_end - a_start) / 360))
    outer = [(ro * math.cos(math.radians(a_start + (a_end - a_start) * k / steps)),
              ro * math.sin(math.radians(a_start + (a_end - a_start) * k / steps))) for k in range(steps + 1)]
    inner = [(ri * math.cos(math.radians(a_start + (a_end - a_start) * k / steps)),
              ri * math.sin(math.radians(a_start + (a_end - a_start) * k / steps))) for k in range(steps + 1)]

    def cap(a_deg, phi0, d):
        """Semicircle around the arc end at a_deg, starting at angle phi0 and turning by 180 degrees."""
        c = (r * math.cos(math.radians(a_deg)), r * math.sin(math.radians(a_deg)))
        return [(c[0] + width / 2 * math.cos(phi0 + d * math.pi * k / 12),
                 c[1] + width / 2 * math.sin(phi0 + d * math.pi * k / 12)) for k in range(1, 12)]

    # outline: outer forward, end cap (outer -> forward tangent -> inner), inner backward, start cap
    d = 1 if a_end > a_start else -1
    outline = (outer + cap(a_end, math.radians(a_end), d) + list(reversed(inner))
               + cap(a_start, math.radians(a_start) + math.pi, d))
    # make sure the polygon is CCW
    area = sum(outline[i][0] * outline[(i + 1) % len(outline)][1] - outline[(i + 1) % len(outline)][0] * outline[i][1]
               for i in range(len(outline)))
    if area < 0:
        outline = list(reversed(outline))
    return prism(name, outline, z0, z1, mat, bevel=bevel)


def needle_outline(length, base_r, tip_r, angle_deg, n=10):
    """Tapered needle from the centre, round base and round tip, pointing at angle_deg."""
    a = math.radians(angle_deg)
    pts = []
    # tip semicircle
    tx, ty = length * math.cos(a), length * math.sin(a)
    half = math.asin(max(-1, min(1, (base_r - tip_r) / length)))
    for k in range(n + 1):
        phi = a - (math.pi / 2 - half) + (math.pi - 2 * half) * k / n
        pts.append((tx + tip_r * math.cos(phi), ty + tip_r * math.sin(phi)))
    # base semicircle (the long way round)
    for k in range(2 * n + 1):
        phi = a + (math.pi / 2 - half) + (math.pi + 2 * half) * k / (2 * n)
        pts.append((base_r * math.cos(phi), base_r * math.sin(phi)))
    return pts


def setup_camera_and_lights(scene, ortho=2.36, tilt=0.0):
    cam_data = bpy.data.cameras.new("Camera")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = ortho
    cam = link(bpy.data.objects.new("Camera", cam_data))
    cam.location = (0, -tilt * 10, 10)
    cam.rotation_euler = (math.atan2(tilt * 10, 10), 0, 0)
    scene.camera = cam

    def area(name, loc, size, power, color="#ffffff"):
        ld = bpy.data.lights.new(name, "AREA")
        ld.shape = "DISK"
        ld.size = size
        ld.energy = power
        ld.color = hex_rgba(color)[:3]
        lo = link(bpy.data.objects.new(name, ld))
        lo.location = loc
        direction = -np.array(loc, dtype=float)
        # aim at origin
        from mathutils import Vector
        lo.rotation_euler = Vector(direction).to_track_quat("-Z", "Y").to_euler()
        return lo

    area("Key", (-3.0, 3.5, 6.0), 5.0, 480)
    area("Fill", (4.0, -2.0, 4.0), 6.0, 110, "#dfe8ff")
    area("Rim", (0.5, 5.5, 1.2), 3.0, 200, "#ffe2c8")


# ---------------------------------------------------------------- shared tile
TILE_W = 2.0
TILE_R = 0.46
TILE_TOP = 0.15


def build_tile():
    mat = gradient_material("Tile", "#1c1f29", "#2c3140", rough=0.42, axis="Y")
    p = mat.node_tree.nodes.get("Principled BSDF")
    p.inputs["Coat Weight"].default_value = 0.35
    p.inputs["Coat Roughness"].default_value = 0.2
    return prism("Tile", rounded_rect(TILE_W, TILE_W, TILE_R), -0.15, TILE_TOP, mat, bevel=0.07, segs=8)


# ---------------------------------------------------------------- concepts
GAUGE_START, GAUGE_END = 220.0, -40.0  # degrees, CCW from +X; 260 degree sweep open at the bottom
NEEDLE_ANGLE = 38.0                    # where the needle points (upper right = "tuned up")


def concept_gauge(small=False):
    """Performance gauge: a 260-degree arc, lit orange up to the needle, with a white needle and hub."""
    build_tile()
    accent = gradient_material("Accent", "#ff6a26", PALETTE["accent_hot"], rough=0.3, emit=0.2, axis="X")
    track = material("Track", PALETTE["track"], rough=0.5)
    white = material("White", PALETTE["white"], rough=0.3, coat=0.4)
    r = 0.62 if not small else 0.6
    w = 0.2 if not small else 0.3
    z0, z1 = TILE_TOP - 0.02, TILE_TOP + (0.07 if not small else 0.06)
    gap = 0.0 if small else 9.0  # small gap between lit and unlit parts at large sizes
    # lit part: from the start of the sweep down (clockwise) to the needle
    arc_band("ArcLit", r, w, GAUGE_START, NEEDLE_ANGLE + gap, z0, z1, accent)
    if not small:
        arc_band("ArcTrack", r, w, NEEDLE_ANGLE - gap, GAUGE_END, z0, z1 - 0.02, track)
    else:
        arc_band("ArcTrack", r, w, NEEDLE_ANGLE - 2, GAUGE_END, z0, z1 - 0.02, track)
    # needle and hub
    nl = 0.6 if not small else 0.56
    prism("Needle", needle_outline(nl, 0.1 if not small else 0.14, 0.045 if not small else 0.08, NEEDLE_ANGLE),
          TILE_TOP - 0.02, TILE_TOP + 0.13, white, bevel=0.025)
    if small:
        hub_r = 0.2
        prism("Hub", circle_pts(0, 0, hub_r), TILE_TOP - 0.02, TILE_TOP + 0.19, white, bevel=0.05)
        prism("HubDot", circle_pts(0, 0, hub_r * 0.42, 40), TILE_TOP + 0.15, TILE_TOP + 0.215, accent, bevel=0.015)
    else:
        # the hub is a game-pad d-pad: the "game" half of the idea, visible from 48 px up
        prism("Hub", sdf_outline(lambda x, y: sd_cross(x, y, HUB_ARM, HUB_HALF, HUB_ROUND), n=240, rmax=0.5),
              TILE_TOP - 0.02, TILE_TOP + 0.17, white, bevel=0.035, segs=6)
        prism("HubDot", circle_pts(0, 0, 0.065, 40), TILE_TOP + 0.13, TILE_TOP + 0.195, accent, bevel=0.015)


HUB_ARM, HUB_HALF, HUB_ROUND = 0.25, 0.085, 0.035  # d-pad hub: arm reach, arm half-width, corner rounding


def sd_box(x, y, hx, hy):
    dx, dy = abs(x) - hx, abs(y) - hy
    return math.hypot(max(dx, 0), max(dy, 0)) + min(max(dx, dy), 0)


def sd_cross(x, y, reach, half, rnd):
    return min(sd_box(x, y, reach - rnd, half - rnd), sd_box(x, y, half - rnd, reach - rnd)) - rnd


def concept_dpad(small=False):
    """Game-pad d-pad whose centre is a tuning knob; the 'up' arm is lit orange."""
    build_tile()
    white = material("White", PALETTE["white"], rough=0.32, coat=0.3)
    accent = material("Accent", PALETTE["accent"], rough=0.3, emit=0.35)
    dark = material("Dark", "#2a2f3c", rough=0.45)
    arm_w, arm_l = 0.5, 1.5
    hw, hl = arm_w / 2, arm_l / 2
    cross = [(hw, -hl), (hw, -hw), (hl, -hw), (hl, hw), (hw, hw), (hw, hl), (-hw, hl), (-hw, hw), (-hl, hw), (-hl, -hw),
             (-hw, -hw), (-hw, -hl)]
    # round the cross corners slightly by insetting a polygon with many points: rely on bevel instead
    prism("Cross", cross, TILE_TOP - 0.02, TILE_TOP + 0.12, white, bevel=0.06, segs=6)
    # orange arrow marks on the arms (up arm brightest)
    tri = lambda cx, cy, s, rot: [(cx + s * math.cos(math.radians(rot + a)), cy + s * math.sin(math.radians(rot + a)))
                                  for a in (90, 210, 330)]
    prism("ArrowUp", tri(0, 0.5, 0.13, 0), TILE_TOP + 0.1, TILE_TOP + 0.16, accent, bevel=0.01)
    # knob in the centre
    prism("KnobRing", circle_pts(0, 0, 0.36), TILE_TOP + 0.08, TILE_TOP + 0.15, dark, bevel=0.02)
    prism("Knob", circle_pts(0, 0, 0.27), TILE_TOP + 0.1, TILE_TOP + 0.26, white, bevel=0.05)
    prism("KnobMark", rounded_rect(0.07, 0.22, 0.035), TILE_TOP + 0.24, TILE_TOP + 0.285, accent, bevel=0.01)
    obj = bpy.data.objects["KnobMark"]
    obj.location = (0.08, 0.08, 0)
    obj.rotation_euler = (0, 0, math.radians(-45))


def concept_chip(small=False):
    """A processor chip (the PC's specs) with a tuning knob on top: free-standing, no tile."""
    body = gradient_material("ChipBody", "#1d212b", "#303646", rough=0.4, axis="Y")
    metal = material("Pins", PALETTE["metal"], rough=0.25, metal=1.0)
    accent = material("Accent", PALETTE["accent"], rough=0.3, emit=0.35)
    white = material("White", PALETTE["white"], rough=0.3, coat=0.3)
    track = material("Track", PALETTE["track"], rough=0.5)
    # pins: 3 per side
    for side in range(4):
        for k in (-0.42, 0, 0.42):
            pin = rounded_rect(0.2, 0.36, 0.08)
            pts = [(x + k, y + 0.86) for x, y in pin]
            rot = math.radians(90 * side)
            pts = [(x * math.cos(rot) - y * math.sin(rot), x * math.sin(rot) + y * math.cos(rot)) for x, y in pts]
            prism(f"Pin{side}{k}", pts, -0.1, 0.0, metal, bevel=0.02)
    prism("Chip", rounded_rect(1.5, 1.5, 0.22), -0.12, 0.12, body, bevel=0.05)
    # knob with ticks
    prism("KnobBase", circle_pts(0, 0, 0.5), 0.1, 0.16, track, bevel=0.02)
    arc_band("Ticks", 0.5, 0.09, 220, 40, 0.14, 0.18, accent, bevel=0.01)
    prism("Knob", circle_pts(0, 0, 0.36), 0.12, 0.34, white, bevel=0.06)
    mark = prism("KnobMark", rounded_rect(0.09, 0.26, 0.045), 0.32, 0.37, accent, bevel=0.012)
    mark.location = (0.12, 0.12, 0)
    mark.rotation_euler = (0, 0, math.radians(-45))


def sdf_outline(sdf, n=180, rmax=1.6):
    """Outline of a 2D signed-distance shape that is star-shaped around the origin."""
    pts = []
    for k in range(n):
        t = 2 * math.pi * k / n
        c, s = math.cos(t), math.sin(t)
        lo, hi = 0.0, rmax
        for _ in range(40):
            mid = (lo + hi) / 2
            if sdf(mid * c, mid * s) < 0:
                lo = mid
            else:
                hi = mid
        pts.append((lo * c, lo * s))
    return pts


def sd_capsule(x, y, ax, ay, bx, by, r):
    px, py, vx, vy = x - ax, y - ay, bx - ax, by - ay
    h = max(0.0, min(1.0, (px * vx + py * vy) / (vx * vx + vy * vy)))
    return math.hypot(px - vx * h, py - vy * h) - r


def smin(a, b, k):
    h = max(0.0, min(1.0, 0.5 + 0.5 * (b - a) / k))
    return b * (1 - h) + a * h - k * h * (1 - h)


def pad_sdf(x, y):
    body = sd_capsule(x, y, -0.5, 0.18, 0.5, 0.18, 0.46)
    gl = sd_capsule(x, y, -0.6, 0.05, -0.74, -0.5, 0.3)
    gr = sd_capsule(x, y, 0.6, 0.05, 0.74, -0.5, 0.3)
    return smin(smin(body, gl, 0.2), gr, 0.2)


def concept_pad(small=False):
    """A game-pad body whose face is a performance gauge: free-standing, no tile."""
    body = gradient_material("PadBody", "#1b1e27", "#2d3241", rough=0.4, axis="Y")
    p = body.node_tree.nodes.get("Principled BSDF")
    p.inputs["Coat Weight"].default_value = 0.35
    accent = gradient_material("Accent", PALETTE["accent"], PALETTE["accent_hot"], rough=0.3, emit=0.35, axis="X")
    track = material("Track", PALETTE["track"], rough=0.5)
    white = material("White", PALETTE["white"], rough=0.3, coat=0.4)
    prism("Pad", sdf_outline(pad_sdf), -0.2, 0.12, body, bevel=0.08, segs=8)
    cy = 0.14
    r, w = 0.36, 0.13
    for o in (arc_band("ArcLit", r, w, 210, 40, 0.1, 0.17, accent),
              arc_band("ArcTrack", r, w, 32, -30, 0.1, 0.15, track)):
        o.location.y = cy
    n = prism("Needle", needle_outline(0.36, 0.07, 0.035, 40), 0.1, 0.2, white, bevel=0.02)
    h = prism("Hub", circle_pts(0, 0, 0.1), 0.1, 0.24, white, bevel=0.035)
    n.location.y = h.location.y = cy


CONCEPTS = {"gauge": concept_gauge, "dpad": concept_dpad, "chip": concept_chip, "pad": concept_pad}


def build(concept, variant):
    scene = reset_scene()
    CONCEPTS[concept](small=(variant == "small"))
    setup_camera_and_lights(scene)
    scene["drop_shadow"] = variant == "full"
    return scene


# ---------------------------------------------------------------- rendering and image IO
def render_array(scene, px, samples):
    scene.render.resolution_x = px
    scene.render.resolution_y = px
    scene.render.resolution_percentage = 100
    scene.cycles.samples = samples
    tmp = TMP_RENDER
    scene.render.filepath = tmp
    bpy.ops.render.render(write_still=True)
    return load_png(tmp)


def load_png(path):
    img = bpy.data.images.load(path, check_existing=False)
    img.colorspace_settings.name = "Non-Color"  # keep the stored 8-bit values as they are
    w, h = img.size
    arr = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(arr)
    bpy.data.images.remove(img)
    return arr.reshape(h, w, 4)[::-1].copy()  # top row first


def save_png(arr, path):
    h, w = arr.shape[:2]
    img = bpy.data.images.new("out", w, h, alpha=True)
    img.colorspace_settings.name = "Non-Color"
    img.alpha_mode = "STRAIGHT"
    img.pixels.foreach_set(np.clip(arr[::-1], 0, 1).astype(np.float32).ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def box_down(arr, size):
    """Area-average downsample with premultiplied alpha (straight alpha in and out)."""
    h = arr.shape[0]
    k = h // size
    assert k * size == h
    a = arr[..., 3:4]
    pre = np.concatenate([arr[..., :3] * a, a], axis=2)
    pre = pre.reshape(size, k, size, k, 4).mean(axis=(1, 3))
    out = pre.copy()
    alpha = pre[..., 3:4]
    out[..., :3] = np.where(alpha > 1e-6, pre[..., :3] / np.maximum(alpha, 1e-6), 0)
    return out


def render_size(scene, size, samples, max_px=1024):
    k = max(1, min(8, max_px // size))
    arr = render_array(scene, size * k, samples)
    if scene.get("drop_shadow") and size >= 48:
        arr = add_drop_shadow(arr)
    return box_down(arr, size) if k > 1 else arr


def blur(a, sigma):
    """Separable Gaussian blur of a 2D array."""
    rad = int(3 * sigma)
    x = np.arange(-rad, rad + 1)
    g = np.exp(-x * x / (2 * sigma * sigma))
    g /= g.sum()
    a = np.apply_along_axis(lambda r: np.convolve(r, g, mode="same"), 0, a)
    return np.apply_along_axis(lambda r: np.convolve(r, g, mode="same"), 1, a)


def add_drop_shadow(arr, opacity=0.45):
    """Soft shadow under the shape, drawn from its own alpha (kept inside the frame margin)."""
    h = arr.shape[0]
    small = 4
    a = arr[..., 3].reshape(h // small, small, h // small, small).mean(axis=(1, 3))
    sh = blur(a, h / small * 0.018)
    sh = np.roll(sh, int(h / small * 0.02), axis=0)
    sh = sh.repeat(small, 0).repeat(small, 1) * opacity
    out = arr.copy()
    fa = arr[..., 3]
    out_a = fa + sh * (1 - fa)
    for c in range(3):  # shadow colour is black, so straight colour = fg * fa / out_a
        out[..., c] = np.where(out_a > 1e-6, arr[..., c] * fa / np.maximum(out_a, 1e-6), 0)
    out[..., 3] = out_a
    return out


# ---------------------------------------------------------------- ICO
def png_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def dib_bytes(arr):
    """32-bit BGRA DIB (BITMAPINFOHEADER, bottom-up) with an AND mask, as classic ICO entries use."""
    h, w = arr.shape[:2]
    px = np.round(np.clip(arr, 0, 1) * 255).astype(np.uint8)
    bgra = px[::-1][..., [2, 1, 0, 3]]
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, w * h * 4, 0, 0, 0, 0)
    row_bytes = ((w + 31) // 32) * 4
    mask = bytearray()
    for row in px[::-1]:
        bits = bytearray(row_bytes)
        for x in range(w):
            if row[x, 3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bits
    return header + bgra.tobytes() + bytes(mask)


def write_ico(path, entries):
    """entries: list of (size, bytes). Sizes of 256 are stored as 0 in the directory."""
    out = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    blobs = b""
    for size, data in entries:
        dim = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data
    with open(path, "wb") as f:
        f.write(out + blobs)


# ---------------------------------------------------------------- flat vector mark (SVG)
# The SVG is generated from the same constants as the 3D gauge, so the two stay the same mark.
# It is the mark without the tile, scaled to fill a 64-unit viewBox.
SVG_S = 42.0           # SVG units per scene unit
SVG_C = (32.0, 35.0)   # where the gauge centre sits in the viewBox
SVG_GAP = 9.0          # degrees between the lit arc and the track (same as the 3D icon)
GAUGE_R, GAUGE_W = 0.62, 0.2
NEEDLE_L, NEEDLE_BASE, NEEDLE_TIP = 0.6, 0.1, 0.045
HUB_DOT_R = 0.065


def _p(x, y):
    return (SVG_C[0] + SVG_S * x, SVG_C[1] - SVG_S * y)


def _f(v):
    return f"{v:.2f}".rstrip("0").rstrip(".")


def _pt(p):
    return f"{_f(p[0])} {_f(p[1])}"


def _polar(r, a_deg):
    a = math.radians(a_deg)
    return _p(r * math.cos(a), r * math.sin(a))


def _sweep_toward(start, centre, forward):
    """SVG sweep flag for a half circle from `start` around `centre` that bulges toward `forward`."""
    vx, vy = start[0] - centre[0], start[1] - centre[1]
    mx, my = -vy, vx  # midpoint direction for sweep=1 (clockwise on screen, y down)
    return 1 if mx * forward[0] + my * forward[1] > 0 else 0


def svg_arc_band(a0, a1, r=GAUGE_R, w=GAUGE_W):
    """Closed path of an arc band with round caps, from a0 to a1 degrees (CCW from +X, maths convention)."""
    ro, ri, h = r + w / 2, r - w / 2, w / 2
    large = 1 if abs(a1 - a0) > 180 else 0
    cw = a1 < a0  # decreasing maths angle = clockwise on screen = SVG sweep 1
    so, si = (1 if cw else 0), (0 if cw else 1)
    o0, o1, i0, i1 = _polar(ro, a0), _polar(ro, a1), _polar(ri, a0), _polar(ri, a1)
    c0, c1 = _polar(r, a0), _polar(r, a1)
    sgn = -1 if cw else 1  # direction of travel in maths angle

    def tangent(a, s):
        t = math.radians(a + s * 90)
        return (math.cos(t), -math.sin(t))  # screen coordinates

    s_end = _sweep_toward(o1, c1, tangent(a1, sgn))
    s_start = _sweep_toward(i0, c0, tangent(a0, -sgn))
    return (f"M{_pt(o0)}A{_f(SVG_S * ro)} {_f(SVG_S * ro)} 0 {large} {so} {_pt(o1)}"
            f"A{_f(SVG_S * h)} {_f(SVG_S * h)} 0 0 {s_end} {_pt(i1)}"
            f"A{_f(SVG_S * ri)} {_f(SVG_S * ri)} 0 {large} {si} {_pt(i0)}"
            f"A{_f(SVG_S * h)} {_f(SVG_S * h)} 0 0 {s_start} {_pt(o0)}Z")


def svg_needle(r0=0.09):
    """Tapered needle with a round tip; starts just outside the hub dot so a punched-out dot stays clean."""
    a = math.radians(NEEDLE_ANGLE)
    d, n = (math.cos(a), math.sin(a)), (-math.sin(a), math.cos(a))
    w0 = NEEDLE_BASE - (NEEDLE_BASE - NEEDLE_TIP) * r0 / NEEDLE_L
    pts = lambda r, w, s: _p(d[0] * r + s * n[0] * w, d[1] * r + s * n[1] * w)
    bl, br = pts(r0, w0, 1), pts(r0, w0, -1)
    tl, tr = pts(NEEDLE_L, NEEDLE_TIP, 1), pts(NEEDLE_L, NEEDLE_TIP, -1)
    tip_r = _f(SVG_S * NEEDLE_TIP)
    s = _sweep_toward(tl, _p(d[0] * NEEDLE_L, d[1] * NEEDLE_L), (d[0], -d[1]))
    return f"M{_pt(bl)}L{_pt(tl)}A{tip_r} {tip_r} 0 0 {s} {_pt(tr)}L{_pt(br)}Z"


def svg_cross():
    """D-pad outline: 8 rounded outer corners, 4 sharp inner corners."""
    R, H, rr = HUB_ARM, HUB_HALF, HUB_ROUND
    verts = [(H, -R), (H, -H), (R, -H), (R, H), (H, H), (H, R), (-H, R), (-H, H), (-R, H), (-R, -H), (-H, -H),
             (-H, -R)]
    n = len(verts)
    out = []
    for i, v in enumerate(verts):
        pv, nv = verts[i - 1], verts[(i + 1) % n]
        din = (v[0] - pv[0], v[1] - pv[1])
        dout = (nv[0] - v[0], nv[1] - v[1])
        convex = din[0] * dout[1] - din[1] * dout[0] > 0  # CCW polygon in maths coordinates
        if not convex:
            out.append(("L", _p(*v)))
            continue
        li, lo = math.hypot(*din), math.hypot(*dout)
        a = _p(v[0] - din[0] / li * rr, v[1] - din[1] / li * rr)
        b = _p(v[0] + dout[0] / lo * rr, v[1] + dout[1] / lo * rr)
        out.append(("L", a))
        out.append(("A", b))
    d = ""
    for k, (cmd, p) in enumerate(out):
        if k == 0:
            d += f"M{_pt(p)}"
        elif cmd == "L":
            d += f"L{_pt(p)}"
        else:  # CCW in maths = counter-clockwise on screen = sweep 0
            d += f"A{_f(SVG_S * rr)} {_f(SVG_S * rr)} 0 0 0 {_pt(p)}"
    return d + "Z"


def svg_circle_path(r, reverse=False):
    cx, cy = _p(0, 0)
    rr = _f(SVG_S * r)
    s = 1 if reverse else 0
    return (f"M{_f(cx + SVG_S * r)} {_f(cy)}A{rr} {rr} 0 1 {s} {_f(cx - SVG_S * r)} {_f(cy)}"
            f"A{rr} {rr} 0 1 {s} {_f(cx + SVG_S * r)} {_f(cy)}Z")


SVG_HEAD = '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64">\n'
FLAT = {"lit": "#ff7a2f", "track": "#4a5163", "needle": "#f2f3f6", "dot": "#ff7a2f"}


def write_svgs():
    lit = svg_arc_band(GAUGE_START, NEEDLE_ANGLE + SVG_GAP)
    track = svg_arc_band(NEEDLE_ANGLE - SVG_GAP, GAUGE_END)
    needle, cross = svg_needle(), svg_cross()
    mono = (SVG_HEAD
            + "  <title>SpecTuner</title>\n"
            + f'  <path fill="currentColor" d="{lit}"/>\n'
            + f'  <path fill="currentColor" fill-opacity="0.35" d="{track}"/>\n'
            + f'  <path fill="currentColor" d="{needle}"/>\n'
            + f'  <path fill="currentColor" fill-rule="evenodd" d="{cross}{svg_circle_path(HUB_DOT_R)}"/>\n'
            + "</svg>\n")
    color = (SVG_HEAD
             + "  <title>SpecTuner</title>\n"
             + f'  <path fill="{FLAT["lit"]}" d="{lit}"/>\n'
             + f'  <path fill="{FLAT["track"]}" d="{track}"/>\n'
             + f'  <path fill="{FLAT["needle"]}" d="{needle}"/>\n'
             + f'  <path fill="{FLAT["needle"]}" d="{cross}"/>\n'
             + f'  <path fill="{FLAT["dot"]}" d="{svg_circle_path(HUB_DOT_R)}"/>\n'
             + "</svg>\n")
    with open(os.path.join(HERE, "logo-mark.svg"), "w", newline="\n") as f:
        f.write(mono)
    with open(os.path.join(HERE, "logo-mark-color.svg"), "w", newline="\n") as f:
        f.write(color)


def svg_bounds():
    """Bounds of the mark in viewBox units, from the geometry (used to place the imported SVG)."""
    xs, ys = [], []
    for a0, a1 in ((GAUGE_START, NEEDLE_ANGLE + SVG_GAP), (NEEDLE_ANGLE - SVG_GAP, GAUGE_END)):
        lo, hi = sorted((a0, a1))
        for k in range(721):
            a = lo + (hi - lo) * k / 720
            for rr in (GAUGE_R + GAUGE_W / 2,):
                x, y = _polar(rr, a)
                xs.append(x)
                ys.append(y)
        for a in (a0, a1):
            cx, cy = _polar(GAUGE_R, a)
            for k in range(72):
                t = 2 * math.pi * k / 72
                xs.append(cx + SVG_S * GAUGE_W / 2 * math.cos(t))
                ys.append(cy + SVG_S * GAUGE_W / 2 * math.sin(t))
    return min(xs), max(xs), min(ys), max(ys)


def render_svg(svg_path, size, supersample=8, ink=None):
    """Render an SVG through Blender's own importer (fills only), flat colours, transparent background.
    With ink (hex), every path is drawn in that colour, as the UI does with currentColor; the importer
    ignores fill-opacity, so the second path (the track) gets its 35% opacity here."""
    from mathutils import Vector
    reset_scene()
    scene = bpy.context.scene
    before = set(bpy.data.objects)
    bpy.ops.import_curve.svg(filepath=svg_path)
    objs = sorted((o for o in bpy.data.objects if o not in before and o.type == "CURVE"), key=lambda o: o.name)
    for i, o in enumerate(objs):  # document order, later paths on top
        o.location.z += i * 1e-4
    xs, ys = [], []
    for o in objs:
        for v in o.bound_box:
            w = o.matrix_world @ Vector(v)
            xs.append(w.x)
            ys.append(w.y)
    # map viewBox units to scene units from the known bounds of the mark
    bx0, bx1, by0, by1 = svg_bounds()
    scale = (max(xs) - min(xs)) / (bx1 - bx0)
    left = min(xs) - bx0 * scale
    top = max(ys) + by0 * scale  # importer flips y
    side = 64 * scale
    cam_data = bpy.data.cameras.new("Cam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = side
    cam = link(bpy.data.objects.new("Cam", cam_data))
    cam.location = (left + side / 2, top - side / 2, 1)
    scene.camera = cam
    for oi, o in enumerate(objs):
        o.data.resolution_u = 64
        if not o.data.materials:
            o.data.materials.append(None)
        for i, m in enumerate(o.data.materials):
            col = tuple(m.diffuse_color) if m else (0.5, 0.5, 0.5, 1)
            if ink:
                col = hex_rgba(ink)
            nm = bpy.data.materials.new(f"flat{o.name}{i}")
            nm.use_nodes = True
            nt = nm.node_tree
            for n in list(nt.nodes):
                if n.type != "OUTPUT_MATERIAL":
                    nt.nodes.remove(n)
            em = nt.nodes.new("ShaderNodeEmission")
            em.inputs["Color"].default_value = col
            out_socket = em.outputs[0]
            if ink and oi == 1:
                tr = nt.nodes.new("ShaderNodeBsdfTransparent")
                mix = nt.nodes.new("ShaderNodeMixShader")
                mix.inputs["Fac"].default_value = 0.35
                nt.links.new(tr.outputs[0], mix.inputs[1])
                nt.links.new(em.outputs[0], mix.inputs[2])
                out_socket = mix.outputs[0]
            nt.links.new(out_socket, nt.nodes["Material Output"].inputs["Surface"])
            o.data.materials[i] = nm
    scene.cycles.samples = 4
    scene.cycles.use_denoising = False
    arr = render_array(scene, size * supersample, 4)
    return box_down(arr, size)


# ---------------------------------------------------------------- contact sheet
def over(dst, src, x, y):
    h, w = src.shape[:2]
    a = src[..., 3:4]
    region = dst[y:y + h, x:x + w, :3]
    dst[y:y + h, x:x + w, :3] = src[..., :3] * a + region * (1 - a)


def hex_srgb(hex_color):
    return [int(hex_color.lstrip("#")[i:i + 2], 16) / 255 for i in (0, 2, 4)]


def bg_fill(h, w, hex_color):
    out = np.ones((h, w, 4), dtype=np.float32)
    out[..., :3] = hex_srgb(hex_color)
    return out


def nearest_up(arr, f):
    return arr.repeat(f, axis=0).repeat(f, axis=1)


def contact_sheet(icons, flat_big, mono, path):
    """Dark and light bands: icon at 256, 64, 32, 16; 32 and 16 enlarged 4x (nearest);
    then the flat mark: colour at 128, tinted mono at 128, 48 and 16."""
    pad = 32
    band_h = 256 + 2 * pad
    sizes = [256, 64, 32, 16, 128, 64, 128, 128, 48, 16]
    total_w = pad + sum(s + pad for s in sizes)
    sheet = np.ones((band_h * 2, total_w, 4), dtype=np.float32)
    for bi, (bg, ink) in enumerate((("#16161a", "#e8e8ec"), ("#f2f2f4", "#16161a"))):
        band = bg_fill(band_h, total_w, bg)
        items = [icons[256], icons[64], icons[32], icons[16], nearest_up(icons[32], 4), nearest_up(icons[16], 4),
                 flat_big, mono[(ink, 128)], mono[(ink, 48)], mono[(ink, 16)]]
        x = pad
        for it in items:
            over(band, it, x, (band_h - it.shape[0]) // 2)
            x += it.shape[1] + pad
        sheet[bi * band_h:(bi + 1) * band_h] = band
    save_png(sheet, path)


# ---------------------------------------------------------------- main
SIZES = [1024, 512, 256, 128, 64, 48, 32, 24, 16]
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
SMALL_MAX = 32  # sizes at or below this use the simplified variant


def save_blend(o, name):
    """Optional: keep the scene for inspection, only where --blend-dir points (never next to the script)."""
    if o["blend-dir"]:
        folder = os.path.abspath(o["blend-dir"])
        os.makedirs(folder, exist_ok=True)
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder, name), copy=True)


def main():
    o = parse_args()
    try:
        run(o)
    finally:
        if os.path.exists(TMP_RENDER):
            os.remove(TMP_RENDER)


def run(o):
    if not o["all"]:
        scene = build(o["concept"], o["variant"])
        arr = render_size(scene, o["size"], o["samples"])
        save_png(arr, os.path.abspath(o["out"]))
        return

    icons = {}
    scene = build(o["concept"], "small")
    save_blend(o, "icon-small.blend")
    for s in SIZES:
        if s <= SMALL_MAX:
            icons[s] = render_size(scene, s, o["samples"])
    scene = build(o["concept"], "full")
    for s in SIZES:
        if s > SMALL_MAX:
            icons[s] = render_size(scene, s, o["samples"])
    scene.render.resolution_x = scene.render.resolution_y = 1024
    scene.render.filepath = "//icon-1024.png"
    save_blend(o, "icon.blend")
    for s in SIZES:
        save_png(icons[s], os.path.join(HERE, f"icon-{s}.png"))

    # Windows icon: classic 32-bit DIB entries up to 64 px, PNG-compressed entries for 128 and 256 px
    entries = []
    for s in ICO_SIZES:
        data = png_bytes(os.path.join(HERE, f"icon-{s}.png")) if s >= 128 else dib_bytes(icons[s])
        entries.append((s, data))
    write_ico(os.path.join(HERE, "icon.ico"), entries)

    write_svgs()
    svg = os.path.join(HERE, "logo-mark-color.svg")
    flat_big = render_svg(svg, 128)
    mono = {}
    for ink in ("#e8e8ec", "#16161a"):
        for s in (128, 48, 16):
            mono[(ink, s)] = render_svg(os.path.join(HERE, "logo-mark.svg"), s, supersample=8 if s > 16 else 16, ink=ink)
    contact_sheet(icons, flat_big, mono, os.path.join(HERE, "contact-sheet.png"))
    print("SPECTUNER_ICON_DONE")


if __name__ == "__main__":
    main()
