# AutoCAD HTTP Server

Простой C#-плагин для **AutoCAD 2021**, реализующий локальный HTTP-сервер непосредственно внутри процесса AutoCAD.

Проект является прототипом для проверки возможности взаимодействия с AutoCAD через локальный HTTP API без использования внешнего Python/Flask-сервера.

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
| любой другой путь | `404 Not Found` |
| заголовок `Host` не `127.0.0.1` / `localhost` | `403 Forbidden` (защита от DNS rebinding) |
| некорректный запрос | `400 Bad Request` |

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
`/ping` и для будущего JSON API.

### Структура проекта

```text
AutoCADHttp.sln
src/AutoCADHttp/
  AutoCADHttp.csproj         net48, x64; ссылки на AcMgd/AcDbMgd/AcCoreMgd
  HttpServerPlugin.cs        IExtensionApplication + команды HTTPSTART/HTTPSTOP/HTTPSTATUS
  CommandLineLog.cs          потокобезопасный вывод в командную строку (через Application.Idle)
  Http/LocalHttpServer.cs    HTTP-сервер на 127.0.0.1 (не зависит от AutoCAD)
  Http/ApiRouter.cs          маршрутизация: GET /ping
  Http/HttpMessages.cs       модели запроса/ответа
tests/AutoCADHttp.Tests/     xUnit-тесты HTTP-слоя (без AutoCAD)
examples/                    скрипты проверки /ping, консольный хост сервера без AutoCAD
```

HTTP-слой (`src/AutoCADHttp/Http`) не ссылается на AutoCAD API — его можно тестировать и запускать отдельно.

В дальнейшем HTTP-запросы должны использоваться только как транспорт. Работа с объектами AutoCAD должна выполняться в корректном контексте AutoCAD и, при необходимости, через очередь команд.

## План развития

После успешной проверки `/ping` планируется добавить:

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
* сквозная проверка на реальном порту `127.0.0.1:5000`.

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

На первом этапе проект **не должен**:

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
`HTTPSTART` / `HTTPSTOP` / `HTTPSTATUS` и `/ping` из браузера по инструкции выше.

Выявленные ограничения и особенности:

1. **`HttpListener` неудобен внутри AutoCAD** — без прав администратора нужен `netsh http add urlacl`;
   поэтому используется `TcpListener` на `127.0.0.1` (см. «Архитектура»).
2. **AutoCAD API однопоточный.** HTTP-запросы приходят в фоновых потоках; обращаться к чертежу, `Editor` и т.п.
   оттуда нельзя. Сейчас `/ping` AutoCAD API не трогает, а вывод сообщений идёт через `Application.Idle`.
   Для будущих команд работы с чертежом понадобится очередь и выполнение в главном потоке (например, через
   `Application.Idle` или `DocumentCollection.ExecuteInCommandContextAsync`), а также `LockDocument`.
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

После подтверждения работоспособности прототипа API может быть расширен до полноценного IPC-механизма для взаимодействия внешних приложений с AutoCAD.
