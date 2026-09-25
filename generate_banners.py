#!/usr/bin/env python3
"""
MirageDiary - Banner Generator
==============================
Generates high-resolution, ultra-crisp UI banners for Telegram and MAX bot interfaces.
Uses 2x supersampling (2400x1160 -> 1200x580 via LANCZOS) for sharp rendering on OLED and Retina displays.

Style Specification (as documented in PROJECT_MAP.md):
- Dimensions: 1200x580 (internal render at 2400x1160)
- Card: White background (#FFFFFF) with rounded corners and hairline border (#E2E8F0)
- Brand: "MirageDiary" in Inter SemiBold (#1E293B)
- Counter/Index: "01 — MENU", etc. in Inter Medium (#94A3B8)
- Header Title: Unbounded Wide Bold (#09090B)
- Subtitle: Segoe UI Regular / Inter Regular (#475569)
- Accent bar: Minimalist horizontal pill (#09090B)
"""

import argparse
import os
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
if hasattr(sys.stderr, "reconfigure"):
    try:
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

# Canonical canvas dimensions (2x for antialiased downsampling)
CANVAS_WIDTH = 2400
CANVAS_HEIGHT = 1160
OUTPUT_WIDTH = 1200
OUTPUT_HEIGHT = 580

# Color palette
COLOR_BG = (255, 255, 255)
COLOR_BORDER = (226, 232, 240)       # #E2E8F0
COLOR_BRAND = (30, 41, 59)           # #1E293B (Slate 800)
COLOR_COUNTER = (148, 163, 184)      # #94A3B8 (Slate 400)
COLOR_TITLE = (9, 9, 11)             # #09090B (Zinc 950)
COLOR_SUBTITLE = (71, 85, 105)       # #475569 (Slate 600)
COLOR_ACCENT_BAR = (9, 9, 11)        # #09090B

# Banner definitions: (filename, title, subtitle, counter, font_size)
BANNERS = [
    (
        "banner_menu.png",
        "ГЛАВНОЕ МЕНЮ",
        "Личный кабинет, журнал и быстрый доступ",
        "01 — MENU",
        180,
    ),
    (
        "banner_homework.png",
        "ДОМАШНЕЕ ЗАДАНИЕ",
        "Упражнения, параграфы и файлы к урокам",
        "02 — HOMEWORK",
        132,
    ),
    (
        "banner_schedule.png",
        "РАСПИСАНИЕ УРОКОВ",
        "Порядок предметов, кабинеты и звонки",
        "03 — SCHEDULE",
        132,
    ),
    (
        "banner_marks.png",
        "ОЦЕНКИ И БАЛЛЫ",
        "Успеваемость, средний балл и калькулятор",
        "04 — MARKS",
        152,
    ),
    (
        "banner_now.png",
        "РАСПИСАНИЕ ЗВОНКОВ",
        "МАОУ СОШ № 113 • Оставшееся время до звонка",
        "05 — BELLS",
        128,
    ),
    (
        "banner_premium.png",
        "MIRAGE PREMIUM",
        "Live-оценки по звонкам, умный будильник и кэш",
        "06 — PREMIUM",
        148,
    ),
    (
        "banner_settings.png",
        "НАСТРОЙКИ СЕРВИСА",
        "Управление напоминаниями, временем и звуками",
        "07 — SETTINGS",
        128,
    ),
    (
        "banner_alarm.png",
        "УМНЫЙ БУДИЛЬНИК",
        "Управление напоминаниями, временем и звуками",
        "08 — ALARM",
        128,
    ),
    (
        "banner_profile.png",
        "МОЙ ПРОФИЛЬ",
        "Данные ученика, школа, класс и статус сессии",
        "08 — PROFILE",
        156,
    ),
    (
        "banner_admin.png",
        "АДМИН-ПАНЕЛЬ",
        "Управление системой, завучами и статистикой",
        "09 — ADMIN",
        156,
    ),
]


def resolve_font_path(repo_root: Path, font_name: str, fallback_paths: list[str]) -> str:
    """Finds font file either in repository root or standard OS locations."""
    local_path = repo_root / font_name
    if local_path.is_file():
        return str(local_path)

    for fallback in fallback_paths:
        if os.path.isfile(fallback):
            return fallback

    return str(local_path)


def generate_banner(
    filename: str,
    title: str,
    subtitle: str,
    counter: str,
    font_size: int,
    out_dir: Path,
    inter_path: str,
    unbounded_path: str,
    sub_font_path: str,
) -> Path:
    """Renders a single banner with 2x supersampling and Lanczos downscale."""
    # 1. 2x Canvas
    img2 = Image.new("RGB", (CANVAS_WIDTH, CANVAS_HEIGHT), color=COLOR_BG)
    draw2 = ImageDraw.Draw(img2)

    # 2. Outer hairline border with rounded corners
    draw2.rounded_rectangle(
        [4, 4, CANVAS_WIDTH - 5, CANVAS_HEIGHT - 5],
        radius=44,
        outline=COLOR_BORDER,
        width=4,
    )

    # 3. Fonts
    brand_font = ImageFont.truetype(inter_path, 44)
    if hasattr(brand_font, "set_variation_by_name"):
        try:
            brand_font.set_variation_by_name("SemiBold")
        except Exception:
            pass

    num_font = ImageFont.truetype(inter_path, 38)
    if hasattr(num_font, "set_variation_by_name"):
        try:
            num_font.set_variation_by_name("Medium")
        except Exception:
            pass

    sub_font = ImageFont.truetype(sub_font_path, 56)
    title_font = ImageFont.truetype(unbounded_path, font_size)

    # 4. Brand label (top-left)
    draw2.text((150, 140), "MirageDiary", font=brand_font, fill=COLOR_BRAND)

    # 5. Section counter (top-right)
    draw2.text((CANVAS_WIDTH - 380, 140), counter, font=num_font, fill=COLOR_COUNTER)

    # 6. Main title in Unbounded
    draw2.text((145, 310), title, font=title_font, fill=COLOR_TITLE)

    # 7. Descriptive subtitle
    draw2.text((152, 605), subtitle, font=sub_font, fill=COLOR_SUBTITLE)

    # 8. Minimalist horizontal accent bar
    draw2.rounded_rectangle([152, 755, 420, 765], radius=5, fill=COLOR_ACCENT_BAR)

    # 9. Downsample with Lanczos to target dimensions (1200x580)
    final_img = img2.resize((OUTPUT_WIDTH, OUTPUT_HEIGHT), Image.Resampling.LANCZOS)

    # 10. Save optimized PNG
    out_dir.mkdir(parents=True, exist_ok=True)
    target_path = out_dir / filename
    final_img.save(target_path, "PNG", optimize=True)
    return target_path


def main():
    parser = argparse.ArgumentParser(description="Generate MirageDiary bot screen banners.")
    parser.add_argument(
        "--out-dir",
        type=str,
        default=None,
        help="Target directory for output PNG files (default: MirageDiary.Server/wwwroot/images/banners)",
    )
    parser.add_argument(
        "--banner",
        type=str,
        default=None,
        help="Generate only one specific banner by filename (e.g. banner_menu.png or test_banner.png)",
    )
    parser.add_argument(
        "--test",
        action="store_true",
        help="Generate a test banner with custom or sample text",
    )
    parser.add_argument(
        "--title",
        type=str,
        default="ТЕСТОВЫЙ ЭКРАН",
        help="Custom title for test banner (default: 'ТЕСТОВЫЙ ЭКРАН')",
    )
    parser.add_argument(
        "--subtitle",
        type=str,
        default="Проверка рендеринга, суперсэмплирования и типографики",
        help="Custom subtitle for test banner",
    )
    parser.add_argument(
        "--counter",
        type=str,
        default="00 — TEST",
        help="Custom top-right counter for test banner (default: '00 — TEST')",
    )
    parser.add_argument(
        "--font-size",
        type=int,
        default=160,
        help="Custom font size for test banner title (default: 160)",
    )
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent

    if args.out_dir:
        out_dir = Path(args.out_dir).resolve()
    else:
        out_dir = repo_root / "MirageDiary.Server" / "wwwroot" / "images" / "banners"

    inter_path = resolve_font_path(repo_root, "Inter.ttf", [])
    unbounded_path = resolve_font_path(repo_root, "Unbounded.ttf", [])

    windows_segoe = r"C:\Windows\Fonts\segoeui.ttf"
    sub_font_path = resolve_font_path(
        repo_root,
        "Inter.ttf",
        [windows_segoe, "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"],
    )
    if os.path.isfile(windows_segoe):
        sub_font_path = windows_segoe

    if not os.path.isfile(inter_path):
        print(f"Error: Inter font not found at '{inter_path}'", file=sys.stderr)
        sys.exit(1)
    if not os.path.isfile(unbounded_path):
        print(f"Error: Unbounded font not found at '{unbounded_path}'", file=sys.stderr)
        sys.exit(1)

    print(f"Generating banners into: {out_dir}")
    print(f"Title font: {unbounded_path}")
    print(f"Brand font: {inter_path}")
    print(f"Subtitle font: {sub_font_path}")
    print("-" * 60)

    if args.test or args.banner == "test_banner.png":
        test_filename = args.banner if args.banner else "test_banner.png"
        banners_to_process = [(test_filename, args.title, args.subtitle, args.counter, args.font_size)]
    else:
        banners_to_process = BANNERS
        if args.banner:
            banners_to_process = [b for b in BANNERS if b[0] == args.banner]
            if not banners_to_process:
                print(f"Error: Banner '{args.banner}' not recognized in definitions.", file=sys.stderr)
                sys.exit(1)

    for filename, title, subtitle, counter, font_size in banners_to_process:
        saved_path = generate_banner(
            filename=filename,
            title=title,
            subtitle=subtitle,
            counter=counter,
            font_size=font_size,
            out_dir=out_dir,
            inter_path=inter_path,
            unbounded_path=unbounded_path,
            sub_font_path=sub_font_path,
        )
        file_size = saved_path.stat().st_size
        print(f"[OK] {filename:<22} [{counter}] -> {file_size:>6} bytes")

    print("-" * 60)
    print(f"Success! {len(banners_to_process)} banner(s) generated.")


if __name__ == "__main__":
    main()
