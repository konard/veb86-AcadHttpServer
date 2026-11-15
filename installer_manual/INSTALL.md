# AcadHttpServer — ручная установка (AutoCAD 2020–2024)

Архив `AcadHttpServer-manual-install-v<версия>.zip` устанавливается **без инсталлятора и без прав
администратора**: все файлы размещаются только в профиле текущего пользователя
(`C:\Users\<имя>\AppData\Roaming`, он же `%APPDATA%`).

AutoCAD — `%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle`, плагин загружается автоматически
при запуске AutoCAD. HTTP-сервер при этом **не** запускается — только командой `HTTPSTART`.

## Требования

* AutoCAD 2020, 2021, 2022, 2023 или 2024, 64-разрядный (в том числе вертикальные продукты на базе AutoCAD);
* .NET Framework 4.8. AutoCAD 2021–2024 устанавливают его сами; для AutoCAD 2020 он входит в Windows 10
  версии 1903 и новее, иначе установите .NET Framework 4.8 с сайта Microsoft.

AutoCAD 2025 и новее (.NET 8) этим архивом не поддерживаются — пакет для них не загрузится.

## Состав архива

| Путь в архиве | Назначение |
| --- | --- |
| `AppData\Roaming\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\PackageContents.xml` | описание пакета для автозагрузчика AutoCAD (AutoCAD 2020–2024, x64) |
| `…\AcadHttpServer.bundle\Contents\AutoCAD2020-2024\AutoCADHttp.dll` | плагин |
| `…\AcadHttpServer.bundle\Contents\AutoCAD2020-2024\AutoCADHttp.settings.json` | настройки: адрес, порт, каталог виджетов |
| `install.cmd` | копирует папку `AppData` в профиль пользователя |
| `uninstall.cmd` | удаляет установленный пакет из профиля пользователя |
| `INSTALL.md` | эта инструкция |

Библиотеки AutoCAD (`AcMgd.dll`, `AcDbMgd.dll`, `AcCoreMgd.dll`) не включены — AutoCAD использует свои.

## 1. Распаковка

1. Закройте AutoCAD.
2. Если `AutoCADHttp.dll` раньше загружался из другой папки автоматически (запись автозагрузки в реестре,
   `acad.lsp`, «Автозагрузка» окна `APPLOAD`), уберите эту запись, чтобы AutoCAD не загрузил две копии плагина.
3. Windows помечает скачанные файлы как полученные из интернета. Перед распаковкой откройте свойства
   ZIP-архива и установите флажок **«Разблокировать»** (`install.cmd` также снимает эту отметку с файлов пакета).
4. Распакуйте архив в любую временную папку.

## 2. Копирование файлов в профиль пользователя

**Вариант А — автоматически.** Запустите `install.cmd` двойным щелчком (права администратора не нужны).
Скрипт скопирует содержимое папки `AppData\Roaming` архива в `%APPDATA%`. Уже существующий файл
`AutoCADHttp.settings.json` не перезаписывается — ваши настройки сохраняются при обновлении.

**Вариант Б — вручную.**

1. Нажмите `Win+R`, введите `%USERPROFILE%` и нажмите Enter — откроется `C:\Users\<имя>`.
2. Скопируйте туда папку `AppData` из архива и подтвердите слияние папок. При обновлении не заменяйте
   свой `AutoCADHttp.settings.json`.

После копирования должны существовать файлы:

```text
%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\PackageContents.xml
%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\Contents\AutoCAD2020-2024\AutoCADHttp.dll
%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\Contents\AutoCAD2020-2024\AutoCADHttp.settings.json
```

## 3. Настройка

Файл настроек лежит рядом с DLL:

```text
%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\Contents\AutoCAD2020-2024\AutoCADHttp.settings.json
```

```json
{
  "address": "127.0.0.1",
  "port": 5000,
  "widgetsDirectory": ""
}
```

* `address` — локальный (loopback) адрес, `port` — от 1 до 65535;
* `widgetsDirectory` — каталог статических файлов виджетов: абсолютный путь (например
  `C:/zcad/GristWidgets/widget`) или путь относительно папки DLL. Пустая строка отключает `/widgets`.

Настройки читаются при каждом `HTTPSTART`; после изменения файла выполните `HTTPSTOP`, затем `HTTPSTART`.

## 4. Запуск в AutoCAD

AutoCAD автоматически загружает пакеты из папки пользователя `%APPDATA%\Autodesk\ApplicationPlugins`,
поэтому достаточно запустить AutoCAD.

1. Запустите AutoCAD 2020–2024. В командной строке появится
   `AutoCADHttp loaded (AutoCAD 2023). Commands: HTTPSTART, HTTPSTOP, HTTPSTATUS.` (год — версия запущенного AutoCAD)
2. Если появится предупреждение безопасности о загрузке `AutoCADHttp.dll`, выберите **«Всегда загружать»**
   либо добавьте папку в доверенные пути (команда `OPTIONS` → «Файлы» → «Надёжные расположения» или
   системная переменная `TRUSTEDPATHS`):

   ```text
   %APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\...
   ```

3. Выполните `HTTPSTART` и откройте в браузере `http://127.0.0.1:5000/ping`.
4. `HTTPSTATUS` показывает состояние сервера и путь к файлу настроек, `HTTPSTOP` останавливает сервер.

Если плагин не загрузился автоматически, выполните `NETLOAD` и выберите
`%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\Contents\AutoCAD2020-2024\AutoCADHttp.dll`
(в поле имени файла можно вставить путь целиком — `%APPDATA%` будет раскрыт).

## 5. Обновление

1. Закройте AutoCAD (загруженную DLL нельзя заменить, пока AutoCAD запущен).
2. Распакуйте новую версию архива и повторите шаг 2. `install.cmd` сохранит ваш `AutoCADHttp.settings.json`.

## 6. Удаление

1. Закройте AutoCAD.
2. Запустите `uninstall.cmd` или удалите вручную папку:

   ```text
   %APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle
   ```

Файл настроек `AutoCADHttp.settings.json` находится внутри этой папки и удаляется вместе с ней —
скопируйте его заранее, если он понадобится.
