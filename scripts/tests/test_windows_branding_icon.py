"""Regression coverage for sidebar contrast and Windows icon resource compatibility."""
from __future__ import annotations

import struct
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "windows" / "src" / "Evict.App"
KEY = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"
PNG = b"\x89PNG\r\n\x1a\n"


def icon_frames():
    data = (APP / "Assets" / "app.ico").read_bytes()
    reserved, kind, count = struct.unpack_from("<HHH", data)
    if (reserved, kind, count) != (0, 1, 9):
        raise ValueError("Expected one icon with nine frames.")
    frames = []
    end = 6 + 16 * count
    for index in range(count):
        w, h, colors, reserved, planes, depth, length, offset = struct.unpack_from("<BBBBHHII", data, 6 + index * 16)
        if offset != end or length <= 0 or offset + length > len(data):
            raise ValueError("ICO frame is truncated, overlapping, or out of bounds.")
        frames.append((w or 256, h or 256, colors, reserved, planes, depth, data[offset:offset + length]))
        end = offset + length
    if end != len(data):
        raise ValueError("Unexpected trailing icon data.")
    return frames


class WindowsBrandingIconTests(unittest.TestCase):
    def test_wordmark_uses_dedicated_dynamic_sidebar_brush(self):
        window = ET.fromstring((APP / "MainWindow.xaml").read_text(encoding="utf-8"))
        brands = [n for n in window.iter() if n.tag.endswith("TextBlock") and n.get("Text") == "Evict"]
        self.assertEqual(len(brands), 1)
        self.assertEqual(brands[0].get("Foreground"), "{DynamicResource Brush.Sidebar.BrandText}")

    def test_wordmark_contrast_in_light_and_dark(self):
        def luminance(color):
            self.assertRegex(color, r"^#[0-9A-Fa-f]{6}$")
            rgb = [int(color[i:i + 2], 16) / 255 for i in (1, 3, 5)]
            linear = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in rgb]
            return sum(c * w for c, w in zip(linear, (0.2126, 0.7152, 0.0722)))

        keys = []
        for theme in ("Light", "Dark"):
            with self.subTest(theme=theme):
                root = ET.fromstring((APP / "Themes" / f"{theme}.xaml").read_text(encoding="utf-8"))
                brushes = {n.get(KEY): n.get("Color") for n in root if n.tag.endswith("SolidColorBrush")}
                keys.append(set(brushes))
                foreground = luminance(brushes["Brush.Sidebar.BrandText"])
                background = luminance(brushes["Brush.Sidebar.Background"])
                ratio = (max(foreground, background) + 0.05) / (min(foreground, background) + 0.05)
                self.assertGreaterEqual(ratio, 4.5)
        self.assertEqual(*keys)

    def test_icon_frames_have_valid_sizes_and_metadata(self):
        frames = icon_frames()
        self.assertEqual([f[0] for f in frames], [16, 20, 24, 32, 40, 48, 64, 128, 256])
        for w, h, colors, reserved, planes, depth, _ in frames:
            with self.subTest(size=w):
                self.assertEqual(w, h)
                self.assertEqual((colors, reserved, planes, depth), (0, 0, 1, 32))

    def test_small_bitmap_frames_preserve_valid_alpha_masks(self):
        for w, h, _, _, _, _, payload in icon_frames():
            if w == 256:
                continue
            with self.subTest(size=w):
                header = struct.unpack_from("<IiiHHIIiiII", payload)
                self.assertEqual(header[:7], (40, w, h * 2, 1, 32, 0, w * h * 4))
                stride = ((w + 31) // 32) * 4
                self.assertEqual(len(payload), 40 + w * h * 4 + stride * h)
                mask = payload[40 + w * h * 4:]
                alphas = []
                for y in range(h):
                    for x in range(stride * 8):
                        bit = bool(mask[y * stride + x // 8] & (0x80 >> (x % 8)))
                        if x < w:
                            alpha = payload[40 + (y * w + x) * 4 + 3]
                            alphas.append(alpha)
                            self.assertEqual(bit, alpha == 0)
                        else:
                            self.assertFalse(bit)
                self.assertTrue(any(alphas), "Icon must contain visible artwork.")

    def test_large_png_matches_existing_brand_artwork(self):
        payload = icon_frames()[-1][-1]
        self.assertTrue(payload.startswith(PNG))
        self.assertEqual(payload, (APP / "Assets" / "app-256.png").read_bytes())


if __name__ == "__main__":
    unittest.main()
