# AutoCAD HTTP Server

C#-плагин для **AutoCAD 2021**, реализующий локальный HTTP-сервер непосредственно внутри процесса AutoCAD.

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

* **AutoCAD 2021**
* **.NET Framework 4.8**
* **x64**
* C#
* AutoCAD .NET API

Используются библиотеки AutoCAD 2021:

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

Сервер слушает:

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
| `GET` / `HEAD /widgets/...` | статический файл из настроенного каталога |
| неправильный метод `/ipc` или `/widgets` | `405 Method Not Allowed` |
| любой другой путь | `404 Not Found` |
| заголовок `Host` не `127.0.0.1` / `localhost` | `403 Forbidden` (защита от DNS rebinding) |
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

Задайте переменные окружения **до запуска AutoCAD** (или консольного хоста):

```powershell
$env:ACADHTTP_WIDGETS_DIR = 'C:\zcad\GristWidgets\widget'
$env:ACADHTTP_EXTERNAL_IPC = 'http://127.0.0.1:5001/ipc'
$env:ACADHTTP_VERBOSE = '1' # необязательно: журнал HTTP-запросов и исходящей доставки
```

* `ACADHTTP_WIDGETS_DIR` — локальный каталог. Без настройки `/widgets` возвращает `404`.
* `ACADHTTP_EXTERNAL_IPC` — URL внешнего HTTP(S)-сервера, путь строго `/ipc`.
  Если URL не задан или неверен, входящий сервер продолжает работать, исходящий транспорт отключён.
  Итоговые ответы команд требуют настроенного внешнего сервера.
* По умолчанию подробный журнал выключен. Ошибки доставки и тайм-ауты выводятся всегда через
  очередь `CommandLineLog`, без вызова AutoCAD API из HTTP-потока.

`HTTPSTART` запускает сервер и исходящий worker; `HTTPSTOP` закрывает входящие соединения и отменяет
исходящий HTTP без ожидания сети в главном потоке. Ожидающие исходящие сообщения отбрасываются;
уже принятые входящие сообщения сохраняются и будут обработаны после следующего `HTTPSTART`.
Сервер не запускается при `NETLOAD`.

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

Исключение обработчика превращается в коррелированный `COMMAND_FAILED`; следующие сообщения продолжают
обрабатываться. `Content-Type` должен быть `application/json` (`415` иначе), тело — UTF-8 с
`Content-Length` (`411` без длины). Chunked-запросы не поддерживаются (`400`), `Expect: 100-continue`
поддерживается. Размер тела ограничен 1 MiB (`413`), глубина JSON — 32; тайм-аут входящего запроса — 5 с.

Исходящая очередь ограничена 1024 сообщениями. `TryEnqueue`/`QueueOutgoing` возвращает немедленно;
переполнение или остановка даёт `false` и запись в журнал. Один фоновый consumer отправляет сообщения
по порядку. Тайм-аут — 5 с; ошибка соединения, HTTP-ошибка или тайм-аут записывается в журнал, затем
обрабатывается следующее сообщение. Автоматического повтора нет: команда могла уже выполниться
на внешнем сервере. HTTP Client не выполняет AutoCAD API.

### Добавление обработчиков и исходящих событий

Из корректного контекста приложения (в AutoCAD — главного потока):

```csharp
HttpServerPlugin.Dispatcher.Register("STATUS", message =>
    IpcMessage.CreateResponse(message.Id, "{\"state\":\"ready\"}"));

HttpServerPlugin.QueueOutgoing(
    IpcMessage.CreateEvent(Guid.NewGuid().ToString(), "OBJECT_CREATED", "{}"));
```

Будущие обработчики, использующие AutoCAD API, должны также обеспечить нужный контекст команды,
`LockDocument` и транзакции; одного idle-события для всех операций с чертежом недостаточно.

### Статические ресурсы Grist

`/widgets` перенаправляет на `/widgets/`, чтобы относительные ссылки на JS/CSS работали.
`/widgets/` открывает `index.html`. Например:

```text
http://127.0.0.1:5000/widgets/index.html
http://127.0.0.1:5000/widgets/manager.html
http://127.0.0.1:5000/widgets/insert.html
```

HTML, JS, CSS, изображения и шрифты возвращаются с подходящим MIME-типом; бинарные файлы сохраняются
без перекодирования. Поддерживаются вложенные каталоги, query string и `HEAD`. Лимит файла — 8 MiB.
Пути за пределы корневого каталога, `..`, обратные слеши, альтернативные NTFS-потоки и ссылки/junctions
отклоняются. Несуществующий файл — `404`. Каталог должен содержать обычные файлы.

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
```

Она дополнительно проверяет выдачу HTML/бинарного изображения и приём события, затем останавливает хост.

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
  CommandLineLog.cs          потокобезопасный вывод в командную строку (через Application.Idle)
  Http/LocalHttpServer.cs    HTTP-сервер на 127.0.0.1 (не зависит от AutoCAD)
  Http/ApiRouter.cs          /ping, POST /ipc, GET /widgets
  Http/IpcMessage.cs         неизменяемые JSON-конверты
  Http/IpcJson.cs            ограниченный JSON parser без дополнительных DLL
  Http/IpcDispatcher.cs      отдельный диспетчер, вызывается приложением
  Http/IpcOutbox.cs          исходящая очередь + фоновый HTTP Client
  Http/WidgetFiles.cs        файлы из настроенного каталога
  Http/HttpMessages.cs       модели запроса/ответа
tests/AutoCADHttp.Tests/     xUnit-тесты HTTP-слоя (без AutoCAD)
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

Результат: `src\AutoCADHttp\bin\Release\AutoCADHttp.dll`.

### Готовая DLL (GitHub Releases)

После каждого `push` в основную ветку GitHub Actions
([`.github/workflows/build-release.yml`](.github/workflows/build-release.yml)) автоматически:

```text
Checkout → Restore NuGet (AutoCAD.NET 24.0.0) → Build Release / x64 → тесты → AutoCADHttp.dll
         → AutoCADHttp.zip → GitHub Release vX.Y.N (AutoCADHttp.zip в Assets)
```

* Версия формируется автоматически: `v0.0.<номер запуска workflow>` (`v0.0.1`, `v0.0.2`, …); менять версию
  перед commit не нужно. Та же версия записывается в `AutoCADHttp.dll` (`AssemblyVersion`/`FileVersion`),
  commit SHA — в `InformationalVersion`. Номер запуска уникален, поэтому два одновременных запуска не получат
  одну версию; номера неудачных сборок пропускаются. Новую серию (например, `v0.1.N`) можно начать, изменив
  `VERSION_PREFIX` в workflow.
* `AutoCADHttp.zip` содержит только `AutoCADHttp.dll` — `AcMgd.dll`, `AcDbMgd.dll`, `AcCoreMgd.dll` и другие
  DLL Autodesk не включаются (workflow проверяет это и завершается ошибкой, если в выходной папке есть лишние DLL).
* В описании Release: версия, commit SHA, дата сборки, конфигурация `Release / x64`,
  платформа `AutoCAD 2021 / .NET Framework 4.8`.
* AutoCAD API берётся только из NuGet (`-p:UseInstalledAcad=false`). Если пакеты недоступны, шаг
  *Restore NuGet packages* завершается ошибкой с понятным сообщением.
* Если сборка, тесты или проверки не прошли — Release не создаётся, ошибка видна во вкладке *Actions*.
  Ассеты загружаются в черновик Release, который публикуется последним шагом, поэтому неполный Release не появляется.
* Запуск вручную: *Actions → Build & Release → Run workflow*. Release создаётся только при запуске из основной
  ветки; для других веток собирается только артефакт workflow.

Workflow [`.github/workflows/build.yml`](.github/workflows/build.yml) проверяет pull request'ы: собирает DLL и
запускает тесты на Windows (.NET Framework 4.8 и .NET 8) и Linux (.NET 8).

## Тесты

`tests/AutoCADHttp.Tests` — xUnit-тесты HTTP-слоя, работают без AutoCAD (на Windows — для net48 и net8.0,
на Linux — для net8.0). Проверяется:

* `GET /ping` возвращает `200` и JSON `{"status":"ok","application":"AutoCAD","version":"2021"}`;
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
* выполнение диспетчера в потоке вызывающего приложения, корреляция и изоляция ошибок;
* исходящий HTTP на реальный локальный peer, тайм-аут/недоступность/HTTP-ошибка, ограничение очереди и отмена;
* HTML/JS/CSS/изображения/шрифты, бинарный GET и HEAD, изоляция путей и символических ссылок.

Проверка без AutoCAD вручную — консольный хост того же HTTP-слоя:

```text
dotnet run --project examples/StandaloneHost
curl http://127.0.0.1:5000/ping
```

## Загрузка в AutoCAD

1. Собрать проект (или скачать `AutoCADHttp.zip` из последнего [Release](../../releases/latest) и распаковать).
2. Запустить AutoCAD 2021.
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
* Для автозагрузки при старте AutoCAD можно использовать Autoloader (`ApplicationPlugins\*.bundle`) или
  ключи реестра — сервер всё равно не будет запущен до команды `HTTPSTART`.

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
* HTTP-слой работает на .NET Framework 4.8 (рантайм AutoCAD 2021) — все требования к серверу покрыты тестами.

Что требует ручной проверки в AutoCAD 2021 (в CI AutoCAD недоступен): `NETLOAD`, команды
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
7. Адрес `localhost` в браузере/`curl` сначала может разрешаться в IPv6 `::1`, на котором сервер не слушает —
   клиент переключится на `127.0.0.1`, но лучше сразу использовать `http://127.0.0.1:5000`.

Отладка: если перед запуском AutoCAD задать переменную окружения `ACADHTTP_VERBOSE=1`, каждый HTTP-запрос
будет выводиться в командную строку (`[HTTP] GET /ping -> 200`).

## Статус проекта

**Prototype / Proof of Concept**

Первоначальная задача проекта:

> Проверить возможность надёжно встроить локальный HTTP-сервер в C# DLL, работающую внутри AutoCAD 2021.

Этап 2 добавляет двунаправленный IPC-транспорт и обслуживание локальных Grist-виджетов. Прикладные операции с чертежом остаются следующим этапом.
