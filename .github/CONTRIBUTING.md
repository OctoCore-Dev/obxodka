# <img src="https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/assets/icons/octo_tools.svg" width="30" height="30" alt="" style="vertical-align: -5px;" /> Руководство по участию в разработке (Contributing to Obxodka)

Спасибо за интерес к развитию проекта **Obxodka**! Мы открыты к любому конструктивному вкладу: от отчётов об ошибках и диагностики
провайдерских блокировок до оптимизации сетевого ядра, Linux/Desktop/CLI клиентов, UI и документации.

---

## <img src="https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/assets/icons/octo_terminal.svg" width="22" height="22" alt="" style="vertical-align: -3px;" /> Требования для локальной разработки

- **.NET 11 SDK** (точная версия закреплена в [`global.json`](../global.json), TFM задаётся централизованно через [`sdk.props`](../sdk.props))
- **Visual Studio 2026 / Rider / VS Code** с поддержкой C# 15 и .NET 11
- Для сборки `obxodka.Maui` (Windows / Android): рабочие нагрузки `maui-windows` и `maui-android`
- Для `obxodka.Cli`, `obxodka.Avalonia`, ядра и тестов: достаточно базового `.NET 11 SDK` (работает на Windows и Linux без MAUI workloads)

```powershell
# Установка рабочих нагрузок MAUI (только если собираете мобильный/WinUI клиент)
dotnet workload install maui-windows maui-android
```

---

## <img src="https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/assets/icons/octo_zap.svg" width="22" height="22" alt="" style="vertical-align: -3px;" /> Процесс разработки и сборки (Workflow)

1. **Форкните** репозиторий [`OctoCore-Dev/obxodka`](https://github.com/OctoCore-Dev/obxodka) на GitHub и клонируйте с подмодулями (`git clone --recursive`).
2. Создайте свою ветку строго от **`develop`**:
   ```bash
   git checkout -b fix/my-improvement
   ```
3. Соберите нужную цель:
   ```powershell
   # Сборка CLI (Windows / Linux)
   dotnet build src/apps/obxodka.Cli/obxodka.Cli.csproj -c Release

   # Сборка Avalonia Desktop (Linux / Windows)
   dotnet build src/apps/obxodka.Avalonia/obxodka.Avalonia.csproj -c Release

   # Сборка MAUI клиента под Windows / Android
   dotnet build src/apps/obxodka.Maui/obxodka.Maui.csproj -f net11.0-windows10.0.19041.0 -c Release
   dotnet build src/apps/obxodka.Maui/obxodka.Maui.csproj -f net11.0-android -c Release

   # Сборка всего решения obxodka.slnx
   dotnet build obxodka.slnx -c Release
   ```
4. Запустите автономные модульные тесты (не требуют живого VPN-сервера и Appium UI):
   ```powershell
   dotnet test tests/obxodka.Client.Tests/obxodka.Client.Tests.csproj -c Release --filter "Category!=Integration&Category!=UI&Category!=WireLevel"
   ```
   _Все 725+ тестов ядра, протоколов (OBX-V9 Hydra / Chameleon / XHTTP / WS) и CLI должны проходить с **0 warnings, 0 errors**._
5. Проверьте требования эталона **`dotnet-blueprint`** и качества кода:
   - Версии пакетов управляются централизованно через [`Directory.Packages.props`](../Directory.Packages.props) (CPM) с централизованными lock-файлами в `.nuget/locks/`. При обновлении пакета обновите lock-файл командой `dotnet restore --force-evaluate`.
   - Общие `global using` хранятся в корневом [`GlobalUsings.cs`](../GlobalUsings.cs) (кроме `obxodka.Shared`, который сохраняет совместимость с серверным репозиторием).
   - Соблюдаются строгие правила анализаторов (`.globalconfig`, `.editorconfig`) и запрещённых API (`BannedSymbols.txt`: без `.Result` / `.Wait()` / `Task.Run` в горячих путях).
   - Статические приватные поля с префиксом `s_camelCase` (`t_` только для `[ThreadStatic]`), в библиотеках обязателен `ConfigureAwait(false)`.
   - Никаких секретов, токенов, `.pfx` или приватных IP в диффе.
   - В горячих путях пакетов используйте пулы (`ArrayPool<byte>.Shared`) и `Span<byte>` без лишних аллокаций.
6. Закоммитьте изменения в формате **Conventional Commits** и откройте **Pull Request** в ветку **`develop`**.

---

## <img src="https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/assets/icons/octo_doc.svg" width="22" height="22" alt="" style="vertical-align: -3px;" /> Лицензия, бренд и права на вклад (AGPLv3 + Trademark Policy)

* Клиентский код распространяется по открытой лицензии **[GNU Affero General Public License v3.0 (AGPLv3)](../LICENSE)**.
* Направляя Pull Request в `OctoCore-Dev/obxodka`, вы подтверждаете, что имеете право передать предложенный код под лицензией **GNU AGPLv3**
  для включения в официальный репозиторий и официальные релизы Obxodka (включая публикации в Google Play, Microsoft Store, Winget, Linux-репозитории и GitHub Releases).
* Наименования **Obxodka / OctoCore**, логотипы и идентификаторы пакетов защищены **[TRADEMARK.md](../TRADEMARK.md)**: сторонние сборки
  обязаны сохранять открытый код под AGPLv3, но не имеют права использовать официальный бренд.

---

## <img src="https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/assets/icons/octo_tools.svg" width="22" height="22" alt="" style="vertical-align: -3px;" /> Сообщения об ошибках (Issues)

Если вы обнаружили баг или блокировку на конкретном провайдере:

1. Проверьте существующие [Issues](https://github.com/OctoCore-Dev/obxodka/issues).
2. Создайте новый Issue по шаблону **Bug Report**, указав платформу (Windows / Android / Linux CLI / Avalonia), провайдера и фрагмент лога (`obx diag` / `debug.html`, **без JWT-токенов, email и паролей**).
3. Если нашли уязвимость безопасности — не открывайте публичный Issue, а напишите через
   **[Security Advisories](https://github.com/OctoCore-Dev/obxodka/security/advisories/new)** ([SECURITY.md](SECURITY.md)).

---

## <img src="https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/assets/icons/octo_globe.svg" width="22" height="22" alt="" style="vertical-align: -3px;" /> Связь с командой

- **Поддержка:** [GitHub Issues](https://github.com/OctoCore-Dev/obxodka/issues)
- **Релизы:** [GitHub Releases](https://github.com/OctoCore-Dev/obxodka/releases)
- **Документация CLI и Linux:** [docs/CLI_AND_LINUX.md](../docs/CLI_AND_LINUX.md)
- **Обсуждения:** [GitHub Discussions](https://github.com/OctoCore-Dev/obxodka/discussions)
