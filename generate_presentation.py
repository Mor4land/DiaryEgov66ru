import os
import qrcode
from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.enum.text import PP_ALIGN
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_SHAPE

# 1. Setup presentation 16:9 widescreen
prs = Presentation()
prs.slide_width = Inches(13.333)
prs.slide_height = Inches(7.5)
blank_layout = prs.slide_layouts[6]

# Color Palette (Slate Dark Theme)
BG_COLOR = RGBColor(15, 23, 42)        # Slate 900 #0F172A
CARD_BG = RGBColor(30, 41, 59)         # Slate 800 #1E293B
CARD_BORDER = RGBColor(51, 65, 85)     # Slate 700 #334155
CARD_HOVER = RGBColor(41, 53, 72)
TEXT_WHITE = RGBColor(248, 250, 252)   # Slate 50 #F8FAFC
TEXT_MUTED = RGBColor(148, 163, 184)   # Slate 400 #94A3B8
ACCENT_BLUE = RGBColor(56, 189, 248)   # Sky 400 #38BDF8
ACCENT_INDIGO = RGBColor(99, 102, 241) # Indigo 500 #6366F1
ACCENT_GREEN = RGBColor(16, 185, 129)  # Emerald 500 #10B981
ACCENT_AMBER = RGBColor(245, 158, 11)  # Amber 500 #F59E0B
ACCENT_PURPLE = RGBColor(168, 85, 247) # Purple 500 #A855F7

banner_dir = os.path.abspath("MirageDiary.Server/wwwroot/images/banners")
qr_dir = os.path.abspath("presentation_assets")
os.makedirs(qr_dir, exist_ok=True)

# Generate QR codes if not exist
if not os.path.exists(os.path.join(qr_dir, "qr_web.png")):
    q1 = qrcode.QRCode(box_size=10, border=2)
    q1.add_data("https://miragediary.space")
    q1.make(fit=True)
    q1.make_image(fill_color="#0F172A", back_color="white").save(os.path.join(qr_dir, "qr_web.png"))

if not os.path.exists(os.path.join(qr_dir, "qr_github.png")):
    q2 = qrcode.QRCode(box_size=10, border=2)
    q2.add_data("https://github.com/Miragestudio-repo/MirageDiary_Backend")
    q2.make(fit=True)
    q2.make_image(fill_color="#0F172A", back_color="white").save(os.path.join(qr_dir, "qr_github.png"))

if not os.path.exists(os.path.join(qr_dir, "qr_bot.png")):
    q3 = qrcode.QRCode(box_size=10, border=2)
    q3.add_data("https://t.me/MirageDiary_bot")
    q3.make(fit=True)
    q3.make_image(fill_color="#0F172A", back_color="white").save(os.path.join(qr_dir, "qr_bot.png"))

def add_bg(slide):
    bg = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, 0, 0, prs.slide_width, prs.slide_height)
    bg.fill.solid()
    bg.fill.fore_color.rgb = BG_COLOR
    bg.line.fill.background()
    return bg

def add_header(slide, number_str, title_str, subtitle_str=""):
    tb = slide.shapes.add_textbox(Inches(0.8), Inches(0.45), Inches(11.733), Inches(1.1))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_left = tf.margin_top = tf.margin_right = tf.margin_bottom = 0
    
    p = tf.paragraphs[0]
    run_num = p.add_run()
    run_num.text = f"{number_str}  |  "
    run_num.font.size = Pt(13)
    run_num.font.bold = True
    run_num.font.color.rgb = ACCENT_BLUE
    run_num.font.name = "Segoe UI"
    
    run_title = p.add_run()
    run_title.text = title_str
    run_title.font.size = Pt(22)
    run_title.font.bold = True
    run_title.font.color.rgb = TEXT_WHITE
    run_title.font.name = "Segoe UI"
    
    if subtitle_str:
        p2 = tf.add_paragraph()
        p2.space_before = Pt(4)
        run_sub = p2.add_run()
        run_sub.text = subtitle_str
        run_sub.font.size = Pt(12)
        run_sub.font.color.rgb = TEXT_MUTED
        run_sub.font.name = "Segoe UI"

def create_card(slide, left, top, width, height, bg_color=CARD_BG, border_color=CARD_BORDER):
    shape = slide.shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, left, top, width, height)
    shape.fill.solid()
    shape.fill.fore_color.rgb = bg_color
    if border_color:
        shape.line.color.rgb = border_color
        shape.line.width = Pt(1.2)
    else:
        shape.line.fill.background()
    return shape

def add_footer(slide, current_idx, total=12):
    tb = slide.shapes.add_textbox(Inches(0.8), Inches(6.9), Inches(11.733), Inches(0.35))
    tf = tb.text_frame
    tf.margin_left = tf.margin_top = tf.margin_right = tf.margin_bottom = 0
    p = tf.paragraphs[0]
    
    r1 = p.add_run()
    r1.text = "MirageDiary • Интеллектуальная кроссплатформенная экосистема школьного дневника"
    r1.font.size = Pt(10)
    r1.font.color.rgb = TEXT_MUTED
    r1.font.name = "Segoe UI"
    
    r_space = p.add_run()
    r_space.text = " " * 80
    
    r2 = p.add_run()
    r2.text = f"{current_idx:02d} / {total:02d}"
    r2.font.size = Pt(10)
    r2.font.bold = True
    r2.font.color.rgb = ACCENT_BLUE
    r2.font.name = "Segoe UI"

# ==========================================
# SLIDE 1: COVER / TITLE
# ==========================================
s1 = prs.slides.add_slide(blank_layout)
add_bg(s1)

# Badge: Contest 2026
b1 = s1.shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, Inches(0.8), Inches(0.8), Inches(3.8), Inches(0.4))
b1.fill.solid()
b1.fill.fore_color.rgb = RGBColor(30, 58, 138)
b1.line.color.rgb = ACCENT_BLUE
b1.line.width = Pt(1)
p_b1 = b1.text_frame.paragraphs[0]
p_b1.alignment = PP_ALIGN.CENTER
r_b1 = p_b1.add_run()
r_b1.text = "🚀 ВСЕРОССИЙСКИЙ КОНКУРС IT-ПРОЕКТОВ 2026"
r_b1.font.size = Pt(10.5)
r_b1.font.bold = True
r_b1.font.color.rgb = ACCENT_BLUE
r_b1.font.name = "Segoe UI"

# Main Title block
tb1 = s1.shapes.add_textbox(Inches(0.8), Inches(1.4), Inches(7.5), Inches(3.2))
tf1 = tb1.text_frame
tf1.word_wrap = True
tf1.margin_left = tf1.margin_top = tf1.margin_right = tf1.margin_bottom = 0

p1 = tf1.paragraphs[0]
r_title = p1.add_run()
r_title.text = "MirageDiary"
r_title.font.size = Pt(44)
r_title.font.bold = True
r_title.font.color.rgb = TEXT_WHITE
r_title.font.name = "Segoe UI"

p2 = tf1.add_paragraph()
p2.space_before = Pt(8)
r_sub = p2.add_run()
r_sub.text = "Интеллектуальная кроссплатформенная экосистема школьного дневника"
r_sub.font.size = Pt(20)
r_sub.font.bold = True
r_sub.font.color.rgb = ACCENT_BLUE
r_sub.font.name = "Segoe UI"

p3 = tf1.add_paragraph()
p3.space_before = Pt(14)
r_desc = p3.add_run()
r_desc.text = "Мгновенный доступ к расписанию, оценкам и домашним заданиям через Telegram, национальный мессенджер MAX и React MiniApp. Прямая интеграция с ФГИС «Моя Школа» через ЕСИА (Госуслуги) с максимальной защитой персональных данных по 152-ФЗ."
r_desc.font.size = Pt(13)
r_desc.font.color.rgb = TEXT_MUTED
r_desc.font.name = "Segoe UI"

# Feature tags pills
tags = [
    ".NET 10 Kestrel", "ЕСИА PKCE S256", "Шифрование enc:v2:", 
    "Telegram & MAX", "152-ФЗ РФ", "Отклик <200мс"
]
left_tag = Inches(0.8)
top_tag = Inches(4.9)
for tag in tags:
    pill = s1.shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, left_tag, top_tag, Inches(1.85), Inches(0.38))
    pill.fill.solid()
    pill.fill.fore_color.rgb = CARD_BG
    pill.line.color.rgb = CARD_BORDER
    p_pill = pill.text_frame.paragraphs[0]
    p_pill.alignment = PP_ALIGN.CENTER
    r_pill = p_pill.add_run()
    r_pill.text = tag
    r_pill.font.size = Pt(9.5)
    r_pill.font.bold = True
    r_pill.font.color.rgb = TEXT_WHITE
    r_pill.font.name = "Segoe UI"
    left_tag += Inches(1.95)

# Metrics bar at bottom left
card_stat = create_card(s1, Inches(0.8), Inches(5.5), Inches(7.4), Inches(1.1), bg_color=CARD_BG)
tb_stat = s1.shapes.add_textbox(Inches(0.9), Inches(5.55), Inches(7.2), Inches(1.0))
tf_stat = tb_stat.text_frame
tf_stat.word_wrap = True
tf_stat.margin_top = tf_stat.margin_bottom = tf_stat.margin_left = tf_stat.margin_right = 0
p_stat = tf_stat.paragraphs[0]

items_stat = [
    ("< 200 мс", "Скорость отклика"),
    ("100%", "Соответствие 152-ФЗ"),
    ("0 паролей", "Zero-Knowledge"),
    ("x20+", "Экономия трафика API")
]
for val, lbl in items_stat:
    r_v = p_stat.add_run()
    r_v.text = f"{val}  "
    r_v.font.bold = True
    r_v.font.size = Pt(14)
    r_v.font.color.rgb = ACCENT_GREEN
    r_l = p_stat.add_run()
    r_l.text = f"{lbl}       "
    r_l.font.size = Pt(10)
    r_l.font.color.rgb = TEXT_MUTED

# Right side: Banner Image
banner_menu_path = os.path.join(banner_dir, "banner_menu.png")
if os.path.exists(banner_menu_path):
    s1.shapes.add_picture(banner_menu_path, Inches(8.5), Inches(1.5), Inches(4.0), Inches(3.2))

# Live production card
live_card = create_card(s1, Inches(8.5), Inches(5.0), Inches(4.0), Inches(1.6))
tb_live = s1.shapes.add_textbox(Inches(8.7), Inches(5.1), Inches(3.6), Inches(1.4))
tf_live = tb_live.text_frame
tf_live.word_wrap = True
tf_live.margin_top = tf_live.margin_bottom = tf_live.margin_left = tf_live.margin_right = 0
p_live1 = tf_live.paragraphs[0]
r_live_t = p_live1.add_run()
r_live_t.text = "🌐 Продакшен-система запущена:"
r_live_t.font.bold = True
r_live_t.font.size = Pt(11)
r_live_t.font.color.rgb = ACCENT_BLUE

p_live2 = tf_live.add_paragraph()
p_live2.space_before = Pt(4)
r_url = p_live2.add_run()
r_url.text = "https://miragediary.space\n"
r_url.font.bold = True
r_url.font.size = Pt(12)
r_url.font.color.rgb = TEXT_WHITE

r_url_sub = p_live2.add_run()
r_url_sub.text = "Сервер: .NET 10 Kestrel (Linux-x64, Nginx, SSL)\nКлиенты: Telegram Bot (@MirageDiary_bot) & MAX"
r_url_sub.font.size = Pt(9.5)
r_url_sub.font.color.rgb = TEXT_MUTED

add_footer(s1, 1)

# ==========================================
# SLIDE 2: PROBLEM & MARKET CONTEXT
# ==========================================
s2 = prs.slides.add_slide(blank_layout)
add_bg(s2)
add_header(s2, "02", "Проблема и актуальность", "Ключевые барьеры использования существующих государственных школьных систем")

problems = [
    ("🐢 Медленная загрузка порталов", 
     "Время открытия веб-страницы регионального дневника достигает 8–15 секунд. В школьных коридорах при слабом мобильном интернете портал часто вообще не открывается перед уроком."),
    ("💥 Пиковые перегрузки серверов", 
     "В 07:45–08:30 (ученики смотрят расписание и кабинеты) и в 17:00–19:00 (проверка домашнего задания) региональные шлюзы падают или отдают ошибки 502/504."),
    ("🧩 Сложная навигация и стресс", 
     "Многократный ввод логинов, громоздкие неадаптивные таблицы, отсутствие встроенного подсчета среднего балла для четверти и невозможность быстро узнать время до конца урока."),
    ("📱 Изолированность от мессенджеров", 
     "Школьники и родители проводят до 90% экранного времени в Telegram и MAX, но вынуждены переключаться в сторонний браузер с постоянным разлогиниванием сессий.")
]

card_w = Inches(5.6)
card_h = Inches(2.2)
coords = [
    (Inches(0.8), Inches(1.8)),
    (Inches(6.8), Inches(1.8)),
    (Inches(0.8), Inches(4.3)),
    (Inches(6.8), Inches(4.3))
]

for idx, (p_title, p_text) in enumerate(problems):
    col, row = coords[idx]
    create_card(s2, col, row, card_w, card_h)
    tb = s2.shapes.add_textbox(col + Inches(0.3), row + Inches(0.25), card_w - Inches(0.6), card_h - Inches(0.5))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_top = tf.margin_bottom = tf.margin_left = tf.margin_right = 0
    
    p = tf.paragraphs[0]
    r_pt = p.add_run()
    r_pt.text = p_title
    r_pt.font.bold = True
    r_pt.font.size = Pt(14)
    r_pt.font.color.rgb = ACCENT_AMBER
    r_pt.font.name = "Segoe UI"
    
    p_body = tf.add_paragraph()
    p_body.space_before = Pt(8)
    r_pb = p_body.add_run()
    r_pb.text = p_text
    r_pb.font.size = Pt(11.5)
    r_pb.font.color.rgb = TEXT_MUTED
    r_pb.font.name = "Segoe UI"

add_footer(s2, 2)

# ==========================================
# SLIDE 3: SOLUTION - MIRAGEDIARY ECOSYSTEM
# ==========================================
s3 = prs.slides.add_slide(blank_layout)
add_bg(s3)
add_header(s3, "03", "Решение: Экосистема MirageDiary", "Легковесный, отказоустойчивый шлюз к образовательным данным прямо в мессенджерах")

pillars = [
    ("⚡ Мгновенный отклик (<200 мс)", 
     "Высокопроизводительное кэширование и компактная отдача. Любой экран (расписание, оценки, ДЗ) открывается за доли секунды без расхода трафика."),
    ("🤖 Мультитранспортная среда", 
     "Единое ядро обслуживает пользователей Telegram, национального мессенджера MAX и веб-клиента React MiniApp без дублирования бизнес-логики."),
    ("🔔 Умные контекстные сервисы", 
     "Таймер звонков со сменами, калькулятор оценок (/calc), автоматический контроль рюкзака и инвентаря на завтра в 20:30."),
    ("🔗 Бесшовная связка аккаунтов", 
     "Быстрый переход Telegram ⟷ MAX по 6-значному коду с сохранением сессии Госуслуг и настроек будильника без повторного логина.")
]

for idx, (pil_title, pil_text) in enumerate(pillars):
    col, row = coords[idx]
    create_card(s3, col, row, card_w, card_h)
    tb = s3.shapes.add_textbox(col + Inches(0.3), row + Inches(0.25), card_w - Inches(0.6), card_h - Inches(0.5))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_top = tf.margin_bottom = tf.margin_left = tf.margin_right = 0
    
    p = tf.paragraphs[0]
    r_pt = p.add_run()
    r_pt.text = pil_title
    r_pt.font.bold = True
    r_pt.font.size = Pt(14)
    r_pt.font.color.rgb = ACCENT_BLUE
    r_pt.font.name = "Segoe UI"
    
    p_body = tf.add_paragraph()
    p_body.space_before = Pt(8)
    r_pb = p_body.add_run()
    r_pb.text = pil_text
    r_pb.font.size = Pt(11.5)
    r_pb.font.color.rgb = TEXT_MUTED
    r_pb.font.name = "Segoe UI"

add_footer(s3, 3)

# ==========================================
# SLIDE 4: ARCHITECTURE & TECH STACK
# ==========================================
s4 = prs.slides.add_slide(blank_layout)
add_bg(s4)
add_header(s4, "04", "Архитектура и стек технологий", "Высокопроизводительный модульный бэкенд на .NET 10 и реактивные клиенты")

arch_cards = [
    ("⚙️ Серверное ядро (.NET 10 / C# 14)", 
     "• ASP.NET Core 10 Kestrel (сборка Linux-x64, режим Release)\n"
     "• Entity Framework Core 10, фоновые Hosted Services\n"
     "• Защита исходного кода через Obfuscar 2.2\n"
     "• Готовые production манифесты Docker & Docker Compose"),
    ("🛡️ Безопасность и интеграции", 
     "• Шлюз ЕСИА: протокол OAuth 2.0 PKCE с хэшированием S256\n"
     "• Конвертное шифрование токенов enc:v2: (AES-256-GCM + DEK)\n"
     "• Встроенный RussianTrustedCertificateValidator (Минцифры)\n"
     "• Очистка памяти секретов CryptographicOperations.ZeroMemory"),
    ("💾 База данных и хранилище", 
     "• Встроенная база SQLite 3 в высоконадежном режиме WAL\n"
     "• Права доступа к файлу базы 0600 (только процессу приложения)\n"
     "• Сервис DatabaseBackupService: горячие бэкапы каждые 6 часов\n"
     "• Пул SharedClassroomCache для кэширования расписания классов"),
    ("📱 Клиентские интерфейсы", 
     "• Telegram.Bot 22.9.2 с лонг-поллингом и вебхук-адаптером\n"
     "• MAX Messenger: кастомный асинхронный REST/Webhook клиент\n"
     "• MiniApp: React 19, TypeScript, Vite, темная дизайн-система\n"
     "• Динамические баннеры высокого разрешения (1200x580)")
]

for idx, (a_title, a_text) in enumerate(arch_cards):
    col, row = coords[idx]
    create_card(s4, col, row, card_w, card_h)
    tb = s4.shapes.add_textbox(col + Inches(0.3), row + Inches(0.2), card_w - Inches(0.6), card_h - Inches(0.4))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_top = tf.margin_bottom = tf.margin_left = tf.margin_right = 0
    
    p = tf.paragraphs[0]
    r_pt = p.add_run()
    r_pt.text = a_title
    r_pt.font.bold = True
    r_pt.font.size = Pt(13)
    r_pt.font.color.rgb = ACCENT_INDIGO
    r_pt.font.name = "Segoe UI"
    
    p_body = tf.add_paragraph()
    p_body.space_before = Pt(6)
    r_pb = p_body.add_run()
    r_pb.text = a_text
    r_pb.font.size = Pt(10.5)
    r_pb.font.color.rgb = TEXT_WHITE
    r_pb.font.name = "Segoe UI"

add_footer(s4, 4)

# ==========================================
# SLIDE 5: SECURITY & 152-FZ COMPLIANCE
# ==========================================
s5 = prs.slides.add_slide(blank_layout)
add_bg(s5)
add_header(s5, "05", "Информационная безопасность и 152-ФЗ", "Эшелонированная криптографическая защита и строгое соблюдение законодательства РФ")

sec_cards = [
    ("🔐 Конвертное шифрование enc:v2:",
     "• Двухуровневая схема: Master KEK (Key Encryption Key) + динамический DEK (Data Encryption Key) на каждую запись.\n"
     "• Алгоритм AES-256-GCM с 96-битным IV и 128-битным тегом целостности.\n"
     "• Зануление открытых ключей в RAM через ZeroMemory сразу после вычисления.",
     ACCENT_BLUE),
    ("🔑 Zero-Knowledge для паролей",
     "• Пароли пользователей от Госуслуг никогда не вводятся и не сохраняются на сервере.\n"
     "• Авторизация выполняется через мобильный контур ЕСИА по стандарту OAuth 2.0 PKCE S256.\n"
     "• Бэкенд получает только ограниченный JWT-токен сессии дневника.",
     ACCENT_GREEN),
    ("📜 Согласие и аудит (152-ФЗ)",
     "• Обязательный онбординг: блокировка функций до подтверждения согласия родителей (ст. 9 ч. 6 152-ФЗ).\n"
     "• Аудит волеизъявления: фиксация таймштампа согласия в UTC в неизменяемом журнале.\n"
     "• Право на забвение: команда /delete_account безвозвратно стирает все токены и кэши.",
     ACCENT_AMBER),
    ("🇷🇺 Суверенитет и локализация",
     "• 100% данных и вычислительных узлов размещены на территории Российской Федерации (ст. 18 ч. 5).\n"
     "• Встроенные корневые сертификаты Минцифры РФ (Russian Trusted CAs).\n"
     "• Отсутствие зависимостей от зарубежных закрытых облачных API и CDN.",
     ACCENT_PURPLE)
]

for idx, (s_title, s_text, col_accent) in enumerate(sec_cards):
    col, row = coords[idx]
    create_card(s5, col, row, card_w, card_h)
    tb = s5.shapes.add_textbox(col + Inches(0.3), row + Inches(0.2), card_w - Inches(0.6), card_h - Inches(0.4))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_top = tf.margin_bottom = tf.margin_left = tf.margin_right = 0
    
    p = tf.paragraphs[0]
    r_pt = p.add_run()
    r_pt.text = s_title
    r_pt.font.bold = True
    r_pt.font.size = Pt(13)
    r_pt.font.color.rgb = col_accent
    r_pt.font.name = "Segoe UI"
    
    p_body = tf.add_paragraph()
    p_body.space_before = Pt(6)
    r_pb = p_body.add_run()
    r_pb.text = s_text
    r_pb.font.size = Pt(10.5)
    r_pb.font.color.rgb = TEXT_WHITE
    r_pb.font.name = "Segoe UI"

add_footer(s5, 5)

# ==========================================
# SLIDE 6: CORE FUNCTIONALITY & UX
# ==========================================
s6 = prs.slides.add_slide(blank_layout)
add_bg(s6)
add_header(s6, "06", "Ключевая функциональность и UX", "Интуитивный интерфейс, разработанный под реальные сценарии школьной жизни")

# 3 vertical cards + banners
col_w = Inches(3.7)
h_feat = Inches(4.8)
col_xs = [Inches(0.8), Inches(4.8), Inches(8.8)]

features_data = [
    ("📅 Расписание и «До звонка»", 
     "banner_now.png",
     "• Мгновенный таймер (/now): сколько минут осталось до конца текущего урока или перемены.\n"
     "• Поддержка 1 и 2 смен для МАОУ СОШ № 113.\n"
     "• Индикация кабинетов, ФИО учителей и статуса замен уроков."),
    ("📊 Оценки и калькулятор (/calc)", 
     "banner_marks.png",
     "• Наглядный средний балл по всем предметам с подсветкой критических четвертных оценок.\n"
     "• Калькулятор целей: алгоритм рассчитывает, сколько именно «5» или «4» нужно получить до конца четверти.\n"
     "• Фильтрация оценок по датам и типам работ."),
    ("⏰ Умный будильник (SmartAlarm)", 
     "banner_alarm.png",
     "• 07:30: Утренний дайджест с расписанием и кабинетом первого урока.\n"
     "• 17:00: Напоминание о невыполненных ДЗ.\n"
     "• 20:30: Анализ предметов на завтра и напоминание собрать рюкзак (форма, ИЗО, циркуль).")
]

for idx, (f_title, b_name, f_text) in enumerate(features_data):
    cx = col_xs[idx]
    create_card(s6, cx, Inches(1.8), col_w, h_feat)
    
    img_path = os.path.join(banner_dir, b_name)
    if os.path.exists(img_path):
        s6.shapes.add_picture(img_path, cx + Inches(0.2), Inches(2.0), col_w - Inches(0.4), Inches(1.55))
        
    tb = s6.shapes.add_textbox(cx + Inches(0.2), Inches(3.65), col_w - Inches(0.4), Inches(2.8))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_top = tf.margin_bottom = tf.margin_left = tf.margin_right = 0
    
    p = tf.paragraphs[0]
    r_pt = p.add_run()
    r_pt.text = f_title
    r_pt.font.bold = True
    r_pt.font.size = Pt(12.5)
    r_pt.font.color.rgb = ACCENT_BLUE
    r_pt.font.name = "Segoe UI"
    
    p_body = tf.add_paragraph()
    p_body.space_before = Pt(6)
    r_pb = p_body.add_run()
    r_pb.text = f_text
    r_pb.font.size = Pt(10)
    r_pb.font.color.rgb = TEXT_MUTED
    r_pb.font.name = "Segoe UI"

add_footer(s6, 6)

# ==========================================
# SLIDE 7: FAULT TOLERANCE & PERFORMANCE
# ==========================================
s7 = prs.slides.add_slide(blank_layout)
add_bg(s7)
add_header(s7, "07", "Отказоустойчивость и производительность", "Архитектурные оптимизации для стабильной работы в часы пиковых нагрузок")

perf_items = [
    ("🚀 SharedClassroomCache",
     "Вместо сотен параллельных запросов от учеников одного класса (например, 8Б) при открытии дневника перед уроком, сервер опрашивает ФГИС «Моя Школа» ровно один раз. Ответ кэшируется в оперативной памяти с TTL.\n\nРезультат: Снижение нагрузки на государственный шлюз более чем в 20 раз и ответ пользователям за 15–30 мс."),
    ("🔧 Автодетекция регламентных работ",
     "Государственные информационные системы регулярно проводят ночные и утренние обновления. Интеллектуальный шлюз MirageDiary анализирует статус-коды и HTML-ответы.\n\nРезультат: Бот не падает с ошибками, а выводит статусное сообщение «🔧 Идут техработы» и показывает закэшированное расписание."),
    ("🔄 Фоновое обновление сессий (TokenRefresh)",
     "Специализированный Hosted Service производит упреждающее обновление access-токенов в фоновом режиме до истечения их срока действия.\n\nРезультат: Пользователю не приходится ждать 2–3 секунды на рефреш токена при вводе команды — бот отвечает мгновенно."),
    ("🛡️ Устойчивый режим Kestrel & WAL",
     "Пул потоков ASP.NET Core 10 оптимизирован под асинхронную обработку тысяч вебхук-событий в секунду без блокировок. База SQLite работает в режиме WAL (Write-Ahead Logging).\n\nРезультат: Параллельные операции чтения расписания не блокируют фоновые записи логов и настроек пользователей.")
]

for idx, (p_t, p_d) in enumerate(perf_items):
    col, row = coords[idx]
    create_card(s7, col, row, card_w, card_h)
    tb = s7.shapes.add_textbox(col + Inches(0.3), row + Inches(0.2), card_w - Inches(0.6), card_h - Inches(0.4))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_top = tf.margin_bottom = tf.margin_left = tf.margin_right = 0
    
    p = tf.paragraphs[0]
    r_pt = p.add_run()
    r_pt.text = p_t
    r_pt.font.bold = True
    r_pt.font.size = Pt(13.5)
    r_pt.font.color.rgb = ACCENT_GREEN
    r_pt.font.name = "Segoe UI"
    
    p_body = tf.add_paragraph()
    p_body.space_before = Pt(6)
    r_pb = p_body.add_run()
    r_pb.text = p_d
    r_pb.font.size = Pt(10.5)
    r_pb.font.color.rgb = TEXT_WHITE
    r_pb.font.name = "Segoe UI"

add_footer(s7, 7)

# ==========================================
# SLIDE 8: IMPORT SUBSTITUTION & DOMESTIC TECH
# ==========================================
s8 = prs.slides.add_slide(blank_layout)
add_bg(s8)
add_header(s8, "08", "Импортозамещение и суверенная среда", "Полная готовность к работе в национальном цифровом контуре Российской Федерации")

imp_items = [
    ("🇷🇺 Интеграция с мессенджером MAX",
     "• Реализован полнофункциональный бот для национальной платформы MAX (VK/MAX Messenger).\n"
     "• Поддержка нативных клавиатур, callback-событий и безопасной доставки уведомлений.\n"
     "• Гарантия непрерывности работы сервиса при любых сценариях деградации зарубежных каналов."),
    ("🔗 Сквозное связывание Telegram ⟷ MAX",
     "• Сервис AccountLinkingService генерирует одноразовый 6-значный криптографический PIN.\n"
     "• Пользователь вводит PIN во втором мессенджере — сессия Госуслуг и настройки объединяются.\n"
     "• Единый профиль без необходимости проходить авторизацию на Госуслугах дважды."),
    ("🔒 Доверенные корневые сертификаты РФ",
     "• Интеграция сертификатов Минцифры России на уровне базового Docker-образа и Kestrel.\n"
     "• Кастомный валидатор RussianTrustedCertificateValidator для верификации цепочек доверия TLS.\n"
     "• Корректное защищенное соединение с порталами ЕСИА и ФГИС «Моя Школа»."),
    ("🏢 Локальная инфраструктура и безопасность",
     "• Развертывание на отечественных серверных мощностях под управлением Linux.\n"
     "• Полный контроль над кодом: защита интеллектуальной собственности обфускацией Obfuscar.\n"
     "• Отсутствие закрытых проприетарных зарубежных SaaS-решений в цепочке обработки данных.")
]

for idx, (i_t, i_d) in enumerate(imp_items):
    col, row = coords[idx]
    create_card(s8, col, row, card_w, card_h)
    tb = s8.shapes.add_textbox(col + Inches(0.3), row + Inches(0.2), card_w - Inches(0.6), card_h - Inches(0.4))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_top = tf.margin_bottom = tf.margin_left = tf.margin_right = 0
    
    p = tf.paragraphs[0]
    r_pt = p.add_run()
    r_pt.text = i_t
    r_pt.font.bold = True
    r_pt.font.size = Pt(13)
    r_pt.font.color.rgb = ACCENT_BLUE
    r_pt.font.name = "Segoe UI"
    
    p_body = tf.add_paragraph()
    p_body.space_before = Pt(6)
    r_pb = p_body.add_run()
    r_pb.text = i_d
    r_pb.font.size = Pt(10.5)
    r_pb.font.color.rgb = TEXT_WHITE
    r_pb.font.name = "Segoe UI"

add_footer(s8, 8)

# ==========================================
# SLIDE 9: CURRENT TRACTION & MVP STATUS
# ==========================================
s9 = prs.slides.add_slide(blank_layout)
add_bg(s9)
add_header(s9, "09", "Текущие результаты и статус MVP", "Проект доведен до боевого состояния и успешно функционирует на продакшен-сервере")

# Left Column: Metrics and achievements
card_left = create_card(s9, Inches(0.8), Inches(1.8), Inches(5.6), Inches(4.8))
tb_l = s9.shapes.add_textbox(Inches(1.1), Inches(2.0), Inches(5.0), Inches(4.4))
tf_l = tb_l.text_frame
tf_l.word_wrap = True
tf_l.margin_top = tf_l.margin_bottom = tf_l.margin_left = tf_l.margin_right = 0

p_ach_t = tf_l.paragraphs[0]
r_ach_t = p_ach_t.add_run()
r_ach_t.text = "🏆 Достигнутые результаты (Ready to Pitch)"
r_ach_t.font.bold = True
r_ach_t.font.size = Pt(15)
r_ach_t.font.color.rgb = ACCENT_GREEN

p_ach_b = tf_l.add_paragraph()
p_ach_b.space_before = Pt(10)
achievements = (
    "✅ Боевой продакшен на https://miragediary.space\n"
    "    Развернут на Linux VPS с настроенным SSL, Nginx и Kestrel.\n\n"
    "✅ Два активных бота (Telegram & MAX)\n"
    "    Поддерживают полный цикл: авторизация, расписание, оценки, ДЗ.\n\n"
    "✅ 100% готовность к требованиям конкурса (п. 9.7)\n"
    "    Собран автономный Docker-образ, подготовлен requirements.txt, синхронизированы порты healthcheck (5000).\n\n"
    "✅ Проведены нагрузочные тесты (test-all.ps1)\n"
    "    API выдерживает конкурентные запросы без утечек памяти и блокировок SQLite."
)
r_ach_b = p_ach_b.add_run()
r_ach_b.text = achievements
r_ach_b.font.size = Pt(11)
r_ach_b.font.color.rgb = TEXT_WHITE

# Right Column: Tech artifacts & Repositories
card_right = create_card(s9, Inches(6.8), Inches(1.8), Inches(5.6), Inches(4.8))
tb_r = s9.shapes.add_textbox(Inches(7.1), Inches(2.0), Inches(5.0), Inches(4.4))
tf_r = tb_r.text_frame
tf_r.word_wrap = True
tf_r.margin_top = tf_r.margin_bottom = tf_r.margin_left = tf_r.margin_right = 0

p_rep_t = tf_r.paragraphs[0]
r_rep_t = p_rep_t.add_run()
r_rep_t.text = "📦 Открытые репозитории и артефакты"
r_rep_t.font.bold = True
r_rep_t.font.size = Pt(15)
r_rep_t.font.color.rgb = ACCENT_BLUE

p_rep_b = tf_r.add_paragraph()
p_rep_b.space_before = Pt(10)
repos_info = (
    "📂 Бэкенд (.NET 10 Web API + Bots):\n"
    "    github.com/Miragestudio-repo/MirageDiary_Backend\n"
    "    (Чистая архитектура, EF Core, обфускация, тесты)\n\n"
    "📂 Фронтенд (React MiniApp):\n"
    "    github.com/Miragestudio-repo/MirageDiary-Miniapp\n"
    "    (TypeScript, Vite, темная адаптивная дизайн-система)\n\n"
    "🐳 Инфраструктура в Docker:\n"
    "    Multi-stage build на базе ASP.NET 10 Noble с сертификатами Минцифры РФ и запуском от non-root пользователя app."
)
r_rep_b = p_rep_b.add_run()
r_rep_b.text = repos_info
r_rep_b.font.size = Pt(11)
r_rep_b.font.color.rgb = TEXT_WHITE

add_footer(s9, 9)

# ==========================================
# SLIDE 10: ROADMAP 2026 - 2027
# ==========================================
s10 = prs.slides.add_slide(blank_layout)
add_bg(s10)
add_header(s10, "10", "Дорожная карта развития (Roadmap)", "Стратегический план масштабирования экосистемы на 2026–2027 учебный год")

roadmap_steps = [
    ("Q4 2026 (Пилотный запуск)", 
     "• Пилотное внедрение в 5 школах Свердловской области (МАОУ СОШ № 113 и партнеры)\n"
     "• Подключение первых 1500+ активных школьников и родителей\n"
     "• Отработка пользовательских отзывов и донастройка сетки звонков"),
    ("Q1 2027 (Календари и WebCal)", 
     "• Экспорт расписания и дедлайнов ДЗ по протоколу iCal / WebCal\n"
     "• Автосинхронизация с Яндекс Календарем, Google Calendar и Apple Calendar\n"
     "• Подписка родителей на сводки об успеваемости за неделю"),
    ("Q2 2027 (ИИ-ассистент в учебе)", 
     "• Интеграция локальной языковой модели для помощи в разборе сложных тем\n"
     "• Автоматическая суммаризация длинных параграфов домашних заданий\n"
     "• Генерация тренировочных викторин для подготовки к контрольным"),
    ("Q3 2027 (Федеральное масштабирование)", 
     "• Подключение новых регионов РФ, интегрированных с ФГИС «Моя Школа»\n"
     "• Разработка интерактивных виджетов рабочего стола для iOS и Android\n"
     "• Панель аналитики для классных руководителей и школьной администрации")
]

rm_w = Inches(2.7)
rm_h = Inches(4.8)
rm_xs = [Inches(0.8), Inches(3.8), Inches(6.8), Inches(9.8)]

for idx, (rm_t, rm_d) in enumerate(roadmap_steps):
    rx = rm_xs[idx]
    create_card(s10, rx, Inches(1.8), rm_w, rm_h)
    
    # Milestone badge
    badge = s10.shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, rx + Inches(0.2), Inches(2.0), rm_w - Inches(0.4), Inches(0.55))
    badge.fill.solid()
    badge.fill.fore_color.rgb = RGBColor(30, 58, 138)
    badge.line.color.rgb = ACCENT_BLUE
    p_b = badge.text_frame.paragraphs[0]
    p_b.alignment = PP_ALIGN.CENTER
    r_b = p_b.add_run()
    r_b.text = rm_t
    r_b.font.bold = True
    r_b.font.size = Pt(10.5)
    r_b.font.color.rgb = ACCENT_BLUE
    
    tb = s10.shapes.add_textbox(rx + Inches(0.2), Inches(2.7), rm_w - Inches(0.4), Inches(3.7))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_top = tf.margin_bottom = tf.margin_left = tf.margin_right = 0
    
    p_body = tf.paragraphs[0]
    r_pb = p_body.add_run()
    r_pb.text = rm_d
    r_pb.font.size = Pt(10.5)
    r_pb.font.color.rgb = TEXT_WHITE
    r_pb.font.name = "Segoe UI"

add_footer(s10, 10)

# ==========================================
# SLIDE 11: TEAM & COMPETENCIES
# ==========================================
s11 = prs.slides.add_slide(blank_layout)
add_bg(s11)
add_header(s11, "11", "Команда проекта и компетенции", "Сбалансированная инженерная экспертиза для масштабирования продукта")

team_cards = [
    ("💻 Backend & Security Architect",
     "• Архитектура распределенного ядра на .NET 10 (C# 14)\n"
     "• Реализация криптографического протокола enc:v2: (AES-256-GCM + DEK)\n"
     "• Интеграция шлюза ЕСИА (OAuth2 PKCE S256) и ФГИС «Моя Школа»\n"
     "• Обеспечение соответствия требованиям 152-ФЗ и защита от атак",
     ACCENT_BLUE),
    ("🎨 Frontend & UI/UX Engineer",
     "• Проектирование эргономичной дизайн-системы для школьников и родителей\n"
     "• Разработка высокопроизводительного React MiniApp (TypeScript + Vite)\n"
     "• Создание системы визуальных баннеров высокого разрешения (1200x580)\n"
     "• In-place интерфейсы ботов Telegram и MAX без спама в чате",
     ACCENT_INDIGO),
    ("🚀 Product Manager & QA / DevOps",
     "• Проработка продуктовых сценариев (умный будильник, таймер звонков)\n"
     "• Автоматизация CI/CD, сборка Docker-образов, конфигурирование Nginx\n"
     "• Проведение нагрузочного тестирования и профилирование Kestrel\n"
     "• Взаимодействие со школьной администрацией и сбор обратной связи",
     ACCENT_GREEN)
]

for idx, (t_title, t_desc, col_c) in enumerate(team_cards):
    cx = col_xs[idx]
    create_card(s11, cx, Inches(1.8), col_w, h_feat)
    
    tb = s11.shapes.add_textbox(cx + Inches(0.3), Inches(2.2), col_w - Inches(0.6), Inches(4.0))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_top = tf.margin_bottom = tf.margin_left = tf.margin_right = 0
    
    p = tf.paragraphs[0]
    r_pt = p.add_run()
    r_pt.text = t_title
    r_pt.font.bold = True
    r_pt.font.size = Pt(13)
    r_pt.font.color.rgb = col_c
    r_pt.font.name = "Segoe UI"
    
    p_body = tf.add_paragraph()
    p_body.space_before = Pt(12)
    r_pb = p_body.add_run()
    r_pb.text = t_desc
    r_pb.font.size = Pt(11)
    r_pb.font.color.rgb = TEXT_WHITE
    r_pb.font.name = "Segoe UI"

add_footer(s11, 11)

# ==========================================
# SLIDE 12: CONCLUSION & CONTACTS
# ==========================================
s12 = prs.slides.add_slide(blank_layout)
add_bg(s12)
add_header(s12, "12", "Заключение и контакты", "MirageDiary — современный стандарт взаимодействия со школьными данными")

# Left side: Summary value statement & links
card_s12_l = create_card(s12, Inches(0.8), Inches(1.8), Inches(6.0), Inches(4.8))
tb_12l = s12.shapes.add_textbox(Inches(1.1), Inches(2.1), Inches(5.4), Inches(4.2))
tf_12l = tb_12l.text_frame
tf_12l.word_wrap = True
tf_12l.margin_top = tf_12l.margin_bottom = tf_12l.margin_left = tf_12l.margin_right = 0

p_call = tf_12l.paragraphs[0]
r_call = p_call.add_run()
r_call.text = "🌟 Почему MirageDiary побеждает:"
r_call.font.bold = True
r_call.font.size = Pt(16)
r_call.font.color.rgb = ACCENT_BLUE

p_pts = tf_12l.add_paragraph()
p_pts.space_before = Pt(10)
concl_text = (
    "• Реальный, уже работающий продукт на https://miragediary.space\n"
    "• Безопасность уровня enterprise: конвертное шифрование enc:v2:, Zero-Knowledge паролей Госуслуг и 100% соблюдение 152-ФЗ.\n"
    "• Высочайшая скорость отклика (<200 мс) и снижение нагрузки на государственные серверы в 20+ раз благодаря SharedClassroomCache.\n"
    "• Готовность к импортозамещению: поддержка отечественного мессенджера MAX и российских сертификатов Минцифры.\n"
    "• Полная открытость кода, Docker-контейнеризация и готовность к масштабированию."
)
r_pts = p_pts.add_run()
r_pts.text = concl_text
r_pts.font.size = Pt(11)
r_pts.font.color.rgb = TEXT_WHITE

# Right side: Interactive QR Codes
card_s12_r = create_card(s12, Inches(7.1), Inches(1.8), Inches(5.4), Inches(4.8))
tb_12r = s12.shapes.add_textbox(Inches(7.4), Inches(2.1), Inches(4.8), Inches(0.5))
tf_12r = tb_12r.text_frame
tf_12r.word_wrap = True
p_qr_head = tf_12r.paragraphs[0]
r_qrh = p_qr_head.add_run()
r_qrh.text = "📱 Попробуйте прямо сейчас:"
r_qrh.font.bold = True
r_qrh.font.size = Pt(15)
r_qrh.font.color.rgb = ACCENT_GREEN

# QR 1: Telegram Bot & WebApp
qr_bot_path = os.path.join(qr_dir, "qr_bot.png")
if os.path.exists(qr_bot_path):
    s12.shapes.add_picture(qr_bot_path, Inches(7.5), Inches(2.7), Inches(1.8), Inches(1.8))
    tb_q1 = s12.shapes.add_textbox(Inches(7.3), Inches(4.55), Inches(2.2), Inches(0.6))
    p_q1 = tb_q1.text_frame.paragraphs[0]
    p_q1.alignment = PP_ALIGN.CENTER
    r_q1 = p_q1.add_run()
    r_q1.text = "Telegram-бот\n@MirageDiary_bot"
    r_q1.font.size = Pt(10)
    r_q1.font.bold = True
    r_q1.font.color.rgb = TEXT_WHITE

# QR 2: GitHub Repository
qr_git_path = os.path.join(qr_dir, "qr_github.png")
if os.path.exists(qr_git_path):
    s12.shapes.add_picture(qr_git_path, Inches(10.1), Inches(2.7), Inches(1.8), Inches(1.8))
    tb_q2 = s12.shapes.add_textbox(Inches(9.9), Inches(4.55), Inches(2.2), Inches(0.6))
    p_q2 = tb_q2.text_frame.paragraphs[0]
    p_q2.alignment = PP_ALIGN.CENTER
    r_q2 = p_q2.add_run()
    r_q2.text = "GitHub Репозиторий\nMirageDiary_Backend"
    r_q2.font.size = Pt(10)
    r_q2.font.bold = True
    r_q2.font.color.rgb = TEXT_WHITE

# Contact line at bottom of right card
tb_contact = s12.shapes.add_textbox(Inches(7.4), Inches(5.3), Inches(4.8), Inches(1.1))
tf_c = tb_contact.text_frame
tf_c.word_wrap = True
tf_c.margin_top = tf_c.margin_bottom = tf_c.margin_left = tf_c.margin_right = 0
p_c = tf_c.paragraphs[0]
r_c1 = p_c.add_run()
r_c1.text = "🌐 Веб-портал: "
r_c1.font.bold = True
r_c1.font.size = Pt(11)
r_c1.font.color.rgb = ACCENT_BLUE
r_c2 = p_c.add_run()
r_c2.text = "https://miragediary.space\n"
r_c2.font.size = Pt(11)
r_c2.font.color.rgb = TEXT_WHITE

p_c2 = tf_c.add_paragraph()
p_c2.space_before = Pt(3)
r_c3 = p_c2.add_run()
r_c3.text = "💬 Команда MirageStudio: "
r_c3.font.bold = True
r_c3.font.size = Pt(11)
r_c3.font.color.rgb = ACCENT_BLUE
r_c4 = p_c2.add_run()
r_c4.text = "Готовы ответить на любые вопросы жюри!"
r_c4.font.size = Pt(11)
r_c4.font.color.rgb = TEXT_MUTED

add_footer(s12, 12)

# Save
output_path = "presentation_miragediary.pptx"
prs.save(output_path)
print(f"Presentation saved successfully to: {os.path.abspath(output_path)}")
