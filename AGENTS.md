# AGENTS.md — правила для ИИ-разработчиков UltraLauncher

## Коротко о проекте
Лаунчер Minecraft Java Edition (vanilla) для Windows: C#/.NET 10 + WPF. Логика — в `src/UltraLauncher.Core`, интерфейс — в `src/UltraLauncher.App`, тесты — в `tests/UltraLauncher.Tests`. Подробности — `ARCHITECTURE.md`.

## Команды

```bash
dotnet build UltraLauncher.slnx
dotnet test tests/UltraLauncher.Tests                       # модульные тесты (~10 с, без сети)
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1   # тесты + релиз в dist/
```

Интеграционные тесты с реальными серверами Mojang (сотни МБ, несколько минут):

```powershell
$env:ULTRALAUNCHER_E2E='1'; $env:ULTRALAUNCHER_E2E_DIR='F:\UltraLauncher Проверка\Папка игры'
dotnet test tests\UltraLauncher.Tests --filter "Category=Integration"
```

Ручной прогон без затрагивания реальных данных пользователя: `ULTRALAUNCHER_DATA=<папка>` перед запуском exe.

## Архитектурные ограничения (обязательно)

1. **UI не содержит** сетевых вызовов, сборки команды запуска, работы с файлами игры и секретами. ViewModel вызывает только `GameService`, `AccountService`, `SettingsService` (через `LauncherCore`).
2. **Секреты**: никогда не логировать токены. Любой новый токен — `SecretRedactor.Register(...)`. Хранение только через DPAPI (`AccountStore`) или кеш MSAL. Никаких клиентских секретов и придуманных Client ID.
3. **Без своей криптографии и своих реализаций OAuth** — только MSAL и штатные API Windows.
4. **Аккаунты**: по запросу владельца добавлены локальные офлайн-профили без Microsoft. Не выдавать их за подтверждённую лицензию: `IsLocal = true`, `OwnsGame = false`, без токенов. Microsoft-аккаунты сохраняют проверку покупки и официальный демо-режим. Без сети нужны установленная версия и Java.
5. **Пользовательские данные**: никогда не удалять `saves`, `screenshots`, `resourcepacks`, `shaderpacks`, `options.txt`, `servers.dat` и т. п. Удаление версии — только `versions/<id>` через `InstallationInspector.DeleteVersionFiles`.
6. **Загрузки** — только через `Downloader` (хеш, `.part`, повторы, отмена). Не писать файлы игры напрямую из сети.
7. **Аргументы процесса** — только `ProcessStartInfo.ArgumentList`, без склейки строк (пути с пробелами/кириллицей).
8. **Длительные операции** — асинхронно с `CancellationToken`; в UI — через `OperationViewModel.RunAsync`, повторный запуск защищён `OperationGuard`.
9. **Ошибки для пользователя** — `LauncherException(code, сообщение, подсказка)` на русском: что случилось и что делать.
10. Не показывать неподдерживаемые версии как рабочие (`VersionSupport`). Не подменять список версий захардкоженным.

## Стиль
- Русский язык в UI, сообщениях, комментариях и документации; идентификаторы — английские.
- Nullable включён, file-scoped namespaces, небольшие файлы по ответственности, без лишних абстракций.
- Стили и цвета — только из `Themes/*.xaml` (не задавать цвета инлайн без необходимости). Недоступные кнопки — отключены и с `ToolTip`, объясняющим причину; `ToolTipService.ShowOnDisabled` уже включён в стилях.
- Кнопки с иконкой без текста — обязательно `AutomationProperties.Name`.

## Перед сдачей изменений
- `dotnet test` зелёный; при изменениях установки/запуска — прогнать интеграционные тесты.
- Обновить `TESTING.md` честно: «проверен / не пройден / не проверен» с причиной. Тест на моках не выдавать за проверку реального входа или запуска.
- Пересобрать релиз скриптом, не коммитить `dist/`, `bin/`, `obj/`.
