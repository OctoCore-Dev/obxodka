# Журнал тотального аудита и рефакторинга кодовой базы Obxodka (80k строк)

> **Статус:** В процессе  
> **Методология:** Пошаговое парное программирование по дереву проекта: полный разбор каждого класса, объяснение физики сокетов/ОС/памяти, выявление багов и написание чистого кода (.NET 10, C#, Zero-Vibe-Coding).

---

## 📌 Золотые правила аудита (Зафиксированы в `csharp-memory` и `AGENTS.md`)

1. **Никаких муляжей и галлюцинаций:** Только рабочий код без фиктивных классов и пустых циклов.
2. **Сетевая строгость:**
   - Никаких фейковых TTL внутри `NetworkStream` (ломает TCP SEQ).
   - `ClientWebSocket.SendAsync` вызывается **строго из одного места через канал** `Channel<(byte[], int)>`.
   - SSL Pinning обязателен (никакого слепого доверия корням ОС без пиннинга).
   - Никаких мастер-ключей и HMAC-секретов в открытом XOR-виде.
   - Валидация Authenticode и SHA256 при обновлениях.
3. **Качество кода:** .NET 10, строгий async/await, `var`, file-scoped namespaces, первичные конструкторы, `s_camelCase` для статики (`t_` только для `[ThreadStatic]`), суффиксы `Async`.
4. **Прозрачность:** Перед изменением критического кода — детальный разбор механики, рисков гонок и блокировок, и согласование с Ромой.

---

## 📁 Настроенные правила и навыки в `.agents/`

* **Правила (`.agents/rules/`):**
  * `strict-development-standards.md` — Главный регламент надежности и запрета муляжей.
  * `network-engineering-standards.md` — Физика сети L3/L4/L7, запрет TTL-инъекций в Stream, потокобезопасность WebSocket, MSS Clamping.
  * `csharp-dotnet10-standards.md` — Стандарты .NET 10, Zero-allocation (`Span`, `Memory`, `ArrayPool`), первичные конструкторы, именование `s_camelCase`.
  * `maui-fluent-ui-standards.md` — Microsoft Fluent 2 Design System, MVVM, потокобезопасные обновления через `Dispatcher`.
* **Навыки (`.agents/skills/`):**
  * `network-tunnel-architect` — Проектирование туннелей, DPI-десинхронизация, WebSocket/H2 потоки, Wintun.
  * `csharp-zero-allocation` — Оптимизация горячих сетевых путей без аллокаций памяти.
  * `maui-fluent-designer` — Fluent 2 дизайн и MVVM для MAUI и Avalonia.

---

## 🗺 Дерево аудита классов (снизу вверх)

### Этап 1. Базовые структуры и фрейминг (`src/core/obxodka.Shared/`) — 100% ЗАВЕРШЕНО (0 ОШИБОК, 0 ПРЕДУПРЕЖДЕНИЙ)
- [x] ~~`OctopusProtocol.cs`~~ — **УДАЛЕН.** Выявлен как мертвый муляж (generic Stream pump без логики протокола, не используемый ни клиентом, ни сервером).
- [x] `Models.cs` — DTO авторизации, устройств, серверов и инцидентов. Устранена аллокация в `DeviceIcon` (Span-based парсинг), удален мусорный дубликат `PaymentUrlResponse`, добавлено поэлементное сравнение зеркал в `VpnServerDto`, зарегистрированы все недостающие типы в `AppJsonContext.cs` для стабильного AOT. Добавлен `string? Email` в `TelemetryDto`.
- [x] `Models/SplitTunnelPolicy.cs` — Политики Split Tunneling. Исправлен префикс статики `s_` вместо запрещенного `t_`, устранены аллокации замыканий LINQ `Any()` на перечислении активных маршрутов обхода, статические коллекции переведены на `FrozenSet<string>`.
- [x] `Config/AppSecrets.cs` — Криптографические секреты: замена псевдослучайного `Random` на криптографически стойкий `RandomNumberGenerator.GetInt32`, кэширование байтов ключей UTF-8, префиксы `s_`.
- [x] `Stealth/ClientHello/` (`TlsBrowserProfile.cs`, `TlsGreaseHelper.cs`, `ChromiumHello.cs`, `FirefoxHello.cs`, `SafariHello.cs`, `RoutingHello.cs`, `ClientHelloBuilder.cs`) — **Архитектура Browser TLS Masquerading.** Модульная генерация TLS 1.3 ClientHello (RFC 8446) под все популярные браузеры (Chrome, Яндекс Браузер, Edge, Firefox, Safari iOS/macOS). Поддержка GREASE (RFC 8701), ALPN h2, скрытия длины SNI через Padding, опционального постквантового обмена ключами X25519MLKEM768 и умной лестницы ротации отпечатков при блокировках ТСПУ.
- [x] ~~`Stealth/Obfuscator.cs`~~ — **УДАЛЕН.** Легаси V1/V2 протокол (8-байтный статический заголовок длин с жесткой маской XOR `0x5A3C96E7`), детектируемый ТСПУ. Полностью заменен современным `ObxodkaFraming.cs`. Вычищены все мосты, удалены старые `ObfuscatorTests.cs`, тесты очередей и бенчмарки переведены на чистый IP и `ObxodkaFraming`.
- [x] `Stealth/ObxodkaFraming.cs` — Защита от переполнения `ushort` (wrap-around при размере кадра > 65535 байт), замена всех 10 вызовов `Random.Shared` на криптографически безопасный `RandomNumberGenerator`, безопасный `GetRandomInt` против исключений при малом MTU, префиксы `s_`.
- [x] `MtuEngine/` (`Ipv4MtuHandler.cs`, `Ipv6MtuHandler.cs`, `SmartMtuEngine.cs`) — Полная модульная декомпозиция MTU-стека. MSS Clamping, фрагментация IPv4 с пересчетом L3 Checksum, ICMPv6 Packet Too Big (Type 2, Code 0) по RFC 4443. Удален устаревший `TcpMssClamper.cs`.
- [x] `Stealth/PriorityPacketQueue.cs` — Двухуровневая очередь Deficit Round Robin 7:1 с приоритизацией TCP Handshake (SYN, FIN, RST, pure ACK), ICMP и DNS. Агрессивный инлайнинг и потокобезопасность.
- [x] `Stealth/PacketRouter.cs` — 3-лучевая архитектура: Ray 0 (основной Data), Ray 1 (вторичный Data, Hedging пинга/DNS/игр), Ray 2 (инспектор Control Plane). Удалены мосты-костыли, единый `SelectRaysFromHealth`.
- [x] `Stealth/PacketDeduplicator.cs` — Кольцевой буфер 256 КБ (L2 cache), атомарная Lock-Free упаковка в `ulong` (44 бита хэш, 20 бит время), окно дедупликации 80 мс, маскирование IPv4 TTL / Checksum и IPv6 HopLimit. Удален устаревший метод-мост, весь код на чистом `ReadOnlySpan<byte>`.
- [x] `Themes/SafeThemeExtractor.cs` — Перевод распаковки ZipArchive на неблокирующий асинхронный `entry.OpenAsync(ct)`, префиксы `s_`, `FrozenSet<string>` для разрешенных расширений.
- [x] `Themes/ThemeValidator.cs` — Статические коллекции переведены на `FrozenSet<string>`, префиксы `s_`.
- [x] `Helpers/DeviceClassifier.cs` & `FormatHelper.cs` — Префиксы `s_`, исключение аллокаций памяти.
- [x] `Logging/AppLogger.cs` — Замена всех некорректных префиксов `t_` на `s_`.

### Этап 2. Сетевое ядро и транспорты (`src/core/obxodka.Client/Core/`)
- [x] `Core/Transports/RayInspector.cs` — **Полная изоляция луча-инспектора (Control Plane):** исключение Ray 2 из Data Plane, кандидаты на блэкхол строго среди Ray 0 и Ray 1, запрет маршрутизации пользовательских пинг/игровых пакетов на Ray 2.
- [ ] `Core/Transports/ObxodkaStreamTransport.cs` — Завершение аудита стримов, фикс CA5394 `Random`, `CA2000`, `Channel` пайплайны.
- [ ] `Core/Transports/DpiBypassStream.cs` — Удаление Fake TTL из `NetworkStream`, чистый L4 `midsld`.
- [ ] `Core/Transports/ObxodkaWebSocketTransport.cs` — Ликвидация параллельных вызовов `SendAsync`, перевод в `_txChannel`.
- [ ] `Core/OctopusEngine.cs` — Главный оркестратор туннеля.
- [ ] `Security/CertificateValidator.cs` — Жесткий SSL Pinning без слепого доверия.
- [ ] `Services/ApiService.cs` — DoH, Anycast, устойчивость к цензуре.

### Этап 3. Системные адаптеры и платформы
- [ ] `Platforms/Windows/WindowsSplitTunnelEngine.cs` — Вычищение фейкового SYN-ACK.
- [ ] `Platforms/Windows/WinDivertEngine.cs` — Удаление муляжей.
- [ ] `Platforms/Android/OctopusVpnService.cs` — TUN loop, фикс `unsafe` для libc `read`/`write`.
- [ ] `Services/WindowsAppUpdaterService.cs` — Добавление Authenticode и SHA-256 проверки.

### Этап 4. UI и ViewModels (`src/apps/obxodka.Maui/` & `obxodka.Avalonia/`)
- [ ] ViewModels и биндинги
- [ ] Fluent 2 темы и стили

---

## 📝 Журнал прогресса по классам

| № | Файл / Класс | Выявленные проблемы | Статус |
|---|---|---|---|
| 1 | `OctopusProtocol.cs` | Мертвый муляж stream pump | УДАЛЕН |
| 2 | `Models.cs` | Аллокации ToLowerInvariant, дубликат PaymentUrlResponse, отсутствие поэлементного сравнения зеркал | ЗАВЕРШЕНО |
| 3 | `Models/SplitTunnelPolicy.cs` | Префикс статики t_, аллокации замыканий Any() | ЗАВЕРШЕНО |
| 4 | `Config/AppSecrets.cs` | Небезопасный Random, префиксы t_ | ЗАВЕРШЕНО |
| 5 | `Stealth/ClientHelloBuilder.cs` | Небезопасный Random, аллокации массивов расширений TLS | ЗАВЕРШЕНО |
| 6 | `Stealth/Obfuscator.cs` | Легаси V1 протокол со статическим заголовком длин и масками XOR. Заменен на ObxodkaFraming | УДАЛЕН |
| 7 | `Stealth/ObxodkaFraming.cs` | Опасность ushort wrap-around, небезопасный Random, префиксы t_ | ЗАВЕРШЕНО |
| 8 | `MtuEngine/` | Разделение IPv4/IPv6 MTU логики, корректный пересчет контрольных сумм при фрагментации | ЗАВЕРШЕНО |
| 9 | `Stealth/PriorityPacketQueue.cs` | Приоритизация TCP handshake/ACK/ICMP, потокобезопасность, 7:1 DRR | ЗАВЕРШЕНО |
| 10 | `Stealth/PacketRouter.cs` | 3-лучевой роутинг, Ray 0/1 (Data), Ray 2 (Inspector), удаление мостов | ЗАВЕРШЕНО |
| 11 | `Stealth/PacketDeduplicator.cs` | 256 КБ L2 Ring, маскировка TTL/HopLimit/Checksum, удален bridge-метод | ЗАВЕРШЕНО |
| 12 | `Core/Transports/RayInspector.cs` | Утечка пользовательских данных/пинга в Control Plane Ray 2 ликвидирована | ЗАВЕРШЕНО |
| 13 | `Core/Transports/DpiBypassStream.cs` | Инъекция фейковых TTL в TCP NetworkStream (TCP RST от сервера) | В ОЧЕРЕДИ (ЭТАП 2) |
