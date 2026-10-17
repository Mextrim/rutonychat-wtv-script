# rutonychat-wtv-script

[![Release](https://img.shields.io/github/v/release/Mextrim/rutonychat-wtv-script?label=release&color=C4B5FD)](https://github.com/Mextrim/rutonychat-wtv-script/releases)
[![License](https://img.shields.io/badge/license-MIT-7C5CFF.svg)](LICENSE)
[![Для RutonyChat](https://img.shields.io/badge/RutonyChat-524660-C4B5FD.svg)](https://store.steampowered.com/app/524660/)

Мост чата W.TV для [RutonyChat](https://store.steampowered.com/app/524660/).

RutonyChat не умеет W.TV нативно: площадки нет ни в списке платформ, ни в
`SiteEnum`. Этот автономный скрипт закрывает дыру — забирает сообщения
канала и выводит их в общий чат рядом с Twitch и YouTube.

**Строго READ-ONLY:** не вступает в чат, не открывает realtime-сокет,
ни на что не подписывается. Авторизация не нужна.

![W.TV → RutonyChat](screenshot.jpg)

> Изображение — рендер на демонстрационных сообщениях, не запись реального стрима.

---

## Установка

**[⬇ Скачать wtv-script-1.0.0.zip](https://github.com/Mextrim/rutonychat-wtv-script/releases/download/v1.0.0/wtv-script-1.0.0.zip)** ·
[все релизы](https://github.com/Mextrim/rutonychat-wtv-script/releases)

### 1. Скопируйте `wtv.cs` в папку скриптов

По умолчанию Steam ставится сюда:

```
C:\Program Files (x86)\Steam\steamapps\common\RutonyChat\scripts\wtv.cs
```

Если библиотека лежит на другом диске (например, на `D:`), ищите
`steamapps\common\RutonyChat\scripts\` внутри неё.

Папка `scripts` уже есть в игре — там лежат штатные скрипты
(`auto_dices.cs`, `label_current_time.cs` и другие). Свои файлы кладите рядом с ними.

### 2. Перезапустите RutonyChat

Скрипт подхватывается при старте игры. Запускайте **только из Steam** —
при прямом запуске `.exe` приложение отказывается работать и завершается.

### 3. Укажите канал

Откройте меню **«Автономные скрипты»** — окно загружает список при открытии,
поэтому до перезапуска новый `wtv.cs` в нём не появится.

Найдите `wtv` → **«Параметры»**:

```json
{"nickname":"mextrim"}
```

`nickname` — канал на W.TV, то, что после `w.tv/` в адресе.
Открывать сайт не нужно: канал находится по нику через `profiles-service.w.tv`.

### 4. Подключите скрипт

Нажмите **«Переподключить»**.

### 5. Проверьте, что всё работает

Откройте лог:

```
<RutonyChat>/scripts/wtv.log
```

Успешный запуск выглядит так:

```
2026-10-18 04:10:45  nickname: mextrim, interval: 3s
2026-10-18 04:10:45  no join, no realtime socket, no subscription
2026-10-18 04:10:45  posting : disabled (no token - read-only)
2026-10-18 04:10:45  chatId : 0198650b-faac-728c-9f1d-2a753aa1b304
2026-10-18 04:10:46  primed with 1 existing messages
```

Строка `chatId` — канал найден, скрипт работает.
Дальше каждая новая строка `+N message(s)` означает пойманные сообщения.
При первом запуске `primed with N` — это норма: скрипт запомнил текущую
историю, чтобы не вывалить её в чат.

---

### Если скрипт не появился или не работает

| Симптом | Причина |
|---|---|
| `wtv` нет в списке автономных скриптов | Не перезапущена игра, либо `wtv.cs` лежит не в `scripts/` |
| В логе ошибка компиляции | Несовместимая версия игры — скрипт использует .NET 9 |
| Канал не находится | Опечатка в `nickname` либо канала нет |
| Сообщения не появляются в чате | Не нажмите «Переподключить» после смены параметров |
| Лог не создан | Скрипт вообще не стартовал, проверьте предыдущий пункт |

---

## Как это работает

Два анонимных запроса:

```
1) GET profiles-service.w.tv/api/v1/profiles/by-nickname/{nick}?platform=web
     -> { profile: { userId } }        userId и есть chatId

2) GET chats-service.w.tv/api/v1/chats/{chatId}/messages?limit=30&platform=web
     -> { messages: [ { messageId, type, content,
                        sender: { userId, nickname, tags[] },
                        createdAt } ] }
```

Каждые **3 секунды** забираются последние 30 сообщений. Дубликаты отсекаются
по `messageId` (буфер на 400 id). При старте скрипт запоминает последние
сообщения и не вываливает историю. При потере связи — автоперезапуск
с нарастающей паузой.

Диагностика пишется в `<RutonyChat>/scripts/wtv.log`.
Строка `+N message(s)` означает, что скрипт поймал новые сообщения.

---

## Почему REST-поллинг, а не WebSocket

Реальный транспорт W.TV — Centrifugo (`wss://ws.w.tv/connection/websocket`).
Он режет подключения из .NET по **TLS-отпечатку** и отдаёт `403` — Cloudflare
bot management отсекает не заголовки, а сам handshake. REST API не заблокирован,
поэтому скрипт опрашивает список сообщений вместо сокета.

**Задержка около 3 секунд** вместо мгновенной доставки — плата за обход.

---

## Отметка площадки в чате

`SayToWindow` не принимает `SiteEnum`, поэтому площадку штатно передать
в чат нельзя. Скрипт добавляет в никнейм невидимый маркер `\u200Dwtv\u200D`
(zero-width joiner) — на экране он не виден, но тема его распознаёт.

Тема [`minimal-chat`](https://github.com/Mextrim/rutonychat-minimal-chat)
снимает маркер и подставляет официальный логотип W.TV.

---

## Ограничения

| Ограничение | Почему |
|---|---|
| Задержка ~3 секунды | REST-поллинг вместо WebSocket |
| Нет бейджа площадки | `SayToWindow` не принимает `SiteEnum` |
| Отправка сообщений не работает | W.TV требует авторизацию для записи, анонимный POST даёт `BAD_REQUEST_ERROR` |

В коде есть заготовка отправки: если передать `token`, скрипт будет
пересылать сообщения с префиксом в W.TV.

```json
{"nickname":"mextrim","token":"...","forwardPrefix":"!wtv"}
```

Без токена мост остаётся строго read-only. На момент публикации путь отправки
не работает — W.TV отвергает любые анонимные запросы записи.

---

## Лицензия

MIT — см. [LICENSE](LICENSE).
