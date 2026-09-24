# Полная спецификация API «Госуслуги Моя Школа» (Reverse Engineering)

> **Источник данных:** декомпиляция официального Android-клиента `ru.gosuslugi.school` (версия `4.1.1.432-appgallery`, кодовое имя `MP_SCHOOL`) через JADX. Документ содержит полную карту эндпоинтов, структур данных и протоколов взаимодействия.

---

## 1. Базовые узлы инфраструктуры

| Подсистема | Базовый URL | Назначение |
|---|---|---|
| **Шлюз ФГИС «Моя Школа»** | `https://www.gosuslugi.ru/api/myschool/` | Основной REST API (оценки, расписание, ДЗ, чеклисты, пуш-настройки) |
| **Шлюз ЕСИА OAuth2** | `https://esia.gosuslugi.ru/aas/oauth2/` | Авторизация, выдача и обмен токенов |
| **ЕСИА Доверенные устройства (TRM)** | `https://esia.gosuslugi.ru/esia-rs/api/public/` | Привязка сессий, профили `prns`, аватары `pso`, push-токены |
| **Мобильный провайдер сессий** | `https://www.gosuslugi.ru/auth-provider/mobile/` | Регистрация устройства, генерация `entToken`, тихое продление |
| **CMS и сториз Госуслуг** | `https://www.gosuslugi.ru/api/cms/` | Баннеры, сториз, FAQ, дисклеймеры |
| **НСИ (Региональные справочники)** | `https://www.gosuslugi.ru/api/nsi/` | Автоопределение региона по ЕСИА/IP, справочники школ |

---

## 2. Авторизация и жизненный цикл сессии (`MP_SCHOOL`)

### 2.1. Инициализация подписи
```http
POST https://www.gosuslugi.ru/auth-provider/mobile/sign
Content-Type: application/json
User-Agent: ru.gosuslugi.school/4.1.1.432 (Android 14; okhttp/4.12.0)

{
  "client_id": "PGU",
  "scope": ""
}
```
**Ответ:** `{ "secret": "...", "time": "2026.09.24 14:00:00 +0300", "stateMarker": "..." }`

### 2.2. Запрос кода авторизации (OAuth2)
Переход на:
```text
https://esia.gosuslugi.ru/aas/oauth2/fc?client_id=PGU&scope=http://esia.gosuslugi.ru/usr_inf?mode=w+http://esia.gosuslugi.ru/usr_sec?mode=w&response_type=code&state={stateMarker}&redirect_uri=https://www.gosuslugi.ru/tut-nichego-net
```

### 2.3. Обмен кода на токены
```http
POST https://esia.gosuslugi.ru/aas/oauth2/te
Content-Type: application/x-www-form-urlencoded

client_id=PGU&code={code}&grant_type=authorization_code&client_secret={secret}&state={stateMarker}&redirect_uri=https://www.gosuslugi.ru/tut-nichego-net&timestamp={time}
```

### 2.4. Регистрация сессии устройства (`saveToken`)
Генерируется `entToken` (Base64 JSON-метаданных `MobileEntTokenMeta`):
```json
{
  "persistent": { "device_id": "<android_id>" },
  "rnd_id": "<guid>",
  "app_name": "MP_SCHOOL",
  "phone_model": "Pixel 8",
  "os_type": "android",
  "os_version": "14"
}
```
Запрос:
```http
POST https://www.gosuslugi.ru/auth-provider/mobile/saveToken
Content-Type: application/json

{
  "id": "<android_id>",
  "userId": "<oid>",
  "name": "Pixel 8",
  "type": "Android",
  "accessToken": "<access_token>",
  "refreshToken": "<refresh_token>",
  "entToken": "<base64_ent_token>",
  "expires": 28800
}
```

### 2.5. Фоновое продление сессии (`createSession`)
Позволяет продлевать авторизацию без ввода логина и пароля:
```http
POST https://www.gosuslugi.ru/auth-provider/mobile/v2/createSession
Content-Type: application/json

{
  "id": "<android_id>",
  "userId": "<oid>",
  "name": "Pixel 8",
  "type": "Android",
  "accessToken": "<access_token>",
  "entToken": "<base64_ent_token>"
}
```

---

## 3. Ядро данных: все 12 контрактов Datamart (`POST /v1/datamart/exec`)

Все ключевые данные школы запрашиваются через пакетный эндпоинт Datamart:
```http
POST https://www.gosuslugi.ru/api/myschool/v1/datamart/exec
Content-Type: application/json
Authorization: Bearer <AccessToken>

{
  "student_id": "<student_id>",
  "year": "2026",
  "entities": [ "student_lessons", "student_marks", "student_classes" ]
}
```

### Реестр контрактов Datamart:

| # | Название контракта | Класс парсера в APK | Описание сущности |
|---|---|---|---|
| 1 | `student_lessons` | `rn.c` (`qn.c`) | **Расписание уроков:** время начала/конца, кабинет, ФИО учителя, статус замены/отмены. |
| 2 | `student_marks` | `jh.c` (`tn.c`) | **Текущие оценки:** значение, дата, вес (коэффициент), форма контроля (К/Р, С/Р, ответ), комментарий учителя. |
| 3 | `student_pmarks` | `lh.c` (`vn.d`) | **Периодические оценки:** срезы за четверти, триместры или полугодия. |
| 4 | `student_ymarks` | `lh.g` (`wn.c`) | **Итоговые оценки:** годовые оценки, экзаменационные баллы (ОГЭ/ЕГЭ) и итоговая аттестация. |
| 5 | `student_homeworks_materials` | `ru.gosuslugi.school.domain.model.homework.impl.e` | **Домашние задания:** текст ДЗ, прикрепленные файлы, ссылки на материалы ЦОК (РЭШ/МЭШ). |
| 6 | `student_classes` | `hh.c` (`cn.b`) | **Класс и школа:** класс, буква, параллель, полное и краткое наименование школы, ID организации. |
| 7 | `student_subject_plan` | `eo.h` (`f`) | **КТП (Тематический план):** темы будущих уроков на весь учебный год вперед по каждому предмету. |
| 8 | `student_events` | `jn.c` (`in.b`) | **Внеурочные события:** школьные олимпиады, кружки, секции, экскурсии, родительские собрания. |
| 9 | `student_2week_report` | `nh.c` (`bo.g`) | **2-недельный сводный отчет:** динамика оценок за 14 дней, пропуски уроков (по болезни / без уважительной причины). |
| 10 | `student_statsubject` | `ph.c` (`co.a`) | **Статистика по предметам:** общий средний балл (GPA), распределение 5/4/3/2, место в классе. |
| 11 | `student_statsubject_by_week` | `ph.g` (`ho.d`) | **Еженедельная динамика:** график изменения среднего балла по каждой неделе учебного года. |
| 12 | `student_years_classes` | `sh.c` (`rh.e`) | **Архив учебы:** полный список классов и школ ученика за все прошлые годы. |

---

## 4. Скрытые и специализированные API

### 4.1. Чеклист готовности портфеля к урокам
Приложение умеет отдавать чеклист необходимых тетрадей, учебников и специнструментов на конкретный учебный день:
```http
GET https://www.gosuslugi.ru/api/myschool/v1/checklist/{student_id}
Authorization: Bearer <AccessToken>
```

### 4.2. Похвалы и бейджи учителей (`Praise`)
Каталог системных похвал, бейджей и достижений, которые учителя выставляют ученикам:
```http
GET https://www.gosuslugi.ru/api/myschool/praise.json
```
Возвращает категории похвал, иконки, цвета бейджей и правила начисления.

### 4.3. Интеграция с голосовым помощником Яндекс «Алиса»
Официальный шлюз связки профиля школьника с навыком Алисы («Алиса, какие уроки завтра?», «Алиса, что задали?»):
- **Получение одноразового PIN-кода связки:**
  ```http
  GET https://www.gosuslugi.ru/api/myschool/api/linking/get_pin
  Authorization: Bearer <AccessToken>
  ```
- **Проверка статуса подключения:**
  ```http
  GET https://www.gosuslugi.ru/api/myschool/api/linking/
  Authorization: Bearer <AccessToken>
  ```
- **Отвязка навыка:**
  ```http
  DELETE https://www.gosuslugi.ru/api/myschool/api/linking/
  Authorization: Bearer <AccessToken>
  ```

### 4.4. Тонкая настройка Push-уведомлений
Управление тем, какие события шлет шлюз Госуслуг и в какие часы:
- **Получение текущих настроек пользователя:**
  ```http
  GET https://www.gosuslugi.ru/api/myschool/v1/push-settings?role=student
  # или role=parent
  Authorization: Bearer <AccessToken>
  ```
  *Ответ (`PushSettingsResponse`):*
  - `startTime` / `endTime` — интервал тишины;
  - `offset` — часовой пояс (например, `+05:00`);
  - `config` — массив мнемоник: `[{ "mnemonic": "MARKS", "name": "Оценки", "send": true }, { "mnemonic": "HOMEWORK", "name": "Домашние задания", "send": true }]`.

- **Включение / выключение конкретного типа пушей:**
  ```http
  POST https://www.gosuslugi.ru/api/myschool/v1/push-settings/mnemonic
  Content-Type: application/json
  Authorization: Bearer <AccessToken>

  {
    "role": "student",
    "mnemonic": "MARKS",
    "send": false
  }
  ```

- **Настройка интервала тишины:**
  ```http
  POST https://www.gosuslugi.ru/api/myschool/v1/push-settings/time
  Content-Type: application/json
  Authorization: Bearer <AccessToken>

  {
    "role": "student",
    "startTime": "08:00:00",
    "endTime": "21:00:00"
  }
  ```

### 4.5. Геолокация и родительский радар
Функционал передачи геопозиции ученика и мониторинга родителем:
- **Передача текущей геопозиции ребенка:**
  ```http
  POST https://www.gosuslugi.ru/api/myschool/v1/geolocation/{student_id}
  Content-Type: application/json
  Authorization: Bearer <AccessToken>

  {
    "latitude": 56.8389,
    "longitude": 60.6057,
    "accuracy": 15.0,
    "timestamp": 1727170000
  }
  ```
- **Запрос геопозиций всех детей (для родителя):**
  ```http
  POST https://www.gosuslugi.ru/api/myschool/v1/geolocation/all
  Authorization: Bearer <AccessToken>
  ```
- **Фиксация геозон (вход/выход из школы/дома):**
  ```http
  POST https://www.gosuslugi.ru/api/myschool/v1/geolocation/transaction
  Authorization: Bearer <AccessToken>
  ```

### 4.6. Электронный пропуск / СКУД школы (QR-код)
Генерация динамического токена для прохода через школьный турникет:
```http
POST https://www.gosuslugi.ru/api/myschool/v1/marketplace/qr
Content-Type: application/json
Authorization: Bearer <AccessToken>

{
  "student_id": "<student_id>"
}
```
**Ответ:** QR-строка и время жизни токена (TTL 60–120 секунд).

### 4.7. Расширенный профиль ЕСИА и фото (Аватар)
- **Полные данные физлица из ЕСИА (паспорт, СНИЛС, контакты):**
  ```http
  GET https://esia.gosuslugi.ru/esia-rs/api/public/v3/prns/{oid}
  Authorization: Bearer <AccessToken>
  ```
- **Официальный квадратный аватар пользователя:**
  ```http
  GET https://esia.gosuslugi.ru/esia-rs/api/public/v1/pso/{oid}/avt/square
  Authorization: Bearer <AccessToken>
  ```
  Возвращает бинарный поток JPEG/PNG фотографии из профиля Госуслуг.

### 4.8. Календарь и пользовательские события
Создание заметок, дедлайнов и событий в расписании:
- **Удаление события:**
  ```http
  DELETE https://www.gosuslugi.ru/api/myschool/v1/calendar/{studentId}/{intervalId}/{eventId}
  Authorization: Bearer <AccessToken>
  ```
- **Удаление серии повторяющихся событий:**
  ```http
  DELETE https://www.gosuslugi.ru/api/myschool/v2/calendar/{studentId}/{seriesId}/{series_edit_type}/{date}
  Authorization: Bearer <AccessToken>
  ```
- **Установка/снятие отметки «Важное событие»:**
  ```http
  POST https://www.gosuslugi.ru/api/myschool/v/calendar/important/{eventId}
  Authorization: Bearer <AccessToken>
  ```

### 4.9. Системные метаданные и регионы
- **Текущий официальный учебный год:**
  ```http
  GET https://www.gosuslugi.ru/api/myschool/v1/current_year
  ```
- **Список подключенных регионов РФ:**
  ```http
  GET https://www.gosuslugi.ru/api/myschool/v1/regions
  ```
- **Определение региона пользователя по ЕСИА:**
  ```http
  GET https://www.gosuslugi.ru/api/nsi/v1/epgu/detectRegion
  Authorization: Bearer <AccessToken>
  ```
- **Healthcheck сервиса «Моя Школа»:**
  ```http
  GET https://www.gosuslugi.ru/api/myschool/md-health
  ```
