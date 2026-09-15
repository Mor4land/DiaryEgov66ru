import os
import qrcode
from reportlab.lib.colors import HexColor
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

# 1. Register Fonts
pdfmetrics.registerFont(TTFont('Golos-Medium', 'Golos+Text-500.ttf'))
pdfmetrics.registerFont(TTFont('Golos-SemiBold', 'Golos+Text-600.ttf'))

# 2. Dimensions & Colors
WIDTH = 960
HEIGHT = 540

C_BG = HexColor('#0F172A')
C_CARD_BG = HexColor('#1E293B')
C_CARD_BORDER = HexColor('#334155')
C_TEXT_WHITE = HexColor('#F8FAFC')
C_TEXT_MUTED = HexColor('#94A3B8')
C_ACCENT_BLUE = HexColor('#38BDF8')
C_ACCENT_INDIGO = HexColor('#818CF8')
C_ACCENT_GREEN = HexColor('#34D399')
C_ACCENT_AMBER = HexColor('#FBBF24')
C_ACCENT_PURPLE = HexColor('#C084FC')

banner_dir = os.path.abspath("MirageDiary.Server/wwwroot/images/banners")
qr_dir = os.path.abspath("presentation_assets")
os.makedirs(qr_dir, exist_ok=True)

# Generate QR codes if missing
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

output_pdf = "presentation_miragediary.pdf"
c = canvas.Canvas(output_pdf, pagesize=(WIDTH, HEIGHT))

def draw_background(c):
    c.setFillColor(C_BG)
    c.rect(0, 0, WIDTH, HEIGHT, fill=1, stroke=0)

def draw_header(c, num_str, title_str, subtitle_str=""):
    # Header tag
    c.setFont('Golos-SemiBold', 11)
    c.setFillColor(C_ACCENT_BLUE)
    tag_text = f"{num_str}  |  "
    c.drawString(55, HEIGHT - 42, tag_text)
    
    tag_w = c.stringWidth(tag_text, 'Golos-SemiBold', 11)
    c.setFont('Golos-SemiBold', 18)
    c.setFillColor(C_TEXT_WHITE)
    c.drawString(55 + tag_w, HEIGHT - 43, title_str)
    
    if subtitle_str:
        c.setFont('Golos-Medium', 10.5)
        c.setFillColor(C_TEXT_MUTED)
        c.drawString(55, HEIGHT - 62, subtitle_str)

def draw_footer(c, current_slide, total_slides=12):
    c.setStrokeColor(HexColor('#1E293B'))
    c.setLineWidth(1)
    c.line(55, 32, WIDTH - 55, 32)
    
    c.setFont('Golos-Medium', 8.5)
    c.setFillColor(C_TEXT_MUTED)
    c.drawString(55, 18, "MirageDiary • Интеллектуальная кроссплатформенная экосистема школьного дневника")
    
    c.setFont('Golos-SemiBold', 9)
    c.setFillColor(C_ACCENT_BLUE)
    counter_str = f"{current_slide:02d} / {total_slides:02d}"
    c.drawRightString(WIDTH - 55, 18, counter_str)

def draw_card(c, x, y, w, h, bg_color=C_CARD_BG, border_color=C_CARD_BORDER, r=10):
    c.setFillColor(bg_color)
    c.setStrokeColor(border_color)
    c.setLineWidth(1)
    c.roundRect(x, y, w, h, r, fill=1, stroke=1)

def draw_wrapped_text(c, text, x, y, max_w, font_name='Golos-Medium', font_size=10, text_color=C_TEXT_MUTED, leading=14):
    c.setFont(font_name, font_size)
    c.setFillColor(text_color)
    
    paragraphs = text.split('\n')
    curr_y = y
    
    for p in paragraphs:
        if not p.strip():
            curr_y -= leading * 0.6
            continue
            
        words = p.split()
        curr_line = []
        for w in words:
            test_line = ' '.join(curr_line + [w])
            if c.stringWidth(test_line, font_name, font_size) <= max_w:
                curr_line.append(w)
            else:
                c.drawString(x, curr_y, ' '.join(curr_line))
                curr_y -= leading
                curr_line = [w]
        if curr_line:
            c.drawString(x, curr_y, ' '.join(curr_line))
            curr_y -= leading
            
    return curr_y

# ==========================================
# SLIDE 1: COVER
# ==========================================
draw_background(c)

# Contest badge
c.setFillColor(HexColor('#1E3A8A'))
c.setStrokeColor(C_ACCENT_BLUE)
c.setLineWidth(1)
c.roundRect(55, HEIGHT - 55, 260, 24, 12, fill=1, stroke=1)
c.setFont('Golos-SemiBold', 8.5)
c.setFillColor(C_ACCENT_BLUE)
c.drawString(70, HEIGHT - 42, "🚀 ВСЕРОССИЙСКИЙ КОНКУРС IT-ПРОЕКТОВ 2026")

# Title
c.setFont('Golos-SemiBold', 34)
c.setFillColor(C_TEXT_WHITE)
c.drawString(55, HEIGHT - 110, "MirageDiary")

c.setFont('Golos-SemiBold', 15)
c.setFillColor(C_ACCENT_BLUE)
c.drawString(55, HEIGHT - 136, "Интеллектуальная кроссплатформенная экосистема школьного дневника")

# Description
intro_desc = (
    "Мгновенный доступ к расписанию, оценкам и домашним заданиям через Telegram,\n"
    "национальный мессенджер MAX и React MiniApp. Прямая интеграция с ФГИС «Моя Школа»\n"
    "через ЕСИА (Госуслуги) с максимальной защитой персональных данных по 152-ФЗ РФ."
)
draw_wrapped_text(c, intro_desc, 55, HEIGHT - 165, 480, font_size=10.5, text_color=C_TEXT_MUTED, leading=16)

# Feature Tags
tags = [".NET 10 Kestrel", "ЕСИА PKCE S256", "Шифрование enc:v2:", "Telegram & MAX", "152-ФЗ РФ", "Отклик <200мс"]
tx = 55
ty = HEIGHT - 245
for t in tags:
    tw = c.stringWidth(t, 'Golos-SemiBold', 8.5) + 16
    c.setFillColor(HexColor('#334155'))
    c.setStrokeColor(HexColor('#475569'))
    c.roundRect(tx, ty, tw, 20, 10, fill=1, stroke=1)
    c.setFont('Golos-SemiBold', 8.5)
    c.setFillColor(C_TEXT_WHITE)
    c.drawString(tx + 8, ty + 5, t)
    tx += tw + 8

# Stat Metrics Card
draw_card(c, 55, 65, 480, 75)
items_stat = [
    ("< 200 мс", "Скорость отклика", C_ACCENT_GREEN),
    ("100%", "Соответствие 152-ФЗ", C_ACCENT_BLUE),
    ("0 паролей", "Zero-Knowledge", C_ACCENT_AMBER),
    ("x20+", "Экономия трафика", C_ACCENT_PURPLE)
]
sx = 75
for val, lbl, col in items_stat:
    c.setFont('Golos-SemiBold', 13)
    c.setFillColor(col)
    c.drawString(sx, 110, val)
    c.setFont('Golos-Medium', 8)
    c.setFillColor(C_TEXT_MUTED)
    c.drawString(sx, 85, lbl)
    sx += 115

# Right side: Banner Image & Live box
banner_menu = os.path.join(banner_dir, "banner_menu.png")
if os.path.exists(banner_menu):
    c.drawImage(banner_menu, 565, HEIGHT - 290, width=340, height=164, mask='auto')

draw_card(c, 565, 65, 340, 125)
c.setFont('Golos-SemiBold', 10.5)
c.setFillColor(C_ACCENT_BLUE)
c.drawString(585, 165, "🌐 Боевой продакшен запущен:")

c.setFont('Golos-SemiBold', 14)
c.setFillColor(C_TEXT_WHITE)
c.drawString(585, 140, "https://miragediary.space")

draw_wrapped_text(c, 
    "• Сервер: ASP.NET Core 10 Kestrel (Linux-x64, SSL)\n"
    "• Telegram-бот: @MirageDiary_bot\n"
    "• Бот в мессенджере MAX и адаптивный React MiniApp",
    585, 115, 305, font_size=8.5, text_color=C_TEXT_MUTED, leading=13)

draw_footer(c, 1)
c.showPage()

# ==========================================
# SLIDE 2: PROBLEM
# ==========================================
draw_background(c)
draw_header(c, "02", "Проблема и рыночный контекст", "Ключевые барьеры использования существующих государственных школьных систем")

probs = [
    ("🐢 Медленная загрузка порталов", 
     "Время полной загрузки веб-страницы регионального дневника достигает 8–15 секунд. В школьных коридорах при слабом мобильном интернете портал часто зависает и не успевает открыться до звонка на урок.",
     C_ACCENT_AMBER),
    ("💥 Пиковые перегрузки серверов", 
     "В 07:45–08:30 (перед занятиями) и 17:00–19:00 (проверка ДЗ) региональные шлюзы испытывают лавинообразные всплески трафика, отдавая пользователям ошибки 502/504 Bad Gateway.",
     HexColor('#F87171')),
    ("🧩 Сложная навигация и стресс", 
     "Многократный ввод логинов, громоздкие неадаптивные таблицы, отсутствие встроенного подсчета среднего балла для четверти и невозможность быстро узнать время до конца урока.",
     C_ACCENT_INDIGO),
    ("📱 Изолированность от мессенджеров", 
     "Школьники и родители проводят до 90% экранного времени в Telegram и MAX, но вынуждены переключаться в сторонний браузер с постоянным разлогиниванием сессий.",
     C_ACCENT_BLUE)
]

card_w = 405
card_h = 185
coords_2x2 = [
    (55, HEIGHT - 265),
    (500, HEIGHT - 265),
    (55, HEIGHT - 480),
    (500, HEIGHT - 480)
]

for idx, (p_title, p_body, col) in enumerate(probs):
    cx, cy = coords_2x2[idx]
    draw_card(c, cx, cy, card_w, card_h)
    c.setFont('Golos-SemiBold', 12.5)
    c.setFillColor(col)
    c.drawString(cx + 20, cy + card_h - 32, p_title)
    draw_wrapped_text(c, p_body, cx + 20, cy + card_h - 60, card_w - 40, font_size=9.5, text_color=C_TEXT_MUTED, leading=15)

draw_footer(c, 2)
c.showPage()

# ==========================================
# SLIDE 3: SOLUTION
# ==========================================
draw_background(c)
draw_header(c, "03", "Решение: Экосистема MirageDiary", "Легковесный, отказоустойчивый шлюз к образовательным данным прямо в мессенджерах")

pillars = [
    ("⚡ Мгновенный отклик (< 200 мс)",
     "Высокоэффективное кэширование и компактная отдача. Расписание, оценки и ДЗ открываются за доли секунды без расхода трафика и без задержек.",
     C_ACCENT_GREEN),
    ("🤖 Мультитранспортная среда",
     "Единое ядро обслуживает пользователей Telegram, отечественного мессенджера MAX и веб-клиента React MiniApp без дублирования бизнес-логики.",
     C_ACCENT_BLUE),
    ("🔔 Умные контекстные сервисы",
     "Таймер звонков со сменами, калькулятор оценок (/calc), автоматический анализ предметов на завтра и контроль сбора рюкзака в 20:30.",
     C_ACCENT_AMBER),
    ("🔗 Бесшовная связка аккаунтов",
     "Мгновенный переход Telegram ⟷ MAX по 6-значному коду с сохранением сессии Госуслуг и настроек будильника без повторной авторизации.",
     C_ACCENT_PURPLE)
]

for idx, (p_title, p_body, col) in enumerate(pillars):
    cx, cy = coords_2x2[idx]
    draw_card(c, cx, cy, card_w, card_h)
    c.setFont('Golos-SemiBold', 12.5)
    c.setFillColor(col)
    c.drawString(cx + 20, cy + card_h - 32, p_title)
    draw_wrapped_text(c, p_body, cx + 20, cy + card_h - 60, card_w - 40, font_size=9.5, text_color=C_TEXT_MUTED, leading=15)

draw_footer(c, 3)
c.showPage()

# ==========================================
# SLIDE 4: ARCHITECTURE & TECH STACK
# ==========================================
draw_background(c)
draw_header(c, "04", "Архитектура и технологический стек", "Высоконагруженное распределенное ядро на .NET 10 и реактивные клиенты")

arch_items = [
    ("⚙️ Серверное ядро (.NET 10 / C# 14)",
     "• ASP.NET Core 10 Kestrel: компиляция Release под Linux-x64\n"
     "• Entity Framework Core 10, фоновые воркеры BackgroundService\n"
     "• Защита исходного кода через Obfuscar 2.2\n"
     "• Автономные манифесты сборки Docker и docker-compose.yml",
     C_ACCENT_INDIGO),
    ("🛡️ Безопасность и интеграции",
     "• Шлюз ЕСИА: протокол OAuth 2.0 PKCE с хэшированием S256\n"
     "• Конвертное шифрование токенов enc:v2: (AES-256-GCM + DEK)\n"
     "• Встроенный валидатор RussianTrustedCertificateValidator\n"
     "• Очистка памяти секретов CryptographicOperations.ZeroMemory",
     C_ACCENT_BLUE),
    ("💾 База данных и хранилище",
     "• Встроенная база SQLite 3 в режиме высокой скорости WAL\n"
     "• Права доступа к файлу базы 0600 (только процессу приложения)\n"
     "• Служба DatabaseBackupService: горячие бэкапы каждые 6 часов\n"
     "• Пул SharedClassroomCache для кэширования расписания классов",
     C_ACCENT_GREEN),
    ("📱 Клиентские интерфейсы",
     "• Telegram.Bot 22.9.2 с лонг-поллингом и вебхук-адаптером\n"
     "• MAX Messenger: кастомный асинхронный REST/Webhook клиент\n"
     "• MiniApp: React 19, TypeScript, Vite, темная дизайн-система\n"
     "• Динамические баннеры высокой четкости (1200x580)",
     C_ACCENT_AMBER)
]

for idx, (a_title, a_body, col) in enumerate(arch_items):
    cx, cy = coords_2x2[idx]
    draw_card(c, cx, cy, card_w, card_h)
    c.setFont('Golos-SemiBold', 12.5)
    c.setFillColor(col)
    c.drawString(cx + 20, cy + card_h - 32, a_title)
    draw_wrapped_text(c, a_body, cx + 20, cy + card_h - 58, card_w - 40, font_size=9, text_color=C_TEXT_WHITE, leading=14)

draw_footer(c, 4)
c.showPage()

# ==========================================
# SLIDE 5: SECURITY & 152-FZ
# ==========================================
draw_background(c)
draw_header(c, "05", "Информационная безопасность и 152-ФЗ", "Эшелонированная криптографическая защита и строгое соблюдение законодательства РФ")

sec_items = [
    ("🔐 Конвертное шифрование enc:v2:",
     "• Master KEK (Key Encryption Key): 256-битный ключ из защищенного окружения.\n"
     "• DEK (Data Encryption Key): уникальный ключ на каждую запись в базе.\n"
     "• Симметричное шифрование AES-256-GCM с 96-битным IV и 128-битным тегом.\n"
     "• Зануление ключей в памяти RAM через ZeroMemory(dek) сразу после вычисления.",
     C_ACCENT_BLUE),
    ("🔑 Zero-Knowledge для паролей",
     "• Пароли пользователей от портала Госуслуг никогда не вводятся и не проходят через ботов.\n"
     "• Авторизация полностью делегирована официальному контуру ЕСИА (OAuth2 PKCE S256).\n"
     "• Сервер получает только временный токен сессии дневника с ограниченными правами.",
     C_ACCENT_GREEN),
    ("📜 Согласие и аудит (152-ФЗ)",
     "• Обязательный онбординг: функции бота заблокированы до подтверждения согласия родителей (ст. 9 ч. 6).\n"
     "• Аудит волеизъявления: фиксация точного UTC-таймштампа в неизменяемом журнале.\n"
     "• Право на забвение: команда /delete_account безвозвратно стирает все токены и кэши.",
     C_ACCENT_AMBER),
    ("🇷🇺 Суверенитет и сертификаты РФ",
     "• 100% данных и серверов размещены на территории Российской Федерации (ст. 18 ч. 5).\n"
     "• Встроенные корневые сертификаты Минцифры РФ (Russian Trusted CAs).\n"
     "• Полная независимость от зарубежных облачных CDN и закрытых API.",
     C_ACCENT_PURPLE)
]

for idx, (s_title, s_body, col) in enumerate(sec_items):
    cx, cy = coords_2x2[idx]
    draw_card(c, cx, cy, card_w, card_h)
    c.setFont('Golos-SemiBold', 12.5)
    c.setFillColor(col)
    c.drawString(cx + 20, cy + card_h - 32, s_title)
    draw_wrapped_text(c, s_body, cx + 20, cy + card_h - 58, card_w - 40, font_size=9, text_color=C_TEXT_WHITE, leading=14)

draw_footer(c, 5)
c.showPage()

# ==========================================
# SLIDE 6: CORE UX
# ==========================================
draw_background(c)
draw_header(c, "06", "Ключевая функциональность и UX", "Эргономичный интерфейс, разработанный под реальные сценарии школьной жизни")

col_w3 = 265
col_h3 = 395
col_xs3 = [55, 347, 640]

features_data = [
    ("📅 Расписание и «До звонка»", 
     "banner_now.png",
     "• Мгновенный таймер (/now): сколько минут осталось до конца текущего урока или перемены.\n\n"
     "• Поддержка 1-й и 2-й смен (сетка звонков МАОУ СОШ № 113).\n\n"
     "• Индикация номеров кабинетов, ФИО учителей и статуса замен уроков.",
     C_ACCENT_BLUE),
    ("📊 Оценки и калькулятор (/calc)", 
     "banner_marks.png",
     "• Наглядный расчет среднего балла по всем предметам с цветовой подсветкой риска.\n\n"
     "• Калькулятор целей: алгоритм рассчитывает, сколько именно «5» или «4» нужно получить.\n\n"
     "• Детализация четвертных срезов и весов контрольных работ.",
     C_ACCENT_GREEN),
    ("⏰ Умный будильник (SmartAlarm)", 
     "banner_alarm.png",
     "• 07:30: Утренний дайджест с расписанием и кабинетом первого урока.\n\n"
     "• 17:00: Напоминание о невыполненных ДЗ на завтра.\n\n"
     "• 20:30: Анализ предметов на завтра и напоминание собрать рюкзак (форма, ИЗО, циркуль).",
     C_ACCENT_AMBER)
]

for idx, (f_title, b_name, f_body, col) in enumerate(features_data):
    cx = col_xs3[idx]
    cy = HEIGHT - 475
    draw_card(c, cx, cy, col_w3, col_h3)
    
    # Image
    img_path = os.path.join(banner_dir, b_name)
    if os.path.exists(img_path):
        c.drawImage(img_path, cx + 15, cy + col_h3 - 135, width=col_w3 - 30, height=115, mask='auto')
        
    c.setFont('Golos-SemiBold', 11.5)
    c.setFillColor(col)
    c.drawString(cx + 15, cy + col_h3 - 155, f_title)
    
    draw_wrapped_text(c, f_body, cx + 15, cy + col_h3 - 180, col_w3 - 30, font_size=8.5, text_color=C_TEXT_MUTED, leading=13)

draw_footer(c, 6)
c.showPage()

# ==========================================
# SLIDE 7: FAULT TOLERANCE
# ==========================================
draw_background(c)
draw_header(c, "07", "Отказоустойчивость и производительность", "Архитектурные оптимизации для стабильной работы в часы пиковых нагрузок")

perf_items = [
    ("🚀 SharedClassroomCache",
     "Вместо сотен параллельных запросов от учеников одного класса при открытии дневника перед уроком, сервер опрашивает ФГИС «Моя Школа» ровно 1 раз. Структура расписания кэшируется в оперативной памяти.\n\n"
     "Итог: Снижение нагрузки на внешний шлюз более чем в 20 раз и выдача ответа ученику за 15–30 мс.",
     C_ACCENT_GREEN),
    ("🔧 Автодетекция регламентных работ",
     "Государственные информационные системы регулярно проводят ночные и утренние обновления. Интеллектуальный шлюз MirageDiary анализирует статус-коды и HTML-ответы.\n\n"
     "Итог: Бот не падает с ошибками, а выводит понятное сообщение «🔧 Идут техработы» и показывает закэшированное расписание.",
     C_ACCENT_AMBER),
    ("🔄 Фоновое обновление сессий (TokenRefresh)",
     "Специализированный сервис TokenRefreshBackgroundService производит упреждающее обновление access-токенов в фоне до истечения их срока действия.\n\n"
     "Итог: Пользователю не приходится ждать 2–3 секунды на рефреш токена при вводе команды — бот отвечает мгновенно.",
     C_ACCENT_BLUE),
    ("🛡️ Устойчивый режим Kestrel & SQLite WAL",
     "Пул потоков .NET 10 оптимизирован под асинхронную обработку тысяч вебхук-событий в секунду без блокировок. База данных SQLite работает в режиме WAL (Write-Ahead Logging).\n\n"
     "Итог: Параллельные чтения расписания сотнями учеников не блокируют фоновые записи логов и настроек.",
     C_ACCENT_INDIGO)
]

for idx, (p_title, p_body, col) in enumerate(perf_items):
    cx, cy = coords_2x2[idx]
    draw_card(c, cx, cy, card_w, card_h)
    c.setFont('Golos-SemiBold', 12.5)
    c.setFillColor(col)
    c.drawString(cx + 20, cy + card_h - 32, p_title)
    draw_wrapped_text(c, p_body, cx + 20, cy + card_h - 58, card_w - 40, font_size=9, text_color=C_TEXT_WHITE, leading=14)

draw_footer(c, 7)
c.showPage()

# ==========================================
# SLIDE 8: IMPORT SUBSTITUTION
# ==========================================
draw_background(c)
draw_header(c, "08", "Импортозамещение и суверенная среда", "Полная готовность к работе в национальном цифровом контуре Российской Федерации")

imp_items = [
    ("🇷🇺 Интеграция с мессенджером MAX",
     "• Полнофункциональный бот для национальной коммуникационной платформы MAX.\n"
     "• Поддержка нативных интерактивных клавиатур, callback-роутинга и событий.\n"
     "• Гарантия доступности сервиса при любых сценариях сбоев зарубежных каналов.",
     C_ACCENT_BLUE),
    ("🔗 Сквозное связывание Telegram ⟷ MAX",
     "• Сервис AccountLinkingService генерирует одноразовый 6-значный криптографический PIN.\n"
     "• Пользователь вводит PIN во втором мессенджере — сессия Госуслуг и настройки объединяются.\n"
     "• Единый профиль без необходимости проходить повторную авторизацию на Госуслугах.",
     C_ACCENT_GREEN),
    ("🔒 Доверенные сертификаты Минцифры РФ",
     "• Интеграция сертификатов Минцифры России на уровне базового Docker-образа и Kestrel.\n"
     "• Кастомный валидатор RussianTrustedCertificateValidator для проверки цепочек TLS.\n"
     "• Стабильное соединение со шлюзами ЕСИА и ФГИС «Моя Школа».",
     C_ACCENT_AMBER),
    ("🏢 Локальная инфраструктура и безопасность",
     "• Развертывание на отечественных серверных мощностях под управлением Linux.\n"
     "• Полный контроль над кодом: защита интеллектуальной собственности обфускацией Obfuscar.\n"
     "• Отсутствие зарубежных проприетарных SaaS-решений в цепочке обработки данных.",
     C_ACCENT_PURPLE)
]

for idx, (i_title, i_body, col) in enumerate(imp_items):
    cx, cy = coords_2x2[idx]
    draw_card(c, cx, cy, card_w, card_h)
    c.setFont('Golos-SemiBold', 12.5)
    c.setFillColor(col)
    c.drawString(cx + 20, cy + card_h - 32, i_title)
    draw_wrapped_text(c, i_body, cx + 20, cy + card_h - 58, card_w - 40, font_size=9, text_color=C_TEXT_WHITE, leading=14)

draw_footer(c, 8)
c.showPage()

# ==========================================
# SLIDE 9: MVP STATUS
# ==========================================
draw_background(c)
draw_header(c, "09", "Текущие результаты и статус MVP", "Проект доведен до боевого состояния и успешно функционирует на продакшен-сервере")

draw_card(c, 55, HEIGHT - 475, 410, 395)
c.setFont('Golos-SemiBold', 14)
c.setFillColor(C_ACCENT_GREEN)
c.drawString(75, HEIGHT - 115, "🏆 Достигнутые результаты")

achiev_text = (
    "✅ Боевой продакшен на https://miragediary.space\n"
    "    Развернут на Linux VPS с настроенным SSL, Nginx и Kestrel.\n\n"
    "✅ Два активных бота (Telegram & MAX)\n"
    "    Поддерживают полный цикл: авторизация, расписание, оценки, ДЗ.\n\n"
    "✅ 100% готовность к требованиям конкурса (п. 9.7)\n"
    "    • Собран автономный Dockerfile и docker-compose.yml\n"
    "    • Подготовлен requirements.txt с пакетами .NET 10\n"
    "    • Синхронизированы порты проверки работоспособности (5000)\n\n"
    "✅ Автоматизированное тестирование (test-all.ps1)\n"
    "    API выдерживает конкурентные запросы без утечек памяти."
)
draw_wrapped_text(c, achiev_text, 75, HEIGHT - 145, 370, font_size=9.5, text_color=C_TEXT_WHITE, leading=14.5)

draw_card(c, 495, HEIGHT - 475, 410, 395)
c.setFont('Golos-SemiBold', 14)
c.setFillColor(C_ACCENT_BLUE)
c.drawString(515, HEIGHT - 115, "📦 Открытые репозитории")

repos_text = (
    "📂 Бэкенд (.NET 10 Web API + Боты):\n"
    "    github.com/Miragestudio-repo/MirageDiary_Backend\n"
    "    (Чистая архитектура, EF Core 10, обфускация Obfuscar, тесты)\n\n"
    "📂 Фронтенд (React MiniApp):\n"
    "    github.com/Miragestudio-repo/MirageDiary-Miniapp\n"
    "    (TypeScript, Vite, темная адаптивная дизайн-система)\n\n"
    "🐳 Инфраструктура в Docker:\n"
    "    Multi-stage сборка на базе ASP.NET 10 Noble с предустановленными сертификатами Минцифры РФ и запуском от non-root пользователя app."
)
draw_wrapped_text(c, repos_text, 515, HEIGHT - 145, 370, font_size=9.5, text_color=C_TEXT_WHITE, leading=14.5)

draw_footer(c, 9)
c.showPage()

# ==========================================
# SLIDE 10: ROADMAP
# ==========================================
draw_background(c)
draw_header(c, "10", "Дорожная карта развития (Roadmap)", "Стратегический план масштабирования экосистемы на 2026–2027 учебный год")

col_w4 = 195
col_xs4 = [55, 270, 485, 700]

roadmap_steps = [
    ("Q4 2026 (Пилот)",
     "• Пилотный запуск в 5 школах Свердловской области (СОШ № 113 и партнеры).\n\n"
     "• Подключение первых 1500+ активных учеников и родителей.\n\n"
     "• Сбор отзывов и точная калибровка расписания звонков.",
     C_ACCENT_BLUE),
    ("Q1 2027 (Календари)",
     "• Экспорт расписания и сроков сдачи ДЗ по протоколам iCal / WebCal.\n\n"
     "• Автосинхронизация с Яндекс Календарем, Apple и Google Calendar.\n\n"
     "• Еженедельный дайджест успеваемости для родителей.",
     C_ACCENT_GREEN),
    ("Q2 2027 (ИИ-помощник)",
     "• Интеграция локальной языковой модели для помощи в разборе сложных тем ДЗ.\n\n"
     "• Автоматическая суммаризация параграфов учебников.\n\n"
     "• Генератор тренировочных тестов перед контрольными.",
     C_ACCENT_AMBER),
    ("Q3 2027 (Масштаб)",
     "• Подключение новых регионов РФ, интегрированных с «Моя Школа».\n\n"
     "• Виджеты расписания для рабочих столов iOS и Android.\n\n"
     "• Аналитический дашборд для завучей и учителей.",
     C_ACCENT_PURPLE)
]

for idx, (rm_t, rm_b, col) in enumerate(roadmap_steps):
    cx = col_xs4[idx]
    cy = HEIGHT - 475
    draw_card(c, cx, cy, col_w4, 395)
    
    # Badge
    c.setFillColor(HexColor('#1E3A8A'))
    c.setStrokeColor(col)
    c.roundRect(cx + 12, cy + 395 - 40, col_w4 - 24, 24, 6, fill=1, stroke=1)
    c.setFont('Golos-SemiBold', 9)
    c.setFillColor(col)
    c.drawCentredString(cx + col_w4/2, cy + 395 - 25, rm_t)
    
    draw_wrapped_text(c, rm_b, cx + 15, cy + 395 - 65, col_w4 - 30, font_size=8.5, text_color=C_TEXT_WHITE, leading=13)

draw_footer(c, 10)
c.showPage()

# ==========================================
# SLIDE 11: TEAM
# ==========================================
draw_background(c)
draw_header(c, "11", "Команда проекта и компетенции", "Сбалансированная инженерная экспертиза для масштабирования продукта")

team_items = [
    ("💻 Backend & Security Architect",
     "• Архитектура высоконагруженного модульного ядра на .NET 10 (C# 14)\n\n"
     "• Разработка криптографического протокола enc:v2: (AES-256-GCM + DEK)\n\n"
     "• Интеграция мобильного шлюза ЕСИА (OAuth2 PKCE S256) и ФГИС «Моя Школа»\n\n"
     "• Обеспечение юридического и технического соответствия 152-ФЗ",
     C_ACCENT_BLUE),
    ("🎨 Frontend & UI/UX Engineer",
     "• Проектирование эргономичной дизайн-системы для школьников и родителей\n\n"
     "• Разработка кроссплатформенного React MiniApp (TypeScript + Vite)\n\n"
     "• Создание системы визуальных баннеров высокого разрешения (1200x580)\n\n"
     "• In-place интерфейсы ботов Telegram и MAX без визуального спама",
     C_ACCENT_INDIGO),
    ("🚀 Product Manager & QA / DevOps",
     "• Проработка сценариев умного будильника и сетки школьных звонков\n\n"
     "• Автоматизация CI/CD, оптимизация Docker-сборок, администрирование Linux\n\n"
     "• Нагрузочное тестирование Kestrel и профилирование базы данных SQLite\n\n"
     "• Взаимодействие со школьной администрацией и проведение пилотов",
     C_ACCENT_GREEN)
]

for idx, (t_title, t_body, col) in enumerate(team_items):
    cx = col_xs3[idx]
    cy = HEIGHT - 475
    draw_card(c, cx, cy, col_w3, col_h3)
    
    c.setFont('Golos-SemiBold', 11.5)
    c.setFillColor(col)
    c.drawString(cx + 15, cy + col_h3 - 35, t_title)
    
    draw_wrapped_text(c, t_body, cx + 15, cy + col_h3 - 65, col_w3 - 30, font_size=9, text_color=C_TEXT_WHITE, leading=14)

draw_footer(c, 11)
c.showPage()

# ==========================================
# SLIDE 12: CONCLUSION & CONTACTS
# ==========================================
draw_background(c)
draw_header(c, "12", "Заключение и контакты", "MirageDiary — современный стандарт взаимодействия со школьными данными")

# Left Column: Value summary
draw_card(c, 55, HEIGHT - 475, 450, 395)
c.setFont('Golos-SemiBold', 14)
c.setFillColor(C_ACCENT_BLUE)
c.drawString(75, HEIGHT - 115, "🌟 Почему MirageDiary побеждает:")

concl_text = (
    "• Реальный, уже работающий продукт, развернутый на https://miragediary.space\n\n"
    "• Безопасность уровня enterprise: конвертное шифрование enc:v2:, Zero-Knowledge авторизация без сохранения паролей Госуслуг и 100% соблюдение 152-ФЗ.\n\n"
    "• Сверхбыстрый отклик (< 200 мс) и снижение нагрузки на государственные серверы в 20+ раз благодаря SharedClassroomCache.\n\n"
    "• Готовность к импортозамещению: поддержка отечественного мессенджера MAX и российских сертификатов Минцифры.\n\n"
    "• Полная открытость кода, Docker-контейнеризация и готовность к масштабированию."
)
draw_wrapped_text(c, concl_text, 75, HEIGHT - 145, 410, font_size=9.5, text_color=C_TEXT_WHITE, leading=15)

# Right Column: QR Codes & Live Contacts
draw_card(c, 530, HEIGHT - 475, 375, 395)
c.setFont('Golos-SemiBold', 13)
c.setFillColor(C_ACCENT_GREEN)
c.drawString(550, HEIGHT - 115, "📱 Протестируйте прямо сейчас:")

# QR 1: Telegram Bot
qr_bot_path = os.path.join(qr_dir, "qr_bot.png")
if os.path.exists(qr_bot_path):
    c.drawImage(qr_bot_path, 560, HEIGHT - 275, width=130, height=130, mask='auto')
    c.setFont('Golos-SemiBold', 9.5)
    c.setFillColor(C_TEXT_WHITE)
    c.drawCentredString(625, HEIGHT - 290, "Telegram-бот")
    c.setFont('Golos-Medium', 8.5)
    c.setFillColor(C_ACCENT_BLUE)
    c.drawCentredString(625, HEIGHT - 302, "@MirageDiary_bot")

# QR 2: GitHub Backend
qr_git_path = os.path.join(qr_dir, "qr_github.png")
if os.path.exists(qr_git_path):
    c.drawImage(qr_git_path, 730, HEIGHT - 275, width=130, height=130, mask='auto')
    c.setFont('Golos-SemiBold', 9.5)
    c.setFillColor(C_TEXT_WHITE)
    c.drawCentredString(795, HEIGHT - 290, "GitHub Бэкенда")
    c.setFont('Golos-Medium', 8.5)
    c.setFillColor(C_ACCENT_BLUE)
    c.drawCentredString(795, HEIGHT - 302, "MirageDiary_Backend")

# Contact Line
c.setStrokeColor(HexColor('#334155'))
c.line(550, HEIGHT - 325, 885, HEIGHT - 325)

c.setFont('Golos-SemiBold', 10.5)
c.setFillColor(C_TEXT_WHITE)
c.drawString(550, HEIGHT - 350, "🌐 Веб-портал: https://miragediary.space")

c.setFont('Golos-Medium', 9)
c.setFillColor(C_TEXT_MUTED)
c.drawString(550, HEIGHT - 375, "Команда MirageStudio")
c.drawString(550, HEIGHT - 395, "Готовы ответить на любые вопросы экспертного жюри!")

draw_footer(c, 12)
c.showPage()

# Save
c.save()
print(f"Vector PDF presentation generated successfully: {os.path.abspath(output_pdf)}")
