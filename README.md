# AutoCAD HTTP Server

C#-плагин для **AutoCAD 2020–2024** (собирается с AutoCAD 2021 API), реализующий локальный HTTP-сервер
непосредственно внутри процесса AutoCAD.

**Установка без инсталлятора и без прав администратора:** скачайте `AcadHttpServer-manual-install-v<версия>.zip`
из последнего [Release](../../releases/latest), распакуйте и запустите `install.cmd` — пакет
`AcadHttpServer.bundle` копируется в `%APPDATA%\Autodesk\ApplicationPlugins` и загружается AutoCAD автоматически
(см. [«Ручная установка (AutoCAD 2020–2024)»](#ручная-установка-autocad-20202024)).

Проект предоставляет двунаправленный JSON IPC-транспорт и локальные статические ресурсы Grist-виджетов без внешнего Python/Flask-сервера.

## Цель проекта

Проверить архитектуру:

```text
Внешнее приложение
       │
       │ HTTP / JSON
       ▼
127.0.0.1:5000
       │
       ▼
AutoCAD HTTP Server
       │
       ▼
C# AutoCAD Plugin
       │
       ▼
AutoCAD .NET API
```

HTTP-сервер запускается и останавливается командами AutoCAD.

## Целевая платформа

* **AutoCAD 2020, 2021, 2022, 2023, 2024** (R23.1–R24.3)
* **.NET Framework 4.8**
* **x64**
* C#
* AutoCAD .NET API

DLL компилируется с библиотеками AutoCAD 2021 (R24.0) — используемые API есть во всех версиях 2020–2024,
а сборки AutoCAD API не имеют строгого имени, поэтому одна DLL загружается во всех этих версиях.
AutoCAD 2020 сам требует .NET Framework 4.7 — для плагина нужен .NET Framework 4.8 (входит в Windows 10
версии 1903 и новее). AutoCAD 2025 и новее работают на .NET 8 и этим пакетом не поддерживаются.

Используются библиотеки AutoCAD:

```text
AcMgd.dll
AcDbMgd.dll
AcCoreMgd.dll
```

## Команды AutoCAD

### HTTPSTART

Запускает локальный HTTP-сервер:

```text
HTTPSTART
```

По умолчанию сервер слушает (адрес и порт задаются в `AutoCADHttp.settings.json` рядом с DLL):

```text
127.0.0.1:5000
```

### HTTPSTOP

Останавливает HTTP-сервер:

```text
HTTPSTOP
```

### HTTPSTATUS

Показывает текущее состояние HTTP-сервера:

```text
HTTPSTATUS
```

Пример:

```text
[HTTP] HTTP Server: RUNNING
[HTTP] Address: 127.0.0.1
[HTTP] Port: 5000
[HTTP] Started: 2026-10-03 21:15:04 (uptime 0:02:31)
[HTTP] URL: http://127.0.0.1:5000/ping
[HTTP] Incoming IPC queued: 0
[HTTP] Outgoing IPC: RUNNING http://127.0.0.1:5001/ipc (queued: 0)
[HTTP] IPC commands registered: 3; event subscriptions: 2; commands awaiting responses: 0
[HTTP] Requests served: 3
```

Все сообщения плагина выводятся в командную строку AutoCAD с префиксом `[HTTP]`:

```text
Команда: HTTPSTART
[HTTP] Server started: http://127.0.0.1:5000/ (test: http://127.0.0.1:5000/ping)
Команда: HTTPSTART
[HTTP] Server is already running on http://127.0.0.1:5000/ - a second server was not started.
Команда: HTTPSTOP
[HTTP] Server stopped. Port 5000 released.
Команда: HTTPSTOP
[HTTP] Server is not running.
```

Если порт занят другой программой:

```text
[HTTP] ERROR: cannot start server on 127.0.0.1:5000: port 5000 is already in use by another program [SocketError.AddressAlreadyInUse]
```

## Тестовый API

После выполнения:

```text
HTTPSTART
```

доступен endpoint:

```text
GET http://127.0.0.1:5000/ping
```

Пример ответа:

```json
{
  "status": "ok",
  "application": "AutoCAD",
  "version": "2021"
}
```

`version` — год запущенного AutoCAD (`2020`, `2021`, `2022`, `2023`, `2024`), определяется по
`Application.Version` (R23.1, R24.0–R24.3); для других выпусков — номер вида `R25.0`.

Проверить можно непосредственно через браузер (открыть `http://127.0.0.1:5000/ping`) или через `curl` (встроен в Windows 10/11):

```text
> curl -i http://127.0.0.1:5000/ping
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Content-Length: 56
Cache-Control: no-store
Connection: close

{"status":"ok","application":"AutoCAD","version":"2021"}
```

PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:5000/ping
```

Готовые скрипты: [`examples/check-ping.cmd`](examples/check-ping.cmd), [`examples/check-ping.ps1`](examples/check-ping.ps1).

После `HTTPSTOP` порт закрыт, соединение отклоняется:

```text
> curl http://127.0.0.1:5000/ping
curl: (7) Failed to connect to 127.0.0.1 port 5000 ... Could not connect to server
```

Другие ответы:

| Запрос | Ответ |
|---|---|
| `GET` / `HEAD /ping` | `200` + JSON выше |
| `POST /ping` и др. методы | `405 Method Not Allowed` |
| `POST /ipc` с JSON | `202 Accepted` + `id`, сообщение в очереди |
| `GET` / `HEAD /widgets/...` | список содержимого каталога или явно указанный файл |
| неправильный метод `/ipc` или `/widgets` | `405 Method Not Allowed` |
| любой другой путь | `404 Not Found` |
| заголовок `Host` не локальный / не настроенный адрес | `403 Forbidden` (защита от DNS rebinding) |
| некорректный запрос | `400 Bad Request` |

## IPC и Grist Widgets (этап 2)

```text
127.0.0.1:5000
├── GET /ping           проверка сервера
├── POST /ipc           JSON → Incoming Queue → Application.Idle → Dispatcher
└── GET /widgets/...    локальные статические файлы

AutoCAD / Application → Outgoing Queue → HTTP Client → External /ipc
```

HTTP-поток только проверяет конверт и ставит его в потокобезопасную очередь. Обработчики команд
регистрируются в `IpcDispatcher`; плагин вызывает `Drain(32)` из `Application.Idle` в главном потоке.
Новые команды добавляются без изменения HTTP-сервера. Встроенный `PING` — только демонстрация;
`LINE`, `INSERT_DEV`, остальные операции с DWG на этом этапе не реализованы.

### Настройка

Файл [`AutoCADHttp.settings.json`](src/AutoCADHttp/AutoCADHttp.settings.json) должен лежать **рядом с
`AutoCADHttp.dll`**. Сборка, CI-артефакт и ZIP из Release включают готовый шаблон:

```json
{
  "address": "127.0.0.1",
  "port": 5000,
  "widgetsDirectory": "C:\\zcad\\GristWidgets\\widget",
  "JSONView": 0,
  "externalIpc": ""
}
```

* `address` — локальный IP-адрес: например `127.0.0.1`, `127.0.0.2` или `::1`. `localhost` означает
  `127.0.0.1`. Привязка к внешним интерфейсам и `0.0.0.0` по-прежнему запрещена.
* `port` — целое число от `1` до `65535`, по умолчанию `5000`.
* `widgetsDirectory` — абсолютный путь или путь относительно папки DLL, например `widgets`.
  В JSON обратные слеши Windows нужно удваивать; можно использовать `C:/zcad/GristWidgets/widget`.
  Пустая строка или `null` отключают статические ресурсы (`/widgets` возвращает `404`).
* `JSONView` — `1`: каждый JSON, пришедший на `/ipc`, — и корректный, и некорректный — дополнительно
  выводится в командную строку AutoCAD без изменений, в порядке поступления; `0` (по умолчанию, в том
  числе если поле отсутствует) — не выводится. Допустимы только целые `0` и `1`, иное значение — ошибка
  настроек, сервер не запускается. Формат строк — см. [«Просмотр JSON (`JSONView`)»](#просмотр-json-jsonview).
  Вывод идёт через очередь `CommandLineLog.Post()` и печатается в `Application.Idle`: HTTP-поток не
  обращается к AutoCAD API и не ждёт вывода, ответы сервера и передача в `IpcDispatcher` одинаковы при `0` и `1`.
  Текущее значение печатается при `HTTPSTART`. Не зависит от `ACADHTTP_VERBOSE` (журнал HTTP-запросов).
* `externalIpc` — необязательный URL внешнего приложения для исходящих сообщений: абсолютный `http`/`https`
  URL с путём ровно `/ipc`, без query и fragment, например `http://127.0.0.1:5001/ipc`. Пустая строка, `null`
  или отсутствие поля — исходящий транспорт не настроен: входящий сервер работает как обычно, а `TrySend`,
  `PublishEvent` возвращают `false` с записью в журнал (`SendCommandAsync` завершается `IpcTransportException`).
  Неверный URL — ошибка настроек, как и для остальных полей. Переменная окружения `ACADHTTP_EXTERNAL_IPC`,
  если задана, имеет приоритет над полем. Источник итогового URL печатается при `HTTPSTART`.

Папка текущего чертежа, рабочая папка AutoCAD и расположение `acad.exe` не влияют на поиск файла или
относительный путь виджетов. Сохраните файл как JSON в UTF-8. Путь настроек виден при загрузке плагина
и в `HTTPSTATUS`. Настройки читаются при каждом запуске остановленного сервера: после изменения файла
выполните `HTTPSTOP`, затем `HTTPSTART`. Повторный `HTTPSTART` работающего сервера оставляет его настройки
без изменений. Ошибки JSON, адреса, порта или пути выводятся с именем файла; сервер не запускается.

Если файла нет, используются прежние `127.0.0.1:5000` и `ACADHTTP_WIDGETS_DIR`. Отсутствующие поля
сохраняют эти значения; поле `widgetsDirectory`, если присутствует, имеет приоритет над переменной
окружения. При обновлении DLL сохраните собственный файл настроек, прежде чем распаковывать шаблон.

Дополнительные настройки транспорта остаются в переменных окружения; задайте их **до запуска AutoCAD**
(или консольного хоста):

```powershell
$env:ACADHTTP_EXTERNAL_IPC = 'http://127.0.0.1:5001/ipc'
$env:ACADHTTP_VERBOSE = '1' # необязательно: журнал HTTP-запросов и исходящей доставки
```

* `ACADHTTP_WIDGETS_DIR` — совместимость с прежней настройкой каталога: используется, когда файл JSON
  или поле `widgetsDirectory` отсутствует. Относительный путь этой переменной, как раньше, считается от рабочей папки.
* `ACADHTTP_EXTERNAL_IPC` — URL внешнего HTTP(S)-сервера, путь строго `/ipc`; имеет приоритет над полем
  `externalIpc` файла настроек. Если URL не задан или неверен, входящий сервер продолжает работать,
  исходящий транспорт отключён. Итоговые ответы команд требуют настроенного внешнего сервера.
* По умолчанию подробный журнал выключен. Ошибки доставки и тайм-ауты выводятся всегда через
  очередь `CommandLineLog`, без вызова AutoCAD API из HTTP-потока.

`HTTPSTART` читает настройки, запускает сервер и исходящий worker; `HTTPSTOP` закрывает входящие соединения
и отменяет исходящий HTTP без ожидания сети в главном потоке. Ожидающие исходящие сообщения отбрасываются,
а все команды, ожидающие ответа (`SendCommandAsync`), сразу завершаются `IpcTransportStoppedException`
(в журнале: `IPC transport stopped by HTTPSTOP. N pending IPC command(s) failed.`). Регистрации команд и
подписки модулей сохраняются. Уже принятые входящие сообщения сохраняются и будут обработаны после
следующего `HTTPSTART`. Сервер не запускается при `NETLOAD`. При выгрузке плагина (выход из AutoCAD)
сервис останавливает транспорт, завершает ожидающие команды и освобождает все регистрации.

### Конверты и ответы

```json
{"id":"123","type":"command","command":"PING","parameters":{}}
{"id":"123","type":"response","status":"ok","result":{}}
{"id":"124","type":"event","event":"OBJECT_CREATED","parameters":{}}
```

`id`, `type`, имя команды/события — непустые строки. `parameters` и успешный `result` — JSON-объекты;
их содержимое транспорт не интерпретирует. Типы: `command`, `response`, `event`. Поле `id` коррелирует
команду с итоговым ответом. Ответы и события передаются приложению через `Dispatcher.MessageReceived`
в том же контексте, без автоматического ответа (чтобы не создавать цикл обратной связи).

Пример запроса:

```powershell
Invoke-RestMethod http://127.0.0.1:5000/ipc -Method Post -ContentType 'application/json' -Body '{"id":"123","type":"command","command":"PING","parameters":{}}'
```

Немедленный HTTP-ответ — `202 Accepted`:

```json
{"id":"123","type":"response","status":"ok","result":{"queued":true}}
```

Это подтверждение постановки в очередь. Итоговый ответ `type: response` с тем же `id` отправляется
отдельно на внешний `/ipc`, когда AutoCAD становится idle. Неизвестная команда даёт:

```json
{"id":"123","type":"response","status":"error","error":{"code":"UNKNOWN_COMMAND"}}
```

Некорректный JSON/конверт даёт HTTP `400`, не попадает в очередь и не останавливает сервер:

```json
{"type":"response","status":"error","error":{"code":"INVALID_JSON"}}
```

Чтобы увидеть, что именно пришло и почему оно отклонено, включите `JSONView` = `1` (см. ниже).

Исключение обработчика превращается в коррелированный `COMMAND_FAILED`; следующие сообщения продолжают
обрабатываться. `Content-Type` должен быть `application/json` (`415` иначе), тело — UTF-8 с
`Content-Length` (`411` без длины). Chunked-запросы не поддерживаются (`400`), `Expect: 100-continue`
поддерживается. Размер тела ограничен 1 MiB (`413`), глубина JSON — 32; тайм-аут входящего запроса — 5 с.

#### Просмотр JSON (`JSONView`)

При `JSONView` = `1` каждый запрос к `/ipc` печатается в командной строке одной записью — тело запроса
выводится точно так, как оно получено (форматирование, порядок ключей, значения не меняются):

```text
[HTTP] IPC JSON: {"id":"1","type":"command","command":"PING","parameters":{}}
[HTTP] IPC JSON INVALID (400 INVALID_JSON: Missing required field "parameters".): {"id":"2","type":"command","command":"PING"}
[HTTP] IPC JSON INVALID (400 INVALID_JSON: Invalid IPC JSON at line 6, column 1 (offset 77): unexpected '}'.): {
  "id": "3",
  "type": "command",
  "command": "PING",
  "parameters": {},
}
[HTTP] IPC JSON INVALID (400 INVALID_JSON: parameters must be a JSON object, not string.): {"id":"4","type":"command","command":"PING","parameters":"{}"}
[HTTP] IPC JSON INVALID (400 INVALID_JSON: Invalid IPC JSON at line 1, column 2 (offset 1): unexpected '''.): {'id':'5','type':'command','command':'PING','parameters':{}}
[HTTP] IPC JSON INVALID (400 INVALID_JSON: Invalid IPC JSON at line 1, column 1 (offset 0): unexpected character U+FEFF.): ﻿{"id":"6","type":"command","command":"PING","parameters":{}}
[HTTP] IPC JSON INVALID (400 INVALID_JSON: type must be "command", "response" or "event", not "request".): {"id":"7","type":"request","command":"PING","parameters":{}}
[HTTP] IPC JSON INVALID (400 Bad Request: Request body must be UTF-8: invalid byte 0xCF at offset 23): {"id":"cp1251","text":"������"}
[HTTP] IPC JSON INVALID (415 INVALID_CONTENT_TYPE: Content-Type is 'text/plain', use application/json): {"id":"1","type":"command","command":"PING","parameters":{}}
[HTTP] IPC JSON INVALID (405 METHOD_NOT_ALLOWED: GET /ipc, use POST): <empty body>
[HTTP] IPC JSON INVALID (413 Payload Too Large: Request body too large): <body not read>
```

* `IPC JSON: <тело>` — сообщение принято (`202`) и поставлено в очередь; строки идут в порядке очереди.
* `IPC JSON INVALID (<HTTP-код> <код ошибки>: <причина>): <тело>` — сообщение отклонено и в очередь не
  попало. Причина указывает, что исправить: отсутствующее или пустое поле и его неверный тип (`id`, `type`,
  `command`/`event`, `parameters`, `status`, `result`, `error.code`), недопустимое значение `type`/`status`,
  дублирующееся поле, а для синтаксической ошибки — строку, столбец, смещение и неожиданный символ (например,
  BOM `U+FEFF`, перевод строки внутри строки, одинарные кавычки, лишняя запятая, данные после JSON).
  Тело не в UTF-8 (например, Windows-1251) выводится с `U+FFFD` вместо неверных байтов и с первым неверным
  байтом и его смещением (в примере — «Привет» в Windows-1251). BOM в начале тела не виден, но указан
  в причине как `U+FEFF`. Если тело не было прочитано (`411`, `413`, чужой `Host` → `403`), вместо него —
  `<body not read>`; пустое тело — `<empty body>`.

HTTP-коды и коды ошибок в ответах (`INVALID_JSON`, `INVALID_CONTENT_TYPE`) от `JSONView` не зависят.
Примеры выше получены скриптом `python3 experiments/jsonview-readme-samples.py` (после сборки `StandaloneHost`).
В коде: `ApiRouter.JsonView` получает готовую строку; ошибки, найденные самим сервером до маршрутизации,
передаются через `LocalHttpServer.RequestRejected = router.ReportRejected`.

Исходящая очередь ограничена 1024 сообщениями. `TryEnqueue`/`QueueOutgoing` возвращает немедленно;
переполнение или остановка даёт `false` и запись в журнал. Один фоновый consumer отправляет сообщения
по порядку. Тайм-аут — 5 с; ошибка соединения, HTTP-ошибка или тайм-аут записывается в журнал, затем
обрабатывается следующее сообщение. Автоматического повтора нет: команда могла уже выполниться
на внешнем сервере. Поэтому каждое сообщение отправляется по новому соединению (`Connection: close`, без пула):
повторное использование соединения, которое внешний сервер как раз закрывает (например, HTTP/1.0
`http.server` Python), теряло бы следующее сообщение с ошибкой «response ended prematurely».
HTTP Client не выполняет AutoCAD API.

### Общий IPC-сервис для модулей (`IIpcService`)

В процессе AutoCAD один сервис — `HttpServerPlugin.Ipc` (`IIpcService`). Модули не создают свои HTTP-серверы,
HTTP Client и очереди: входящие сообщения приходят через единственный `POST /ipc`, исходящие уходят через
единственную ограниченную очередь `IpcOutbox`. Регистрация не зависит от `HTTPSTART`: модуль может
зарегистрироваться при своей загрузке, регистрации переживают `HTTPSTOP`/`HTTPSTART`.

```csharp
// В IExtensionApplication.Initialize модуля (поле _ipc — для Dispose в Terminate).
_ipc = new IpcModule(HttpServerPlugin.Ipc, "Devices");

// Входящая команда. Имена уникальны во всём процессе и сравниваются без учёта регистра.
_ipc.RegisterCommand("DEVICE_INFO", message =>
{
    // Обработчик выполняется в Application.Idle (главный поток) и не ждёт сети.
    _ipc.SendCommandAsync("GRISTGET_DEVICE", message.ParametersJson, TimeSpan.FromSeconds(10))
        .ContinueWith(t => _ipc.PublishEvent("DEVICE_INFO.COMPLETED", t.Status == TaskStatus.RanToCompletion
            ? t.Result.ResultJson ?? "{}" : "{}"), TaskScheduler.Default);
    return IpcMessage.CreateResponse(message.Id, "{\"accepted\":true}");
});

// Событие: подписчиков может быть несколько, ответ на события не отправляется.
_ipc.SubscribeEvent("PROJECT.UPDATED", message => { /* ... */ });

// Исходящее событие с новым уникальным id; true — поставлено в очередь (не «доставлено»).
bool queued = _ipc.PublishEvent("DEVICE.CREATED", "{\"deviceId\":5}");

// В IExtensionApplication.Terminate модуля: снимает только регистрации этого модуля.
_ipc.Dispose();
```

Без `IpcModule` те же операции доступны напрямую: `HttpServerPlugin.Ipc.RegisterCommand("Devices", "DEVICE_INFO", handler)`
и `SubscribeEvent("Devices", "PROJECT.UPDATED", handler)` возвращают `IDisposable`; его `Dispose` удаляет ровно эту
регистрацию, поэтому один модуль не может снять обработчики другого.

* **Регистрация команд.** Повторная регистрация занятого имени (в том числе другим модулем или в другом регистре)
  выбрасывает `InvalidOperationException` с именем модуля-владельца: `IPC command 'DEVICE_INFO' is already
  registered by module 'Devices'`. Тихой замены нет. Обработчик возвращает ответ с тем же `id`
  (`IpcMessage.CreateResponse`/`CreateError`); исключение превращается в `COMMAND_FAILED`, неизвестная команда — в
  `UNKNOWN_COMMAND`, следующие сообщения обрабатываются как обычно.
* **События.** `SubscribeEvent(module, event, handler)` — имя без учёта регистра, `"*"` — все события. Вызываются все
  подписчики; исключение одного записывается в журнал и не мешает остальным. Автоматических ответов на события нет.
* **Исходящие сообщения.** `TrySend(message)` и `PublishEvent(event, parametersJson)` возвращаются сразу:
  `true` — сообщение в очереди, `false` — транспорт не настроен, остановлен или очередь переполнена (с записью в журнал).
* **Исходящие команды.** `SendCommandAsync(command, parametersJson, timeout, cancellationToken)` создаёт уникальный `id`
  и возвращает `Task<IpcMessage>`, который завершается ответом внешнего приложения (`type: response` с тем же `id`,
  `status` `ok` или `error`), пришедшим на `POST /ipc`. Ответы с неизвестным `id` (в том числе опоздавшие после
  тайм-аута) игнорируются. Задача никогда не ждёт бесконечно:
  * `TimeoutException` — по истечении `timeout` (по умолчанию 30 с);
  * отмена через `cancellationToken`;
  * `IpcTransportException` — команду не удалось поставить в очередь (транспорт не настроен или очередь заполнена);
  * `IpcTransportStoppedException` — `HTTPSTOP`, перезапуск транспорта или выгрузка плагина.

**Потоки.** HTTP-поток только разбирает конверт и ставит его в очередь. Обработчики команд, подписчики событий и
сопоставление ответов выполняются в `Drain(32)` из `Application.Idle` в главном потоке. Продолжения задачи
`SendCommandAsync` никогда не выполняются синхронно внутри `Drain`, поэтому не блокируют AutoCAD; не вызывайте
`.Result`/`.Wait()` в главном потоке. Продолжение выполняется в пуле потоков: для AutoCAD API из него нужно
вернуться в главный поток (например, через собственную очередь, читаемую в `Application.Idle`). Выполнение в
Idle само по себе не делает работу с чертежом безопасной: обработчики, изменяющие DWG, сами берут
`Document.LockDocument()` и открывают транзакции.

**Совместимость.** `HttpServerPlugin.Dispatcher.Register(command, handler)` и `HttpServerPlugin.QueueOutgoing(message)`
продолжают работать (регистрация от имени модуля `AutoCADHttp`), но `Register` теперь возвращает `IDisposable` и
выбрасывает `InvalidOperationException` для уже занятого имени вместо тихой замены обработчика.

Проверка без AutoCAD: [`examples/external-ipc-app.py`](examples/external-ipc-app.py) — пример внешнего приложения
(отвечает на `GRISTGET_DEVICE`, отправляет команды и события), [`experiments/verify-ipc-service.py`](experiments/verify-ipc-service.py)
запускает консольный хост с демонстрационными модулями `Devices` и `Reports` и проверяет весь сценарий
(выполняется в CI).

### Статические ресурсы Grist

`/widgets/` показывает содержимое настроенного каталога, а вложенные пути с `/` — содержимое
соответствующих папок. Файлы и папки представлены ссылками; папки отмечены завершающим `/`.
Вложенный список содержит ссылку `../` для возврата, а корневой список не ведёт выше каталога виджетов.
Сервер не подставляет `index.html`: для открытия HTML-виджета нужно явно указать имя файла.
Например, для дерева `widget/managerGRIST/{index.html, manager.js, style.css}`, `widget/catalog/`
и `widget/common/`:

```text
GET /widgets/                        → catalog/, common/, managerGRIST/
GET /widgets/managerGRIST/            → index.html, manager.js, style.css
GET /widgets/managerGRIST/index.html  → файл index.html
```

Наличие `index.html` не меняет список, а пустая папка возвращает пустой список (`200`). `/widgets`
и вложенные каталоги без завершающего `/` перенаправляют (`308`) на путь с `/`, сохраняя query string,
чтобы относительные ссылки работали. Несуществующая папка или файл возвращает `404`.

HTML, JS, CSS, изображения и шрифты возвращаются с подходящим MIME-типом; бинарные файлы сохраняются
без перекодирования. Поддерживаются вложенные каталоги, query string и `HEAD`. Лимит файла — 8 MiB.
Пути за пределы корневого каталога, `..`, обратные слеши, альтернативные NTFS-потоки и ссылки/junctions
отклоняются; ссылки/junctions не включаются в список. Имена экранируются в HTML и кодируются в URL,
в том числе пробелы и Unicode.

Демонстрация дерева из примера без AutoCAD (Enter останавливает сервер):

```text
dotnet build examples/StandaloneHost -c Release
python3 examples/widget-browser.py
```

Откройте `/widgets/` на локальном адресе, напечатанном хостом. Перейдите в `managerGRIST/`, затем
откройте ссылку `index.html`. Скрипт создаёт временные файлы и удаляет их после остановки.


В Grist можно указать URL HTML-виджета. Его JavaScript обращается к `/ipc` на том же origin:

```javascript
fetch('/ipc', {
  method: 'POST',
  headers: {'Content-Type': 'application/json'},
  body: JSON.stringify({id: crypto.randomUUID(), type: 'command', command: 'PING', parameters: {}})
});
```

`/widgets` обслуживает только ресурсы; прикладные команды проходят через `/ipc`. Grist API,
WebSocket, подписки, авторизация и операции с DWG на этом этапе не добавлены.

### Сквозная проверка без AutoCAD

```powershell
$env:ACADHTTP_EXTERNAL_IPC = 'http://127.0.0.1:5001/ipc'
dotnet run --project examples/StandaloneHost -- 5000 'C:\zcad\GristWidgets\widget'
```

В другом терминале запустите `python examples/check-ipc.py`. Скрипт поднимает локальный внешний `/ipc`,
посылает `PING` и неизвестный `LINE`, проверяет `202`, коррелированные обратные ответы и `/ping`.
Python нужен только для этого примера проверки.

Автономная проверка с временным каталогом и свободными портами:

```text
dotnet build examples/StandaloneHost -c Release
python3 experiments/verify-stage2.py
python3 experiments/verify-settings.py
python3 experiments/verify-jsonview.py
python3 experiments/verify-ipc-service.py
```

Первая проверка также проверяет списки каталогов, явно указанные HTML-файлы, бинарное изображение и приём
события, затем останавливает хост.
Вторая запускает копию хоста с JSON рядом с DLL из другой рабочей папки и проверяет заданные адрес/порт,
относительный каталог виджетов, IPC и отказ запуска при ошибке настроек. Консольный хост читает тот же JSON
рядом с `StandaloneHost.dll`; его прежние аргументы порта, каталога и внешнего `/ipc` имеют приоритет.
Третья проверяет `JSONView` = `1`, `0`, отсутствие поля и недопустимое значение: при `1` вывод исходного JSON
по порядку — и принятого, и отклонённого с причиной (синтаксис, конверт, Windows-1251 вместо UTF-8, неверный
`Content-Type`), при `0` и без поля — отсутствие вывода, а также одинаковые HTTP-ответы и обработку `PING`.
Четвёртая проверяет общий IPC-сервис с двумя демонстрационными модулями и внешним приложением
[`examples/external-ipc-app.py`](examples/external-ipc-app.py): отказ дублирующей регистрации, `SendCommandAsync` с
ответом по тому же `id`, событие двум подписчикам без ответа, `UNKNOWN_COMMAND`, тайм-аут и немедленное завершение
ожидающей команды при остановке транспорта.

## Архитектура

HTTP-сервер работает непосредственно внутри процесса AutoCAD.

```text
                    AutoCAD
                       │
                       │ NETLOAD
                       ▼
              ┌─────────────────┐
              │  AutoCADHttp.dll│
              │                 │
              │  HTTPSTART      │
              │  HTTPSTOP       │
              │  HTTPSTATUS     │
              └────────┬────────┘
                       │
                       ▼
       TcpListener (127.0.0.1) + HTTP/1.1
                       │
                       ▼
                127.0.0.1:5000
                       │
                 HTTP / JSON
```

HTTP-сервер не блокирует основной поток AutoCAD:

* `HTTPSTART` / `HTTPSTOP` только открывают / закрывают сокет и сразу возвращают управление;
* входящие соединения принимаются в отдельном фоновом потоке (`IsBackground = true`);
* запросы обрабатываются асинхронно (`ReadAsync` / `WriteAsync`) в пуле потоков, на каждое соединение — тайм-аут 5 с;
* обработчик `/ping` не обращается к AutoCAD API, поэтому безопасно выполняется вне главного потока;
* сообщения из фоновых потоков (ошибки) ставятся в очередь и печатаются в командную строку из события
  `Application.Idle`, т.е. в главном потоке AutoCAD (`Editor.WriteMessage` нельзя вызывать из других потоков).

### Почему `TcpListener`, а не `HttpListener`

`HttpListener` в .NET Framework работает через драйвер Windows `http.sys`. Для процесса без прав администратора
регистрация префикса `http://127.0.0.1:5000/` требует резервирования URL
(`netsh http add urlacl url=http://127.0.0.1:5000/ user=...`), иначе `HttpListener.Start()` падает с
`Access is denied`. Кроме того, `http.sys` сопоставляет префиксы по заголовку `Host`, а не по сетевому интерфейсу.

`TcpListener`, привязанный к `IPAddress.Loopback` (`127.0.0.1`), не требует прав администратора, `netsh` и
настройки брандмауэра, и физически доступен только с локального компьютера. Поверх него реализован минимальный
HTTP/1.1 (строка запроса + заголовки, ответ с `Content-Length` и `Connection: close`) — этого достаточно для
`/ping`, JSON IPC и статических ресурсов.

### Структура проекта

```text
AutoCADHttp.sln
src/AutoCADHttp/
  AutoCADHttp.csproj         net48, x64; ссылки на AcMgd/AcDbMgd/AcCoreMgd
  HttpServerPlugin.cs        IExtensionApplication + команды HTTPSTART/HTTPSTOP/HTTPSTATUS
  AcadRelease.cs             номер выпуска AutoCAD (R23.1…R24.3) → год 2020…2024 для /ping
  AutoCADHttp.settings.json  адрес, порт, каталог виджетов и JSONView; рядом с DLL в выходной папке
  CommandLineLog.cs          потокобезопасный вывод в командную строку (через Application.Idle)
  Http/LocalHttpServer.cs    HTTP-сервер на настроенном loopback-адресе (не зависит от AutoCAD)
  Http/ServerSettings.cs     чтение и проверка JSON относительно папки DLL
  Http/ApiRouter.cs          /ping, POST /ipc, GET /widgets
  Http/IpcMessage.cs         неизменяемые JSON-конверты
  Http/IpcJson.cs            ограниченный JSON parser без дополнительных DLL
  Http/IpcDispatcher.cs      команды модулей (уникальные имена) и подписки на события, вызывается в Drain
  Http/IIpcService.cs        общий IPC-сервис для модулей (HttpServerPlugin.Ipc)
  Http/IpcService.cs         реализация: входящая очередь, SendCommandAsync, жизненный цикл транспорта
  Http/IpcModule.cs          регистрации одного модуля с общим Dispose
  Http/IpcTransportException.cs  ошибки транспорта для SendCommandAsync
  Http/IpcOutbox.cs          исходящая очередь + фоновый HTTP Client
  Http/WidgetFiles.cs        файлы из настроенного каталога
  Http/HttpMessages.cs       модели запроса/ответа
tests/AutoCADHttp.Tests/     xUnit-тесты HTTP-слоя (без AutoCAD)
installer_acad/
  PackageContents.xml        описание AcadHttpServer.bundle для автозагрузчика AutoCAD 2020–2024
installer_manual/            install.cmd, uninstall.cmd, INSTALL.md — корень архива ручной установки
scripts/
  package-release.ps1        AutoCADHttp.zip, AcadHttpServer-manual-install-v<версия>.zip, release-manifest.txt
  test-release-package.ps1   проверка состава архивов и PackageContents.xml
  test-manual-install.ps1    install.cmd / uninstall.cmd на временном %APPDATA% (Windows)
examples/                    скрипты проверки /ping, консольный хост сервера без AutoCAD
```

HTTP-слой (`src/AutoCADHttp/Http`) не ссылается на AutoCAD API — его можно тестировать и запускать отдельно.

HTTP-запросы используются только как транспорт. Работа с объектами AutoCAD должна выполняться обработчиками в корректном контексте приложения после чтения очереди.

## План развития

Транспорт и очередь уже реализованы. Следующий этап — прикладные обработчики команд:

```text
HTTP
  │
  ▼
JSON
  │
  ▼
IPC / Command Queue
  │
  ▼
AutoCAD Main Thread
  │
  ├── LINE
  ├── CIRCLE
  ├── POLYLINE
  ├── TEXT
  ├── MTEXT
  ├── BLOCKINSERT
  └── INSERT_DEV
```

В перспективе HTTP-сервер должен быть отделён от конкретной структуры команд AutoCAD.

HTTP-слой отвечает только за:

* HTTP;
* JSON;
* приём запросов;
* отправку ответов;
* передачу команд в очередь.

Обработчики команд отвечают за взаимодействие с AutoCAD API.

## Сборка

Проект собирается под:

```text
.NET Framework 4.8
Platform: x64
Configuration: Release
```

Для компиляции необходимы библиотеки AutoCAD 2021:

```text
AcMgd.dll
AcDbMgd.dll
AcCoreMgd.dll
```

Эти библиотеки должны соответствовать установленной версии AutoCAD. Проект находит их так:

1. Если AutoCAD 2021 установлен в `C:\Program Files\Autodesk\AutoCAD 2021` — используются DLL из установки.
   Другой путь можно указать свойством `AcadDir`:
   `dotnet build -c Release -p:AcadDir="D:\Autodesk\AutoCAD 2021"`.
2. Иначе используются официальные NuGet-пакеты Autodesk для AutoCAD 2021 — `AutoCAD.NET`, `AutoCAD.NET.Core`,
   `AutoCAD.NET.Model` версии **24.0.0** («AutoCAD 2021 .Net API»). Это позволяет собирать DLL без установленного
   AutoCAD (например, в CI).

В обоих случаях DLL AutoCAD **не копируются** в выходную папку (`Private=false` / `ExcludeAssets=runtime`) —
AutoCAD использует свои собственные.

### Visual Studio 2022

Открыть `AutoCADHttp.sln`, выбрать конфигурацию `Release | x64`, выполнить *Build → Build Solution*.

### Командная строка

```text
dotnet build AutoCADHttp.sln -c Release
dotnet test  AutoCADHttp.sln -c Release
```

Результат: `src\AutoCADHttp\bin\Release\AutoCADHttp.dll` и `AutoCADHttp.settings.json` в той же папке.

### Готовая DLL (GitHub Releases)

После каждого `push` в основную ветку GitHub Actions
([`.github/workflows/build-release.yml`](.github/workflows/build-release.yml)) автоматически:

```text
Checkout → Restore NuGet (AutoCAD.NET 24.0.0) → Build Release / x64 → тесты → AutoCADHttp.dll
         → scripts/package-release.ps1 → проверка архивов и install.cmd / uninstall.cmd
         → GitHub Release vX.Y.N (Assets ниже)
```

| Файл в Assets | Назначение |
| --- | --- |
| `AcadHttpServer-manual-install-vX.Y.N.zip` | **ручная установка в профиль пользователя** для AutoCAD 2020–2024, без инсталлятора и прав администратора; инструкция `INSTALL.md` внутри |
| `AutoCADHttp.zip` | `AutoCADHttp.dll` и `AutoCADHttp.settings.json` для загрузки командой `NETLOAD` из любой папки |
| `release-manifest.txt` | контрольные суммы SHA256 архивов |

* Версия формируется автоматически: `v0.0.<номер запуска workflow>` (`v0.0.1`, `v0.0.2`, …); менять версию
  перед commit не нужно. Та же версия записывается в `AutoCADHttp.dll` (`AssemblyVersion`/`FileVersion`),
  commit SHA — в `InformationalVersion`. Номер запуска уникален, поэтому два одновременных запуска не получат
  одну версию; номера неудачных сборок пропускаются. Новую серию (например, `v0.1.N`) можно начать, изменив
  `VERSION_PREFIX` в workflow.
* `AutoCADHttp.zip` содержит `AutoCADHttp.dll` и `AutoCADHttp.settings.json` в корне — `AcMgd.dll`, `AcDbMgd.dll`, `AcCoreMgd.dll` и другие
  DLL Autodesk не включаются (workflow проверяет это и завершается ошибкой, если в выходной папке есть лишние DLL).
* `AcadHttpServer-manual-install-vX.Y.N.zip` повторяет структуру профиля пользователя — см.
  [«Ручная установка (AutoCAD 2020–2024)»](#ручная-установка-autocad-20202024). `AppVersion` в
  `PackageContents.xml` совпадает с версией Release.
* В описании Release: таблица файлов, версия, commit SHA, дата сборки, конфигурация `Release / x64`,
  платформа `AutoCAD 2020–2024 / .NET Framework 4.8`.
* AutoCAD API берётся только из NuGet (`-p:UseInstalledAcad=false`). Если пакеты недоступны, шаг
  *Restore NuGet packages* завершается ошибкой с понятным сообщением.
* Если сборка, тесты или проверки не прошли — Release не создаётся, ошибка видна во вкладке *Actions*.
  Ассеты загружаются в черновик Release, который публикуется последним шагом, поэтому неполный Release не появляется.
* Запуск вручную: *Actions → Build & Release → Run workflow*. Release создаётся только при запуске из основной
  ветки; для других веток собирается только артефакт workflow.

Workflow [`.github/workflows/build.yml`](.github/workflows/build.yml) проверяет pull request'ы: собирает DLL и
запускает тесты на Windows (.NET Framework 4.8 и .NET 8) и Linux (.NET 8), собирает те же архивы, проверяет их
состав, на Windows выполняет `install.cmd` / повторную установку / `uninstall.cmd` во временный `%APPDATA%`, а на
Linux — негативные проверки (`experiments/verify-release-package.py`). Локально (нужен PowerShell 7 или Windows
PowerShell 5.1):

```text
dotnet build AutoCADHttp.sln -c Release
pwsh scripts/package-release.ps1 -Version 0.0.0          # → package/
pwsh scripts/test-release-package.ps1 -Version 0.0.0
pwsh scripts/test-manual-install.ps1 -Version 0.0.0      # только Windows
```

## Тесты

`tests/AutoCADHttp.Tests` — xUnit-тесты HTTP-слоя, работают без AutoCAD (на Windows — для net48 и net8.0,
на Linux — для net8.0). Проверяется:

* `GET /ping` возвращает `200` и JSON `{"status":"ok","application":"AutoCAD","version":"2021"}`;
* номер выпуска AutoCAD R23.1–R24.3 сопоставляется с годом 2020–2024 (`AcadReleaseTests`);
* сервер не запускается сам по себе (только `Start()`), порт по умолчанию — `5000`, адрес — `127.0.0.1`;
* после `Stop()` подключение к порту отклоняется (`ConnectionRefused`);
* повторный `Start()` возвращает `AlreadyRunning`, не бросает исключений и не создаёт второй сервер;
* перезапуск на том же порту после `Stop()`;
* занятый порт → понятная ошибка `AddressAlreadyInUse`, сервер остаётся остановленным;
* сервер недоступен по внешнему IP-адресу компьютера (только loopback);
* `Start()` / `Stop()` возвращают управление сразу; простаивающие соединения не мешают другим запросам и
  закрываются по тайм-ауту / при `Stop()`;
* 50 параллельных запросов; 404 / 405 / 400 / 403; ошибка обработчика → `500` + сообщение в журнал;
* сквозная проверка на реальном порту `127.0.0.1:5000`;
* IPC command/response/event, валидация JSON, UTF-8 и фрагментация тела, 50 параллельных команд;
* `JSONView`: вывод принятого и отклонённого JSON с причиной (поле, строка/столбец, неверный байт UTF-8),
  порядок вывода, одинаковые ответы при `0` и `1`;
* выполнение диспетчера в потоке вызывающего приложения, корреляция и изоляция ошибок;
* исходящий HTTP на реальный локальный peer, тайм-аут/недоступность/HTTP-ошибка, ограничение очереди и отмена;
* списки корневого/вложенного/пустого каталога, явный `index.html`, ссылки и экранирование имён;
* HTML/JS/CSS/изображения/шрифты, бинарный GET и HEAD, HEAD списка, изоляция путей и символических ссылок.
* JSON рядом с DLL, значения по умолчанию, относительные/абсолютные пути, UTF-8, ошибки полей,
  повторное чтение настроек, пользовательский IPv4/IPv6 loopback-адрес и защита `Host`.

Проверка без AutoCAD вручную — консольный хост того же HTTP-слоя:

```text
dotnet run --project examples/StandaloneHost
curl http://127.0.0.1:5000/ping
```

## Ручная установка (AutoCAD 2020–2024)

В каждом [Release](../../releases) есть архив `AcadHttpServer-manual-install-v<версия>.zip`. Он устанавливается
**без прав администратора** — все файлы размещаются только в профиле пользователя (`%APPDATA%`):
AutoCAD — `%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle`, плагин загружается автоматически.

```text
AcadHttpServer-manual-install-v<версия>.zip
├── install.cmd                копирует AppData\Roaming в %APPDATA% (настройки при обновлении сохраняются)
├── uninstall.cmd              удаляет %APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle
├── INSTALL.md                 подробная инструкция
└── AppData\Roaming\Autodesk\ApplicationPlugins\AcadHttpServer.bundle
    ├── PackageContents.xml    AutoCAD 2020–2024 (SeriesMin R23.1, SeriesMax R24.3), Win64, LoadOnAutoCADStartup
    └── Contents\AutoCAD2020-2024
        ├── AutoCADHttp.dll
        └── AutoCADHttp.settings.json
```

1. Закрыть AutoCAD, разблокировать скачанный ZIP (*Свойства → Разблокировать*) и распаковать его.
2. Запустить `install.cmd` (или вручную скопировать папку `AppData` архива в `%USERPROFILE%` со слиянием папок).
3. Запустить AutoCAD 2020–2024 — плагин загрузится сам (`AutoCADHttp loaded (AutoCAD 2023)…` в командной строке).
   При предупреждении безопасности выбрать «Всегда загружать» или добавить папку пакета в `TRUSTEDPATHS`.
4. Выполнить `HTTPSTART` и открыть `http://127.0.0.1:5000/ping`.

Настройки: `%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\Contents\AutoCAD2020-2024\AutoCADHttp.settings.json`
(путь также выводит `HTTPSTATUS`). Обновление — повторить шаги 1–2 с новым архивом; удаление — `uninstall.cmd`.
Полная инструкция — [`installer_manual/INSTALL.md`](installer_manual/INSTALL.md); описание пакета —
[`installer_acad/PackageContents.xml`](installer_acad/PackageContents.xml).

## Загрузка в AutoCAD

1. Собрать проект (или скачать `AutoCADHttp.zip` из последнего [Release](../../releases/latest) и распаковать).
   Оставить `AutoCADHttp.settings.json` рядом с DLL и при необходимости задать адрес, порт и каталог виджетов.
2. Запустить AutoCAD 2020–2024.
3. Выполнить:

```text
NETLOAD
```

4. Выбрать:

```text
AutoCADHttp.dll
```

5. Выполнить:

```text
HTTPSTART
```

6. Проверить:

```text
http://127.0.0.1:5000/ping
```

7. Остановить сервер:

```text
HTTPSTOP
```

Примечания:

* DLL из сети / интернета Windows может пометить как заблокированную: *Свойства файла → Разблокировать*.
* При `SECURELOAD = 1` AutoCAD покажет предупреждение о загрузке из ненадёжного расположения — выбрать
  «Загрузить» или добавить папку с DLL в доверенные (`TRUSTEDPATHS`, *Параметры → Файлы → Надёжные расположения*).
* Сборку, загруженную через `NETLOAD`, нельзя выгрузить без перезапуска AutoCAD (ограничение .NET Framework).
  Чтобы заменить DLL новой версией, закройте AutoCAD.
* Для автозагрузки при старте AutoCAD используйте архив ручной установки (`AcadHttpServer.bundle`, см. выше) —
  сервер всё равно не будет запущен до команды `HTTPSTART`. Не загружайте одновременно DLL из другой папки.

## Ограничения прототипа

На этом этапе проект **не должен**:

* изменять чертёж через HTTP;
* выполнять AutoCAD-команды из HTTP-потока;
* использовать Python;
* использовать Flask;
* открывать сервер во внешнюю сеть;
* слушать `0.0.0.0`;
* автоматически запускать сервер при загрузке DLL.

Сервер должен запускаться **только по команде `HTTPSTART`**.

## Безопасность

HTTP-сервер предназначен только для локального взаимодействия.

Используется:

```text
127.0.0.1
```

а не:

```text
0.0.0.0
```

Поэтому сервер не предназначен для приёма подключений из локальной сети или Интернета.

## Результаты проверки и ограничения

Цепочка **AutoCAD 2021 → C# DLL → HTTP Server → 127.0.0.1:5000 → HTTP-запрос → ответ** реализована.
Что проверено автоматически (CI, без AutoCAD):

* DLL собирается под .NET Framework 4.8 / x64 с официальными сборками AutoCAD 2021 API (NuGet 24.0.0);
* архив ручной установки содержит корректный `AcadHttpServer.bundle` (`PackageContents.xml` для R23.1–R24.3 ссылается
  на упакованную DLL), `install.cmd` / `uninstall.cmd` работают без прав администратора и сохраняют настройки;
* HTTP-слой работает на .NET Framework 4.8 (рантайм AutoCAD 2021) — все требования к серверу покрыты тестами.

Что требует ручной проверки в AutoCAD 2020–2024 (в CI AutoCAD недоступен): автозагрузка `AcadHttpServer.bundle`, `NETLOAD`, команды
`HTTPSTART` / `HTTPSTOP` / `HTTPSTATUS`, обработка IPC из idle и открытие локального виджета в Grist по инструкции выше.

Выявленные ограничения и особенности:

1. **`HttpListener` неудобен внутри AutoCAD** — без прав администратора нужен `netsh http add urlacl`;
   поэтому используется `TcpListener` на `127.0.0.1` (см. «Архитектура»).
2. **AutoCAD API однопоточный.** HTTP-запросы приходят в фоновых потоках; обращаться к чертежу, `Editor` и т.п.
   оттуда нельзя. Сейчас `/ping` AutoCAD API не трогает, а вывод сообщений идёт через `Application.Idle`.
   IPC-диспетчер уже вызывается из `Application.Idle`. Для будущих команд работы с чертежом потребуется
   нужный командный контекст (например, `DocumentCollection.ExecuteInCommandContextAsync`), а также `LockDocument`.
3. **Сообщения из фоновых потоков** появляются в командной строке, когда AutoCAD простаивает
   (не во время выполнения другой команды). Если ни один чертёж не открыт, сообщения ждут открытия чертежа.
4. **Порт 5000** часто занят другими программами (например, приложения ASP.NET Core по умолчанию слушают
   `localhost:5000`). В этом случае `HTTPSTART` сообщает об ошибке `AddressAlreadyInUse`.
5. **Один сервер на процесс AutoCAD** (общий для всех открытых чертежей). Два одновременно запущенных экземпляра
   AutoCAD не могут оба занять порт 5000 — второй получит ошибку «порт занят».
6. **Выгрузка DLL невозможна** без перезапуска AutoCAD; при закрытии AutoCAD сервер останавливается
   автоматически (`IExtensionApplication.Terminate`).
7. Адрес `localhost` в браузере/`curl` сначала может разрешаться в IPv6 `::1`. По умолчанию сервер слушает
   IPv4 `127.0.0.1`; для IPv6 задайте `address: "::1"` и используйте `http://[::1]:5000`.

Отладка: если перед запуском AutoCAD задать переменную окружения `ACADHTTP_VERBOSE=1`, каждый HTTP-запрос
будет выводиться в командную строку (`[HTTP] GET /ping -> 200`).

## Статус проекта

**Prototype / Proof of Concept**

Первоначальная задача проекта:

> Проверить возможность надёжно встроить локальный HTTP-сервер в C# DLL, работающую внутри AutoCAD 2021.

Этап 2 добавляет двунаправленный IPC-транспорт и обслуживание локальных Grist-виджетов. Прикладные операции с чертежом остаются следующим этапом.
