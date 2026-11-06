# -*- coding: utf-8 -*-
"""
CloudFlow 应用图标生成脚本（立体版）
设计：Azure 蓝对角渐变圆角底座（顶部高光 + 内描边 + 投影）
      + 立体云朵（上白下淡蓝渐变、投影、顶部高光）
      + 云下方的流动弧线（CloudFlow 的 Flow，带投影）
输出：src/CloudFlow.App/Assets/app.ico（16/24/32/48/64/128/256）与 app-icon-preview.png

用法：python tools/assets/generate_icon.py
依赖：Pillow、numpy
"""
import math
import os

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter

S = 2048                 # 超采样主画布，缩小得到抗锯齿
OUT = 512                # 预览图尺寸
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# 品牌色（与 UI 主题一致的 Azure 蓝）
TILE_LIGHT = (64, 178, 255)    # 左上
TILE_DARK = (0, 58, 170)       # 右下
CLOUD_TOP = (255, 255, 255)
CLOUD_BOTTOM = (150, 202, 252)
SHADOW_BLUE = (4, 36, 104)


def lerp_gradient(size, c0, c1, axis="v", angle=None) -> Image.Image:
    """生成 RGB 渐变图。axis='v' 为纵向；angle 给出时按角度（度，0=向右，90=向下）。"""
    h = w = size
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    if angle is None:
        t = ys / (h - 1) if axis == "v" else xs / (w - 1)
    else:
        a = math.radians(angle)
        proj = xs * math.cos(a) + ys * math.sin(a)
        t = (proj - proj.min()) / (proj.max() - proj.min())
    t = t[..., None]
    c0 = np.array(c0, np.float32)
    c1 = np.array(c1, np.float32)
    arr = (c0 + (c1 - c0) * t).clip(0, 255).astype(np.uint8)
    return Image.fromarray(arr, "RGB")


def alpha_vertical(size, a0, a1) -> Image.Image:
    """纵向 alpha 渐变的 L 图。"""
    col = np.linspace(a0, a1, size, dtype=np.float32)[:, None]
    arr = np.repeat(col, size, axis=1).clip(0, 255).astype(np.uint8)
    return Image.fromarray(arr, "L")


def rounded_mask(box, radius) -> Image.Image:
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m


def cloud_mask(cx, cy, scale) -> Image.Image:
    """云朵形状：三个圆 + 底部圆角矩形的并集。原始设计坐标基于 512 画布，中心约 (258, 236)。"""
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    u = scale / 512.0
    ox, oy = cx - 258 * u, cy - 236 * u

    def box(x0, y0, x1, y1):
        return [ox + x0 * u, oy + y0 * u, ox + x1 * u, oy + y1 * u]

    d.rounded_rectangle(box(96, 252, 416, 356), radius=52 * u, fill=255)
    d.ellipse(box(98, 192, 248, 342), fill=255)
    d.ellipse(box(158, 118, 354, 314), fill=255)
    d.ellipse(box(276, 196, 420, 340), fill=255)
    return m


def paste_solid(canvas, color, mask, alpha=255):
    """用纯色按 mask 叠加（alpha 为整体不透明度）。"""
    m = mask if alpha == 255 else mask.point(lambda v: v * alpha // 255)
    canvas.alpha_composite(Image.merge("RGBA", (*Image.new("RGB", (S, S), color).split(), m)))


def paste_gradient(canvas, c0, c1, mask, angle=None):
    src = lerp_gradient(S, c0, c1, axis="v", angle=angle)
    canvas.alpha_composite(Image.merge("RGBA", (*src.split(), mask)))


def extrude(mask, dy, steps=40) -> Image.Image:
    """把形状沿 +y 方向拉出厚度：各偏移副本取并集，得到实心侧面。"""
    out = mask
    for i in range(1, steps + 1):
        out = ImageChops.lighter(out, ImageChops.offset(mask, 0, int(dy * i / steps)))
    return out


def ribbon_mask(x0, x1, y_mid, amp, width) -> Image.Image:
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    r = width / 2
    n = 1600
    for i in range(n + 1):
        t = i / n
        x = x0 + (x1 - x0) * t
        y = y_mid + amp * math.sin((t - 0.5) * math.pi * 1.6)
        d.ellipse([x - r, y - r, x + r, y + r], fill=255)
    return m


def _box_blur_axis(a, r, axis):
    """沿某轴的盒式模糊（累加和实现，O(n)）。"""
    a = np.moveaxis(a, axis, 0)
    pad = np.concatenate([np.repeat(a[:1], r + 1, axis=0), a, np.repeat(a[-1:], r, axis=0)], axis=0)
    c = np.cumsum(pad, axis=0, dtype=np.float64)
    out = (c[2 * r + 1:] - c[:-2 * r - 1]) / (2 * r + 1)
    return np.moveaxis(out.astype(np.float32), 0, axis)


def gauss_blur(a, sigma):
    """用 3 次盒式模糊近似高斯模糊（不引入 scipy 依赖，也避开 PIL 对浮点图的限制）。"""
    r = max(1, int(round(sigma * 1.2)))
    for _ in range(3):
        a = _box_blur_axis(_box_blur_axis(a, r, 0), r, 1)
    return a


def bezier(p0, p1, p2, p3, n=300):
    pts = []
    for i in range(n + 1):
        t = i / n
        u = 1 - t
        pts.append((u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0],
                    u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1]))
    return pts


def flow_path():
    """CloudFlow 的字母 C：一条开口向右的粗带，下端顺势拖出一道向右的流线（Flow）。
    返回 [(x, y, 宽度系数)]，宽度系数在两端收窄。"""
    cx, cy, R = S * 0.485, S * 0.455, S * 0.255
    pts = []
    a0, a1 = math.radians(-46), math.radians(-260)             # 从右上起，逆时针绕到底部偏左
    n = 700
    for i in range(n + 1):
        th = a0 + (a1 - a0) * i / n
        pts.append((cx + R * math.cos(th), cy + R * math.sin(th)))
    ex, ey = pts[-1]
    tx, ty = math.sin(a1), -math.cos(a1)                      # 终点切线方向（运动方向）
    tail = bezier((ex, ey), (ex + tx * S * 0.17, ey + ty * S * 0.17),
                  (S * 0.66, S * 0.745), (S * 0.835, S * 0.655))
    pts += tail[1:]
    total = len(pts)
    out = []
    for i, (x, y) in enumerate(pts):
        t = i / (total - 1)
        k = 1.0
        if t < 0.06:
            k = 0.55 + 0.45 * (t / 0.06)                          # 上端收窄
        if t > 0.80:
            k = 1.0 - 0.60 * ((t - 0.80) / 0.20)                  # 流线尾端收窄
        out.append((x, y, k))
    return out


def stamp(canvas_draw, pts, width, color, dx=0.0, dy=0.0, taper=True):
    for (x, y, k) in pts:
        r = width * (k if taper else 1.0) / 2
        canvas_draw.ellipse([x + dx - r, y + dy - r, x + dx + r, y + dy + r], fill=color)


THEMES = {
    # 深色系：深海军蓝底盘（程序图标 / 深色主题）
    "dark": dict(
        side=(0, 8, 40), tile=((30, 70, 160), (8, 26, 84)), rim=(120, 170, 255),
        plate=((10, 30, 92), (5, 18, 66)), plate_rim_top=(2, 10, 46), plate_rim_bottom=(60, 110, 220),
        cast=((0, 6, 36), 190), glow=((24, 100, 255), 60), thick=((10, 58, 190), (2, 20, 100)),
        ribbon=((4, 34, 170), (22, 110, 255), (120, 200, 255)),
    ),
    # 浅色系：近白淡蓝底盘（浅色主题），缎带保持品牌蓝，投影改淡
    "light": dict(
        side=(150, 176, 218), tile=((255, 255, 255), (218, 231, 249)), rim=(255, 255, 255),
        plate=((240, 246, 255), (224, 236, 252)), plate_rim_top=(176, 198, 232), plate_rim_bottom=(255, 255, 255),
        cast=((40, 70, 140), 120), glow=((60, 130, 255), 45), thick=((16, 70, 200), (6, 36, 130)),
        ribbon=((2, 40, 176), (20, 104, 252), (118, 196, 255)),
    ),
}


def build_master(detail: bool = True, theme: str = "dark") -> Image.Image:
    """参考 AgentDeck 的图标语言：深海军蓝圆角底盘 + 立体发光的蓝色缎带字母。
    这里的字母是 CloudFlow 的 C，下端拖出流线。缎带用同心描边层叠出圆润的立体光影。"""
    T = THEMES[theme]
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # ---- 底盘：深蓝渐变 + 厚度 + 内凹面板 ----
    pad = int(S * 0.05)
    box = [pad, pad, S - pad - 1, S - pad - int(S * 0.035)]
    radius = int((box[2] - box[0]) * 0.23)
    face = rounded_mask(box, radius)
    side = extrude(face, int(S * 0.035))
    paste_solid(canvas, T["side"], side)
    paste_gradient(canvas, *T["tile"], face, angle=80)
    # 顶沿亮边 / 底沿暗边
    inset = int(S * 0.009)
    inner = rounded_mask([box[0] + inset, box[1] + inset, box[2] - inset, box[3] - inset], radius - inset)
    rim = ImageChops.subtract(face, inner)
    paste_solid(canvas, T["rim"], ImageChops.multiply(rim, alpha_vertical(S, 255, 0)))
    # 内凹面板
    pin = int(S * 0.062)
    pbox = [box[0] + pin, box[1] + pin, box[2] - pin, box[3] - pin]
    plate = rounded_mask(pbox, int(radius * 0.72))
    paste_gradient(canvas, *T["plate"], plate, angle=80)
    pin2 = int(S * 0.006)
    plate_in = rounded_mask([pbox[0] + pin2, pbox[1] + pin2, pbox[2] - pin2, pbox[3] - pin2], int(radius * 0.72) - pin2)
    plate_rim = ImageChops.subtract(plate, plate_in)
    paste_solid(canvas, T["plate_rim_top"], ImageChops.multiply(plate_rim, alpha_vertical(S, 255, 0)))
    paste_solid(canvas, T["plate_rim_bottom"], ImageChops.multiply(plate_rim, alpha_vertical(S, 0, 255)), 200)

    # ---- 缎带 C：高度场 + 法线光照（漫反射 + 高光 + 反射光），得到真正的立体浮雕感 ----
    path = flow_path()
    W = S * 0.150
    mask_img = Image.new("L", (S, S), 0)
    stamp(ImageDraw.Draw(mask_img), path, W, 255)
    mask_img = mask_img.filter(ImageFilter.GaussianBlur(S * 0.0008))

    # 缎带在底盘上的投影（向右下偏移，较深较实）
    cast = ImageChops.offset(mask_img, int(S * 0.026), int(S * 0.042))
    paste_solid(canvas, T["cast"][0], cast.filter(ImageFilter.GaussianBlur(S * 0.016)), T["cast"][1])
    # 背后的蓝色辉光（很淡）
    paste_solid(canvas, T["glow"][0], mask_img.filter(ImageFilter.GaussianBlur(S * 0.035)), T["glow"][1])
    # 缎带厚度：整体向右下挤出一段，深蓝实心侧面
    thick = extrude(ImageChops.offset(mask_img, int(S * 0.010), int(S * 0.020)), S * 0.012, steps=24)
    paste_gradient(canvas, *T["thick"], thick, angle=60)

    # 高度场：同心收窄的 56 层叠加 → 截面是中间高、两侧圆滑下降的"圆角带"
    N = 56
    height = np.zeros((S, S), np.float32)
    for i in range(N):
        ratio = 1.0 - 0.94 * (i / N)
        m = Image.new("L", (S, S), 0)
        stamp(ImageDraw.Draw(m), path, W * ratio, 255)
        height += np.asarray(m, np.float32) / 255.0
    height /= N
    # 截面再圆滑一点（圆角 = 1 - (1-h)^2）并平滑，消除层间台阶
    height = 1.0 - (1.0 - height) ** 1.6
    height = gauss_blur(height, S * 0.0035)

    gy, gx = np.gradient(height * (S * 0.085))
    nz = np.ones_like(gx)
    inv = 1.0 / np.sqrt(gx * gx + gy * gy + nz * nz)
    nx, ny, nz = -gx * inv, -gy * inv, nz * inv

    def unit(v):
        v = np.array(v, np.float32)
        return v / np.linalg.norm(v)

    light = unit((-0.55, -0.65, 0.55))            # 主光：左上前方
    diffuse = np.clip(nx * light[0] + ny * light[1] + nz * light[2], 0, 1)
    half = unit(light + np.array([0, 0, 1], np.float32))
    spec = np.clip(nx * half[0] + ny * half[1] + nz * half[2], 0, 1) ** 70
    bounce_dir = unit((0.6, 0.7, 0.25))           # 右下反射光：给下沿一圈亮蓝，避免暗部发闷
    bounce = np.clip(nx * bounce_dir[0] + ny * bounce_dir[1] + nz * bounce_dir[2], 0, 1) ** 2

    dark, mid, bright = (np.array(c, np.float32) for c in T["ribbon"])
    d = diffuse[..., None]
    col = np.where(d < 0.55, dark + (mid - dark) * (d / 0.55), mid + (bright - mid) * ((d - 0.55) / 0.45))
    col = col + np.array((30, 120, 255), np.float32) * (bounce[..., None] * 0.35)
    col = col + 255.0 * (spec[..., None] * 0.85)
    rgb = Image.fromarray(col.clip(0, 255).astype(np.uint8), "RGB")
    canvas.alpha_composite(Image.merge("RGBA", (*rgb.split(), mask_img)))
    return canvas


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out_dir = os.path.join(root, "src", "CloudFlow.App", "Assets")
    os.makedirs(out_dir, exist_ok=True)

    master = build_master()                                   # 深色系：程序 / 任务栏 / 快捷方式图标
    master_small = build_master(detail=False)

    def down(sz, src=None):
        """缩小并按尺寸做适度锐化，避免小尺寸发虚。"""
        img = (src or (master_small if sz <= 48 else master)).resize((sz, sz), Image.LANCZOS)
        pct = 60 if sz <= 48 else 0
        return img.filter(ImageFilter.UnsharpMask(radius=0.6, percent=pct, threshold=1)) if pct else img

    down(OUT).save(os.path.join(out_dir, "app-icon-preview.png"))
    # 浅色系：窗口内 Logo 在浅色主题下使用（深色主题用上面的深色系）
    build_master(theme="light").resize((OUT, OUT), Image.LANCZOS).save(os.path.join(out_dir, "app-icon-preview-light.png"))

    # ICO：各尺寸独立从主画布重采样，小尺寸更清晰
    base = down(256)
    frames = [down(sz) for sz in ICO_SIZES]
    base.save(os.path.join(out_dir, "app.ico"), format="ICO",
              sizes=[(sz, sz) for sz in ICO_SIZES], append_images=frames[1:])

    print("OK ->", os.path.join(out_dir, "app.ico"))
    for sz in ICO_SIZES:
        print(f"  {sz}x{sz}")


if __name__ == "__main__":
    main()
