# Архитектура UltraLauncher

## Стек и обоснование

| Решение | Почему |
|---|---|
| **C# / .NET 10, WPF** | Нативное Windows-приложение: стабильность, аппаратная отрисовка, анимации, DPI PerMonitorV2, без браузерного движка. Async/await и `Process` — удобные асинхронные загрузки и управление процессом игры. Один self-contained exe. |
| **MSAL.NET** | Официальная библиотека Microsoft для OAuth публичного клиента (Auth Code + PKCE, системный браузер, refresh). Собственная реализация протоколов не пишется. |
| **DPAPI** (`ProtectedData`, MSAL Extensions) | Штатное шифрование Windows для секретов пользователя, без своей криптографии. |
| **CommunityToolkit.Mvvm** | Убирает шаблонный код MVVM; единственная зависимость UI. |
| **xUnit** | Тесты ядра без UI. |

## Проекты

```
UltraLauncher.slnx
├─ src/UltraLauncher.Core   (net10.0-windows, без WPF) — вся логика
├─ src/UltraLauncher.App    (WPF, exe UltraLauncher) — только интерфейс и состояние представления
└─ tests/UltraLauncher.Tests (xUnit) — модульные и интеграционные тесты ядра
```

Интерфейс не содержит сетевой логики, сборки команды запуска и работы с секретами: он вызывает только `GameService`, `AccountService` и `SettingsService`.

## Модули ядра (`UltraLauncher.Core`)

| Папка | Ответственность | Ключевые типы |
|---|---|---|
| `Common` | Ошибки с текстом для пользователя, пути, атомарная запись, защита от повторных операций | `LauncherException`/`ErrorCode`, `ErrorTranslator`, `AppPaths`, `GameDirectoryLayout`, `AtomicFile`, `OperationGuard`, `CommandLine` |
| `Logging` | Журнал в файл и память, вырезание секретов | `FileLog`, `SecretRedactor` |
| `Settings` | Модель, хранение с резервной копией и восстановлением, проверка | `LauncherSettings`, `SettingsStore`, `SettingsValidator`, `SystemMemory` |
| `Downloads` | Загрузка с повторами/отменой/контролем зависания, SHA-1, кеш проверенных файлов | `Downloader`, `FileVerifier`, `VerificationCache`, `DownloadItem` |
| `Minecraft` | Манифест, разбор версии, правила ОС, наследование, план установки, установка, natives, аргументы запуска, процесс игры | `VersionManifestService`, `VersionSupport`, `VersionJsonParser`, `RuleEvaluator`, `VersionInheritance`, `LibraryResolver`, `AssetIndex`, `InstallPlanBuilder`, `VersionInstaller`, `InstallationInspector`, `LaunchArgumentsBuilder`, `GameLauncher`/`GameSession`, `Log4jEventParser` |
| `Java` | Поиск Java, версия/разрядность, совместимость, установка Java-среды Mojang | `JavaDetector`, `JavaVersionParser`, `JavaCompatibility`, `JavaRuntimeInstaller` |
| `Auth` | MSAL, Xbox Live/XSTS/Minecraft Services, хранилище аккаунтов (DPAPI), сценарии входа | `IMicrosoftIdentity`/`MsalMicrosoftIdentity`, `XboxMinecraftClient`, `AccountStore`, `AccountService` |
| `Application` | Сценарии приложения и сборка зависимостей | `GameService`, `SettingsService`, `LauncherCore` |

## Сценарий «выбор версии → установка → запуск»

```
UI (HomeViewModel.Play)
  └─ OperationViewModel.RunAsync  (прогресс, отмена, ошибки → уведомление; работа в пуле потоков)
       └─ GameService.PlayAsync(versionId, offline)
            1. OperationGuard("play"), проверка настроек и аккаунта
            2. offline:  InstallationInspector.IsReadyOffline → план из локальных файлов (сеть не нужна)
               online:   установлена и все файлы на месте → без загрузок
                         иначе VersionInstaller.InstallAsync:
                           манифест → JSON версии (SHA-1) → inheritsFrom → индекс ассетов (SHA-1)
                           → InstallPlanBuilder (клиент, LibraryResolver, log4j, ассеты)
                           → проверка файлов (VerificationCache) → Downloader (только недостающие)
                           → распаковка natives → маркер .ultralauncher-installed.json
            3. ResolveJava: путь из настроек (проверка совместимости) или автоподбор,
               при необходимости JavaRuntimeInstaller (официальная Java Mojang)
            4. AccountService.GetPlayerAsync: свежий токен (тихое обновление MSAL → Xbox → MC) или офлайн-данные
            5. LaunchArgumentsBuilder → GameLauncher.Start → GameSession (вывод без секретов, код выхода, crash-report)
```

## Интерфейс (`UltraLauncher.App`)

- MVVM: `MainViewModel` (навигация, уведомления, диалог, общая операция, состояние игры) и модели страниц `Home/Downloads/Accounts/Settings/DiagnosticsViewModel`. `VersionCatalog` — общий список версий.
- `OperationViewModel` — единственная длительная операция за раз (установка/запуск/Java): кнопки повторного запуска отключаются, отмена через `CancellationToken`.
- Представления сопоставляются моделям через `DataTemplate` в `App.xaml`. Темы: `Themes/Colors.xaml` (палитра, шрифты, отступы), `Controls.xaml` (стили всех элементов с состояниями), `Icons.xaml` (векторные иконки).
- Окно с собственным заголовком (`WindowChrome`), смена страниц с анимацией, подгонка размера окна под рабочую область (масштаб 125–150%).
- Один экземпляр приложения (именованный mutex), необработанные ошибки пишутся в журнал и показываются уведомлением.

## Данные и файлы

- `%LOCALAPPDATA%\UltraLauncher`: `settings.json` (+`.bak`, повреждённый файл переименовывается в `.corrupt-*`), `accounts.dat` (DPAPI), `msal_cache.dat` (DPAPI), `logs\launcher-*.log` (14 дней), `logs\game\*.log`, `cache\version_manifest_v2.json`, `cache\verified-files.json`, `runtimes\<component>`.
- Папка игры: стандартная раскладка Mojang (`versions`, `libraries`, `assets`, `saves`…). Удаление версии затрагивает только `versions\<id>`.

## Точки расширения

- Новые типы версий (например, загрузчики модов) — через `VersionInheritance` (уже поддерживает `inheritsFrom`) и отдельный установщик профиля поверх `VersionInstaller`.
- Другой способ входа — новая реализация `IMicrosoftIdentity`.
- Плагинная система намеренно не делалась: границы модулей и интерфейсы достаточны.
