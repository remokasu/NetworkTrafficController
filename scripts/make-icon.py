"""NetworkTrafficController のアプリアイコン（速度グラフ）を生成する。

使い方: python scripts/make-icon.py
出力:   assets/app.ico（16〜256px を 1 ファイルに格納。NetworkTrafficController.App が参照する）
必要:   Pillow（pip install "Pillow>=12"。12.1.0 で確認）

絵柄: 暗い角丸の背景に、軸と 2 本の折れ線。水色が送信、緑が受信で、画面のグラフと同じ色にしている。
小さいサイズ（32px 以下）は細部が潰れるため、線を太くした版を描く。
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT_DIR = Path(__file__).resolve().parent.parent / "assets"
SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 1024  # 描画用の大きさ。各サイズへは縮小して作る

BG_TOP = (38, 50, 58)
BG_BOTTOM = (16, 20, 24)
AXIS = (120, 134, 142)
OUT_LINE = (79, 195, 247)  # 送信（画面の OutBrush）
IN_LINE = (129, 199, 132)  # 受信（画面の InBrush）

OUT_POINTS = [(230, 640), (330, 520), (430, 580), (540, 380), (640, 450), (760, 260)]
IN_POINTS = [(230, 740), (330, 690), (430, 720), (540, 610), (640, 660), (760, 560)]


def draw(simple: bool) -> Image.Image:
    img = Image.new("RGBA", (SS, SS), (0, 0, 0, 0))

    # 背景: 縦グラデーションの角丸四角
    bg = Image.new("RGBA", (SS, SS))
    bd = ImageDraw.Draw(bg)
    for y in range(SS):
        t = y / (SS - 1)
        bd.line([(0, y), (SS, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOTTOM)) + (255,))
    mask = Image.new("L", (SS, SS), 0)
    ImageDraw.Draw(mask).rounded_rectangle([24, 24, SS - 24, SS - 24], radius=210, fill=255)
    img.paste(bg, (0, 0), mask)

    d = ImageDraw.Draw(img)
    d.line([(200, 230), (200, 800), (840, 800)], fill=AXIS, width=36 if simple else 26, joint="curve")

    width = 70 if simple else 52
    for points, color in ((IN_POINTS, IN_LINE), (OUT_POINTS, OUT_LINE)):
        d.line(points, fill=color, width=width, joint="curve")
        # 線の端を丸め、右端は最新の値として点を打つ
        x, y = points[0]
        r = width / 2
        d.ellipse([x - r, y - r, x + r, y + r], fill=color)
        x, y = points[-1]
        r = width * 0.9
        d.ellipse([x - r, y - r, x + r, y + r], fill=color)

    return img


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    full, simple = draw(False), draw(True)
    frames = [(simple if s <= 32 else full).resize((s, s), Image.LANCZOS) for s in SIZES]
    frames[-1].save(OUT_DIR / "app.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    print(f"wrote {OUT_DIR / 'app.ico'}")


if __name__ == "__main__":
    main()
