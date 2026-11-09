"""Builds docs/walkthrough.gif from the screenshots in docs/screens.

The app renders its own UI to PNG (RouteScope.exe --render), so every frame here is a real
screenshot rather than a mock-up. Each frame gets a caption bar and a progress hint.
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
SCREENS = ROOT / "docs" / "screens"
OUT = ROOT / "docs" / "walkthrough.gif"

WIDTH = 1000
CAPTION_H = 74
BG = (11, 13, 20)
ACCENT = (124, 108, 246)
TEXT = (236, 238, 245)
MUTED = (150, 156, 176)

FRAMES = [
    ("01-home.png", "RouteScope — only the apps you pick use the proxy",
     "Everything else on the PC keeps its normal connection"),
    ("02-targets.png", "1. Choose what to route",
     "Drop a folder or an .exe. Launchers expand to their child processes"),
    ("03-serversb.png", "2. Choose where traffic goes",
     "A local proxy, a pasted link, a WireGuard or OpenVPN config"),
    ("03-servers.png", "Servers you already have are found automatically",
     "Scan for local proxies speaks a real SOCKS5/HTTP handshake"),
    ("06-settingsb.png", "3. Engines keep themselves current",
     "sing-box and Xray-core, versioned side by side"),
    ("04-live.png", "4. Watch it work",
     "Live connections, the rule that matched, and per-app counts"),
    ("05-logs.png", "The Activity log answers most questions",
     "Every change RouteScope makes is written here"),
    ("06b-support.png", "Free and open source",
     "No telemetry, no account, no paid tier"),
]


def load_font(size: int, bold: bool = False):
    candidates = [
        "C:/Windows/Fonts/segoeuib.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf",
        "C:/Windows/Fonts/arialbd.ttf" if bold else "C:/Windows/Fonts/arial.ttf",
    ]
    for path in candidates:
        if Path(path).exists():
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def build_frame(name: str, title: str, subtitle: str) -> Image.Image:
    source = Image.open(SCREENS / name).convert("RGB")

    # Scale to the target width, keeping the aspect ratio.
    ratio = WIDTH / source.width
    body = source.resize((WIDTH, int(source.height * ratio)), Image.LANCZOS)

    canvas = Image.new("RGB", (WIDTH, body.height + CAPTION_H), BG)
    canvas.paste(body, (0, 0))

    draw = ImageDraw.Draw(canvas)
    top = body.height

    # Accent rule separating the screenshot from the caption.
    draw.rectangle([0, top, WIDTH, top + 2], fill=ACCENT)

    draw.text((22, top + 14), title, font=load_font(20, bold=True), fill=TEXT)
    draw.text((22, top + 43), subtitle, font=load_font(15), fill=MUTED)

    return canvas


def main() -> None:
    frames = [build_frame(*spec) for spec in FRAMES]

    # One frame per step, holding long enough to read the caption.
    durations = [2600] * len(frames)
    durations[-1] = 4200

    frames[0].save(
        OUT,
        save_all=True,
        append_images=frames[1:],
        duration=durations,
        loop=0,
        optimize=True,
        disposal=2,
    )

    size_kb = OUT.stat().st_size / 1024
    print(f"wrote {OUT}  ({len(frames)} frames, {size_kb:.0f} KB)")


if __name__ == "__main__":
    main()
