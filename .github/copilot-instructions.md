# Инструкции для GitHub Copilot (Obxodka)

Этот файл задаёт обязательные инженерные стандарты для всех изменений, которые предлагает или создаёт Copilot (как в облачном агенте, так и в IDE).

## 1. Стек, сборка и стиль кода (.NET 11 / C#)
- Целевой SDK: **.NET 11** (`11.0.x`), `<Nullable>enable</Nullable>`, `<AnalysisMode>Recommended</AnalysisMode>`, `<WarningLevel>9999</WarningLevel>`.
- Строгий `async`/`await`: **запрещены** блокирующие `.Result`, `.Wait()` и вызовы в обход `ConfigureAwait` в библиотечном/ядерном коде.
- Все асинхронные методы обязаны иметь суффикс `Async`.
- Использовать `var` для локальных переменных, file-scoped namespaces, primary constructors, collection expressions `[]`.
- Именование полей (строго по `.editorconfig`):
  - Приватные поля экземпляра: `_camelCase`.
  - Приватные статические поля: `s_camelCase` (префикс `t_` зарезервирован исключительно для `[ThreadStatic]`).
- Комментарии в коде писать коротко, по-русски, со строчной буквы, без нумерации списков и академических формулировок (только суть).
- Запрещено переименовывать существующие публичные символы, ломать API-контракты или удалять несвязанные комментарии и docstrings.

## 2. Проверка в песочнице Copilot (Linux)
- Обязательные команды проверки перед завершением задачи:
  - `dotnet build src/core/obxodka.Shared/obxodka.Shared.csproj --no-restore`
  - `dotnet build src/core/obxodka.Client/obxodka.Client.csproj --no-restore`
  - `dotnet build src/apps/obxodka.Avalonia/obxodka.Avalonia.csproj --no-restore`
  - `dotnet test tests/obxodka.Client.Tests/obxodka.Client.Tests.csproj --filter "Category!=UI" --no-restore`
- Целевая сборка `obxodka.Maui` (`net11.0-windows10.0.19041.0`) требует Windows и MAUI workloads — в Linux-песочнице проверяй логику через `obxodka.Client` и юнит-тесты `obxodka.Client.Tests`.

## 3. Нулевая терпимость к заглушкам (Zero Mocks / Placeholders)
- **Категорически запрещено** писать пустые классы-заглушки, фейковые логи или имитировать работу сетевых/системных механизмов.
- Любая новая логика должна реально функционировать и проверяться сборкой и тестами.

## 4. Сетевая строгость, P/Invoke и безопасность (VPN Core)
- **Запрещено** инжектировать фейковые байты/TTL внутри активных TCP/TLS-потоков (`NetworkStream`) — это ломает `SEQ` и сбрасывает соединение. Модификация L3-пакетов допустима только корректно (Wintun / Raw Sockets / WinDivert) с пересчётом контрольных сумм.
- Строгая потокобезопасность сокетов и `ClientWebSocket`: `SendAsync` обязан вызываться строго из одного потребителя через `Channel`.
- Обязательны **SSL Pinning** и строгая проверка хэшей ключей; запрещено слепо доверять любым сертификатам в продакшен-коде.
- Запрещено хранить секреты, токены, пароли или мастер-ключи в открытом виде в клиентском коде.
- При использовании `[LibraryImport("libc")]` на Android/Linux **всегда** указывать явный `EntryPoint` в нижнем регистре (`EntryPoint = "read"` / `"write"`) и `DllImportSearchPath.SafeDirectories` — ELF-символы `libc.so` регистрозависимы.

## 5. Архитектура (SSOT), UI и чистота рефакторинга
- **Single Source of Truth (SSOT):** периферийные/платформенные слои (MAUI, Avalonia, платформенные сервисы) должны оставаться тонкими адаптерами и не дублировать состояние, порты, URL или логику failover бизнес-ядра.
- **Адаптивный UI и темы MAUI:** раскладка экранов управляется централизованно через `AdaptiveViewport` (`AdaptiveLayoutHelper`) и контракт `IAdaptivePage.ApplyAdaptiveLayout(AdaptiveViewport)`; для всех цветов использовать `{DynamicResource ...}` (без хардкода цветов), чтобы не ломать `ThemeManager` и кастомные темы.
- **Zero Legacy Leftovers:** при замене механизма, переносе кода или рефакторинге в том же изменении удалять все неиспользуемые `using`, поля, методы-дублёры и устаревшие XAML-привязки/`x:Name`.
- Всегда проверять жизненный цикл `CancellationTokenSource`, каналов и сокетов на гонки потоков и утечки ресурсов (`IDisposable`).

## 6. Git и Conventional Commits
- Основная ветка разработки — **`develop`** (все PR и ветки задач отводить от `develop` и направлять в `develop`).
- Формат коммитов и заголовков PR: `<тип>(<область>): <описание>` на русском языке с маленькой буквы, без точки на конце, глагол в инфинитиве совершенного вида (например: `fix(core): исправить утечку сокета при переподключении`).
- Допустимые типы: `feat`, `fix`, `refactor`, `perf`, `docs`, `style`, `test`, `chore`.
- Никогда не добавлять в репозиторий `.gemini/`, `CODEBASE_MAP.md`, `AGENTS.md`, `.idea/` и временные файлы.
