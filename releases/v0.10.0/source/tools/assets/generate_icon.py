# -*- coding: utf-8 -*-
"""
CloudFlow 应用图标生成脚本
设计：Azure 蓝渐变圆角底 + 白色云朵 + 底部流动弧线（CloudFlow）
输出：src/CloudFlow.App/Assets/app.ico（16/24/32/48/64/128/256）

用法：python tools/assets/generate_icon.py
"""
import math
import os

from PIL import Image, ImageDraw

MASTER = 2048          # 超采样主画布，缩小得到抗锯齿
OUT = 512              # 逻辑尺寸
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# 品牌色（与 UI 主题一致的 Azure 蓝）
GRAD_TOP = (58, 160, 240)      # #3AA0F0
GRAD_BOTTOM = (11, 92, 173)    # #0B5CAD
CLOUD_WHITE = (255, 255, 255, 255)
SWOOSH = (168, 212, 255, 230)  # #A8D4FF 流动弧线


def rounded_gradient_bg(size: int) -> Image.Image:
    """圆角矩形 + 垂直渐变背景。"""
    radius = int(size * 0.215)
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    # 渐变先画在整张画布上，再用圆角矩形 mask 裁切
    grad = Image.new("RGBA", (size, size))
    top, bottom = GRAD_TOP, GRAD_BOTTOM
    px = grad.load()
    for y in range(size):
        t = y / (size - 1)
        r = int(top[0] + (bottom[0] - top[0]) * t)
        g = int(top[1] + (bottom[1] - top[1]) * t)
        b = int(top[2] + (bottom[2] - top[2]) * t)
        for_row = (r, g, b, 255)
        d = ImageDraw.Draw(grad)
        d.line([(0, y), (size, y)], fill=for_row)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)
    return img


def draw_flow_swoosh(img: Image.Image, s: int):
    """云朵下方的流动弧线（CloudFlow 的 Flow）。"""
    d = ImageDraw.Draw(img)
    # 大圆弧的一段：圆心在画布上方外侧，弧线最低点从云底下方穿过
    cx, cy = s * 0.5, s * 0.10
    r = s * 0.66
    width = int(s * 0.042)
    # x ∈ [0.10s, 0.90s] 的一段弧
    half = s * 0.40
    start = math.degrees(math.atan2(s * 0.625 - cy, -half))
    end = math.degrees(math.atan2(s * 0.625 - cy, half))
    bbox = [cx - r, cy - r, cx + r, cy + r]
    d.arc(bbox, start=start, end=end, fill=SWOOSH, width=width)


def draw_cloud(img: Image.Image, s: int):
    """白色云朵：三个圆 + 底部圆角矩形（并集）。"""
    d = ImageDraw.Draw(img)
    u = s / 512.0
    # 底部主体
    d.rounded_rectangle([96 * u, 252 * u, 416 * u, 356 * u],
                        radius=52 * u, fill=CLOUD_WHITE)
    # 左圆 / 中圆 / 右圆
    d.ellipse([98 * u, 192 * u, 248 * u, 342 * u], fill=CLOUD_WHITE)     # 左
    d.ellipse([158 * u, 118 * u, 354 * u, 314 * u], fill=CLOUD_WHITE)    # 中（大）
    d.ellipse([276 * u, 196 * u, 420 * u, 340 * u], fill=CLOUD_WHITE)    # 右


def build_master() -> Image.Image:
    img = rounded_gradient_bg(MASTER)
    draw_flow_swoosh(img, MASTER)
    draw_cloud(img, MASTER)
    return img


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out_dir = os.path.join(root, "src", "CloudFlow.App", "Assets")
    os.makedirs(out_dir, exist_ok=True)

    master = build_master()

    # 预览图
    master.resize((OUT, OUT), Image.LANCZOS).save(
        os.path.join(out_dir, "app-icon-preview.png"))

    # ICO：各尺寸独立从主画布重采样，小尺寸更清晰
    base = master.resize((256, 256), Image.LANCZOS)
    frames = [master.resize((sz, sz), Image.LANCZOS) for sz in ICO_SIZES]
    base.save(os.path.join(out_dir, "app.ico"),
              format="ICO",
              sizes=[(sz, sz) for sz in ICO_SIZES],
              append_images=frames[1:])

    print("OK ->", os.path.join(out_dir, "app.ico"))
    for sz in ICO_SIZES:
        print(f"  {sz}x{sz}")


if __name__ == "__main__":
    main()
