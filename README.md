<p align="center">
  <a href="https://github.com/OctoCore-Dev/obxodka/releases">
    <img src=".github/assets/banner.png" alt="Obxodka VPN Banner" width="100%" style="border-radius: 16px; box-shadow: 0 12px 40px rgba(0, 0, 0, 0.6);" />
  </a>
</p>

<p align="center">
  <img src=".github/assets/animated_header.svg" alt="Obxodka Live Engine Status" width="100%" />
</p>

<div align="center">

# <img src=".github/assets/icons/octo.svg" width="52" height="52" alt="Obxodka Mascot" style="vertical-align: -12px;" /> Obxodka VPN Client

### *Стелс-VPN нового поколения с протоколом OBXODKA-STREAM и технологией Dual-Ray Hedging для Windows и Android.*

> **Абсолютная сетевая свобода • Устойчивость к DPI и ТСПУ • Нулевой пинг в играх • Строгий Zero-Log**

<br/>

<!-- Store & Action Badges -->
<a href="https://github.com/OctoCore-Dev/obxodka/releases"><img src="https://img.shields.io/github/v/release/OctoCore-Dev/obxodka?style=for-the-badge&logo=github&color=00e5ff&label=Latest%20Version" alt="Latest Version" /></a>
<a href="https://github.com/OctoCore-Dev/obxodka/stargazers"><img src="https://img.shields.io/github/stars/OctoCore-Dev/obxodka?style=for-the-badge&logo=github&color=F59E0B&label=Stars" alt="GitHub Stars" /></a>
<a href="https://github.com/OctoCore-Dev/obxodka/releases"><img src="https://img.shields.io/github/downloads/OctoCore-Dev/obxodka/total?style=for-the-badge&logo=github&color=10B981&label=Downloads" alt="Total Downloads" /></a>
<a href="https://github.com/orgs/OctoCore-Dev/projects/1"><img src="https://img.shields.io/badge/Roadmap-Obxodka_Dev-00e5ff?style=for-the-badge&logo=github&logoColor=white" alt="Public Roadmap" /></a>
<a href="https://github.com/OctoCore-Dev/obxodka/actions/workflows/release.yml"><img src="https://img.shields.io/github/actions/workflow/status/OctoCore-Dev/obxodka/release.yml?branch=main&style=for-the-badge&logo=githubactions&logoColor=white&label=Central%20Pipeline" alt="Central Pipeline" /></a>
<a href="https://play.google.com/store/apps/details?id=com.octocore.obxodka"><img src="https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/badges/playstore.json&style=for-the-badge&logo=googleplay" alt="Google Play Deploy" /></a>
<a href="https://apps.microsoft.com/store/detail/9NZXP5WR803J"><img src="https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/badges/mstore.json&style=for-the-badge&logo=windows" alt="Microsoft Store Deploy" /></a>
<a href="https://github.com/OctoCore-Dev/obxodka/actions/workflows/store-watchdog.yml"><img src="https://img.shields.io/github/actions/workflow/status/OctoCore-Dev/obxodka/store-watchdog.yml?branch=main&style=for-the-badge&logo=githubactions&logoColor=white&label=Store%20Watchdog" alt="Store Watchdog" /></a>
<a href="https://github.com/OctoCore-Dev/obxodka"><img src="https://img.shields.io/badge/Stealth-Octopus_v7.0-8B5CF6?style=for-the-badge&logo=shield&logoColor=white" alt="Stealth Engine" /></a>

<br/><br/>

<!-- Tech Stack Badges -->
[![.NET 10 MAUI](https://img.shields.io/badge/.NET-10.0_MAUI-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![C# 14](https://img.shields.io/badge/Language-C%23_14-239120?style=flat-square&logo=c-sharp)](https://learn.microsoft.com/dotnet/csharp/)
[![CI](https://img.shields.io/github/actions/workflow/status/OctoCore-Dev/obxodka/ci.yml?branch=develop&style=flat-square&logo=githubactions&logoColor=white&label=CI)](https://github.com/OctoCore-Dev/obxodka/actions/workflows/ci.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/OctoCore-Dev/obxodka/codeql.yml?branch=develop&style=flat-square&logo=github&logoColor=white&label=CodeQL)](https://github.com/OctoCore-Dev/obxodka/actions/workflows/codeql.yml)
[![Strict Zero-Logs](https://img.shields.io/badge/Privacy-Strict_Zero--Logs-00C853?style=flat-square&logo=shield)](PRIVACY_POLICY.md)
[![Wintun Layer 3](https://img.shields.io/badge/Kernel_Driver-Wintun-orange?style=flat-square&logo=windows)](https://www.wintun.net/)
[![License: GNU AGPLv3](https://img.shields.io/badge/License-GNU_AGPLv3-blue?style=flat-square)](LICENSE)
[![Trademark Policy](https://img.shields.io/badge/Brand-Trademark_Policy-6366F1?style=flat-square)](TRADEMARK.md)

</div>

<br/>

<p align="center">
  <img src=".github/assets/live_metrics.svg" alt="Live Telemetry Metrics" width="100%" />
</p>

---

## <img src=".github/assets/icons/octo_zap.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Почему Obxodka?

Традиционные VPN-протоколы (WireGuard, OpenVPN, IPsec) используют фиксированные порты и узнаваемые заголовки пакетов, которые мгновенно
распознаются и блокируются государственными и магистральными системами глубокого анализа пакетов (**DPI / ТСПУ**).

**Obxodka** решает эту задачу фундаментально через проприетарный высокоскоростной движок **OBXODKA-STREAM**:

<table>
  <tr>
    <td width="50%" valign="top">
      <h3><img src=".github/assets/icons/octo_stealth.svg" width="30" height="30" style="vertical-align: -6px;" /> 1. Гибридный транспорт под DPI / ТСПУ</h3>
      <p>Клиент сочетает два транспорта: прямой <b>OBXODKA-STREAM</b> (полнодуплексный HTTP/2 поверх TLS 1.3 на портах <code>443</code>, <code>8443</code>, <code>2053</code> в режиме Zero-SNI с жестким SSL Pinning) и резервный облачный <b>OBXODKA-WS</b> через Cloudflare Edge с ротацией зеркал и бесшовной эскалацией без пересоздания TUN-адаптера.</p>
    </td>
    <td width="50%" valign="top">
      <h3><img src=".github/assets/icons/octo_game.svg" width="30" height="30" style="vertical-align: -6px;" /> 2. Dual-Ray Hedging для гейминга</h3>
      <p>В режиме <code>OBXODKA-STREAM</code> открываются два параллельных TCP-луча (<code>Ray 0</code> и <code>Ray 1</code>), а критичные UDP/ICMP-пакеты дублируются в оба луча. Кольцевой дедупликатор <code>PacketDeduplicator</code> без аллокаций отбрасывает запоздавшую копию и сглаживает импульсный джиттер.</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3><img src=".github/assets/icons/octo_ping.svg" width="30" height="30" style="vertical-align: -6px;" /> 3. Адаптивная сегментация ClientHello</h3>
      <p>Модуль <code>DpiBypassStream</code> совместно с <code>RoutingHello</code> выбирает браузерный TLS-профиль и применяет нарезку первого пакета <code>ClientHello</code> (<code>PureL4Midsld</code>, <code>TlsRecordMidsld</code>, <code>MultiRecordMidsld</code>) с микро-джиттером, усложняя сигнатурный разбор на ТСПУ.</p>
    </td>
    <td width="50%" valign="top">
      <h3><img src=".github/assets/icons/octo_disconnect.svg" width="30" height="30" style="vertical-align: -6px;" /> 4. Zero-Flicker Disconnect &amp; Probing</h3>
      <p>Плавное снятие <code>/1</code> супернет-маршрутов на Windows без сброса физического адаптера, адаптивное зондирование задержки и удержание NAT сотовых операторов на Android без «моргания» интернета у локальных программ и игр.</p>
    </td>
  </tr>
  <tr>
    <td colspan="2" valign="top">
      <h3><img src=".github/assets/icons/octo_mtu.svg" width="30" height="30" style="vertical-align: -6px;" /> 5. Адаптивный MTU и защита от «чёрных дыр» ТСПУ</h3>
      <p>Внутритуннельное зондирование MTU, серверный TCPMSS Clamping и встроенный L3 <code>DnsAdBlocker</code> обеспечивают прохождение трафика на мобильных сетях и ШПД при каскадной инкапсуляции без «залипания» HTTPS-сессий.</p>
    </td>
  </tr>
</table>

---

## <img src=".github/assets/icons/octo_server.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Архитектура конвейера Obxodka

Сетевой трафик проходит через единый сквозной конвейер ядра `OctopusEngine`:

* **Платформенные адаптеры (Windows / Android):** Перехват Layer 3 пакетов через драйвер **Wintun** (64 MB ring buffer) на Windows и
  нативный `VpnService` на Android с поддержкой **Split Tunneling** и исключением банковских/государственных приложений.
* **Маршрутизация и приоритеты:** `PacketRouter` распределяет трафик по `PriorityPacketQueue` (приоритет DNS, TCP SYN/ACK, игрового UDP и
  ICMP) и при использовании `OBXODKA-STREAM` дублирует realtime-пакеты по `Ray 0` и `Ray 1`.
* **Защита от фризов ТСПУ:** При обнаружении блокировки даунлинка (`TX > 0, RX = 0`) инспектор `OctopusEngine.TryEscalateToShieldAsync` на
  лету переключает транспорт на облачный **WebSocket Shield** (`OBXODKA-WS`) без обрыва TUN-интерфейса.

---

## <img src=".github/assets/icons/octo_lock.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Взаимная аутентификация mTLS и DoH Anti-Poison

* **Сессионные сертификаты mTLS:** При входе клиент получает персональный `.pfx`-сертификат устройства и предъявляет его при установлении
  туннеля.
* **Проверка открытых ключей (SPKI Pinning):** При прямых подключениях к серверам `CertificateValidator` сверяет SHA-256 хэш публичного
  ключа сервера с эталоном, предотвращая MITM-подмену даже при наличии сторонних корневых сертификатов.
* **DoH-резолвинг:** `ApiService` проверяет домены через шифрованные DoH-резолверы, отсеивая поддельные bogon/sinkhole-ответы провайдеров.

---

## <img src=".github/assets/icons/octo_chart.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Сравнение протоколов в условиях жёсткого DPI

| Протокол / Подход                                                                                                                    |          Маскировка под HTTPS           |       Резервный CDN Shield       |     Обработка потерь UDP/ICMP     |     Шифрование / Аутентификация      |
|:-------------------------------------------------------------------------------------------------------------------------------------|:---------------------------------------:|:--------------------------------:|:---------------------------------:|:------------------------------------:|
| <img src=".github/assets/icons/octo.svg" width="24" height="24" style="vertical-align:-4px;"/> **Obxodka (`OBXODKA-STREAM` + `WS`)** | HTTP/2 over TLS 1.3 + `DpiBypassStream` | Авто-эскалация на Cloudflare WSS | `Dual-Ray Hedging` + дедупликация | TLS 1.3 + mTLS `.pfx` + SPKI Pinning |
| **Классический WireGuard**                                                                                                           |      Нет (сигнатура UDP-хендшейка)      |               Нет                |        Одиночный UDP-поток        |     Noise_IK / статические ключи     |
| **OpenVPN (TCP/UDP)**                                                                                                                | Частичная (распознаётся по TLS-фреймам) |               Нет                |  Одиночный поток / TCP-over-TCP   |      TLS / OpenVPN Data Channel      |
| **Shadowsocks / VLESS**                                                                                                              |  Зависит от транспорта (WS / Reality)   |        Опционально (CDN)         |       Одиночное соединение        |           AEAD / XTLS-TLS            |

---

## <img src=".github/assets/icons/octo_shield.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Интеллектуальный защитный контур (Smart Shield Engine)

В **Obxodka** интегрирован передовой комплекс аппаратной защиты от телеметрии, слежки и утечек трафика:

* <img src=".github/assets/icons/octo_dns.svg" width="26" height="26" style="vertical-align:-6px;"/> **Аппаратный L3 DNS Sinkhole (Windows &
  Android):** Встроенный в драйвер Wintun и TUN-ядро фильтр. Все попытки фоновой телеметрии (`AppMetrica`, `Yandex Metrika`, `VK Stats`,
  `Mail.ru`, аналитика банков, трекеры `AppsFlyer` / `Adjust`) перехватываются на сетевом уровне L3 и за **`0.05 мс` гасятся ответом
  `0.0.0.0`**. Шпионский пакет физически не покидает оперативную память.
* <img src=".github/assets/icons/octo_ping.svg" width="26" height="26" style="vertical-align:-6px;"/> **Happy Eyeballs & Latency Scoring:**
  Параллельное неблокирующее зондирование узлов кластера при подключении. Клиент автоматически выбирает сервер с минимальным RTT и
  наименьшей нагрузкой.
* <img src=".github/assets/icons/octo_stealth.svg" width="26" height="26" style="vertical-align:-6px;"/> **Multi-Stage TLS Splitting:**
  Динамическое расщепление первого сегмента TLS ClientHello на 1–5 байт с паузой 15–35 мс и фрагментацией тела пакета. DPI не может
  распознать сигнатуру рукопожатия.
* <img src=".github/assets/icons/octo_zap.svg" width="26" height="26" style="vertical-align:-6px;"/> **Zero-Handoff Seamless Switching:**
  Бесшовное переключение серверов и сетевой роуминг (Wi-Fi ↔ LTE) без сброса TCP-соединений и потери пакетов.
* <img src=".github/assets/icons/octo_shield.svg" width="26" height="26" style="vertical-align:-6px;"/> **Защита от утечек DNS (NRPT &
  Auto-Heal):** Принудительная изоляция через таблицу политик разрешения имен Windows (NRPT), сброс кэша резолвера и авто-восстановление
  системных DNS-адаптеров.
* <img src=".github/assets/icons/octo_stealth.svg" width="26" height="26" style="vertical-align:-6px;"/> **Smart App Cloaking (Android):**
  Автоматическая изоляция отечественных банков, Госуслуг и сервисов (`AddDisallowedApplication`). Система Android рапортует приложениям:
  **`TRANSPORT_VPN: FALSE`** — банковские переводы работают без блокировок.
* <img src=".github/assets/icons/octo_lock.svg" width="26" height="26" style="vertical-align:-6px;"/> **Hardware Kill-Switch:**
  Принудительная блокировка утечек IPv6 (`fd00::/8`), изоляция портов WebRTC/STUN (`3478` / `5349`) и аппаратная блокировка пакетов при
  разрывах связи.

---

## <img src=".github/assets/icons/octo_terminal.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Галерея интерфейса

<p align="center">
  <img src=".github/assets/previews/vpn_on_dark.png" width="48%" alt="Dark Theme" style="border-radius: 12px; margin-right: 2%; box-shadow: 0 8px 24px rgba(0,0,0,0.5);" />
  <img src=".github/assets/previews/vpn_on_light.png" width="48%" alt="Light Theme" style="border-radius: 12px; box-shadow: 0 8px 24px rgba(0,0,0,0.5);" />
</p>
<p align="center">
  <img src=".github/assets/previews/vpn_off_dark.png" width="48%" alt="Disconnected" style="border-radius: 12px; margin-right: 2%; box-shadow: 0 8px 24px rgba(0,0,0,0.5);" />
  <img src=".github/assets/previews/login.png" width="48%" alt="Login Screen" style="border-radius: 12px; box-shadow: 0 8px 24px rgba(0,0,0,0.5);" />
</p>

---

## <img src=".github/assets/icons/octo_doc.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Дорожная карта развития (Roadmap 2026)

- [x] **Релиз клиента для Android** в [Google Play Store](https://play.google.com/store/apps/details?id=com.octocore.obxodka)
- [x] **Релиз клиента для Windows 10/11** в [Microsoft Store](https://apps.microsoft.com/store/detail/9NZXP5WR803J)
- [x] **Движок Octopus:** Wintun Layer 3 + OBXODKA-STREAM (HTTP/2 duplex)
- [x] **Dual-Ray Hedging:** Удвоение игрового трафика и пингов с zero-allocation дедупликацией
- [x] **Zero-Flicker Disconnect:** Бесшовное отключение туннеля без сброса сетевого стека Windows
- [x] **Автоматическая синхронизация отзывов:** Google Play + Microsoft Store в единой экосистеме
- [x] **CI/CD Авто-деплой:** непрерывная сборка и публикация в магазины через GitHub Actions
- [x] **Встроенный AdBlock & Anti-Phishing фильтр на уровне DNS**
- [ ] **Кроссплатформенный desktop-клиент (`obxodka.Avalonia`, Linux / macOS)** *(UI-каркас в разработке)*
- [ ] **Клиент под iOS**

---

## <img src=".github/assets/icons/octo_tools.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Часто задаваемые вопросы (FAQ)

<details>
<summary><b>Почему клиент открыт под GNU AGPLv3 и можно ли собрать его самостоятельно?</b></summary>
<br/>

Мы открыли исходный код клиентского приложения под лицензией **[GNU AGPLv3](LICENSE)**, чтобы любой разработчик или исследователь
безопасности мог убедиться в отсутствии скрытой слежки и бэкдоров. Если вы не доверяете готовым сборкам из магазинов приложений, вы можете
скомпилировать клиент самостоятельно из этого репозитория и пользоваться им через свой аккаунт **Obxodka** (с активной подпиской/балансом
времени).

Форки и доработки приветствуются в виде **Pull Request** в ветку `develop`. Обратите внимание на **[TRADEMARK.md](TRADEMARK.md)**: сторонние
сборки обязаны сохранять лицензию AGPLv3 (открытый исходный код), но не имеют права использовать бренд **Obxodka / OctoCore**, логотипы и
официальные ID пакетов в сторах.

</details>

<details>
<summary><b>Как Obxodka обходит блокировки DPI / ТСПУ?</b></summary>
<br/>

Обычные VPN используют протоколы с фиксированными сигнатурами (WireGuard, OpenVPN). **Obxodka** сочетает два режима: прямой
**OBXODKA-STREAM** (полнодуплексный HTTP/2 поверх TLS 1.3 на портах `443`, `8443`, `2053` в режиме Zero-SNI) с нарезкой `ClientHello` в
`DpiBypassStream` и облачный **OBXODKA-WS** через Cloudflare Shield с ротацией зеркал и автоматическим переключением
`TryEscalateToShieldAsync` при зависании канала.

</details>

<details>
<summary><b>Как Dual-Ray Hedging решает проблему пинга и потерь пакетов в онлайн-играх?</b></summary>
<br/>

В режиме `OBXODKA-STREAM` игровой UDP-трафик и ICMP-пинги клонируются в два параллельных TCP-луча (`Ray 0` и `Ray 1`). Принимающая сторона
забирает первый дошедший пакет, а дубликат мгновенно отбрасывается через `PacketDeduplicator` без аллокаций памяти. Это сглаживает
импульсные потери и джиттер на проблемных участках сети.

</details>

<details>
<summary><b>Ведёт ли Obxodka логи посещений (Logs)?</b></summary>
<br/>

**Нет.** Вся маршрутизация пакетов работает в оперативной памяти. Серверы не сохраняют историю посещённых сайтов, IP-адреса назначения или
DNS-запросы. Подробнее читайте в [Политике конфиденциальности](PRIVACY_POLICY.md).

</details>

<details>
<summary><b>Как собрать проект и запустить тесты из исходников самостоятельно?</b></summary>
<br/>

```powershell
# 1. Клонирование репозитория
git clone https://github.com/OctoCore-Dev/obxodka.git
cd obxodka

# 2. Установка рабочих нагрузок .NET MAUI
dotnet workload install maui-windows maui-android

# 3. Сборка клиента под Windows
dotnet build src/apps/obxodka.Maui/obxodka.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release

# 4. Запуск автономных модульных тестов (без живого сервера и UI)
dotnet test tests/obxodka.Client.Tests/obxodka.Client.Tests.csproj -c Release --filter "Category!=Integration&Category!=UI&Category!=WireLevel"
```

Подробнее о структуре решений (`obxodka.slnx`), стандартах кода и отправке PR читайте в [.github/CONTRIBUTING.md](.github/CONTRIBUTING.md).

</details>

---

## <img src=".github/assets/icons/octo_server.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Статус умного деплоя (Smart Multi-Store Pipeline)

Система релизов Obxodka использует двухконтурный механизм умного деплоя (**Smart Store Deployer & Watchdog**), предотвращающий сбои сборки
при нахождении приложения на проверке:

| Контур / Магазин         | Пайплайн                                                     |                                                                                                                                      Текущий статус                                                                                                                                      | Механика работы                                                                                                            |
|:-------------------------|:-------------------------------------------------------------|:----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------:|:---------------------------------------------------------------------------------------------------------------------------|
| **Центральный релиз**    | [`release.yml`](.github/workflows/release.yml)               |      [![Central Pipeline](https://img.shields.io/github/actions/workflow/status/OctoCore-Dev/obxodka/release.yml?branch=main&style=flat-square&logo=githubactions&logoColor=white&label=Central%20Pipeline)](https://github.com/OctoCore-Dev/obxodka/actions/workflows/release.yml)      | Сборка Windows MSIX, Android AAB, прогон xUnit тестов (505 тестов) и публикация в GitHub Releases.                         |
| **Google Play Store**    | `OctoPublisher (Android)`                                    |               [![Google Play Deploy](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/badges/playstore.json&style=flat-square&logo=googleplay)](https://play.google.com/store/apps/details?id=com.octocore.obxodka)               | Автопубликация AAB в Google Play Console (Production). При модерации переводится в очередь `[QUEUED]`.                     |
| **Microsoft Store**      | `OctoPublisher (Windows)`                                    |                       [![Microsoft Store Deploy](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/.github/badges/mstore.json&style=flat-square&logo=windows)](https://apps.microsoft.com/store/detail/9NZXP5WR803J)                       | Автопубликация MSIX в Partner Center API. Если магазин на ревью (код 3) — не ломает сборку, а ставит в очередь `[QUEUED]`. |
| **Smart Store Watchdog** | [`store-watchdog.yml`](.github/workflows/store-watchdog.yml) | [![Store Watchdog](https://img.shields.io/github/actions/workflow/status/OctoCore-Dev/obxodka/store-watchdog.yml?branch=main&style=flat-square&logo=githubactions&logoColor=white&label=Store%20Watchdog)](https://github.com/OctoCore-Dev/obxodka/actions/workflows/store-watchdog.yml) | Автономный фоновый сторож: циклически проверяет окончание проверки сторами и автоматически допушивает релиз из очереди.    |

---

## <img src=".github/assets/icons/octo_globe.svg" width="34" height="34" alt="" style="vertical-align: -8px;" /> Сообщество, правовая информация и контакты

<table>
  <tr>
    <td width="50%">
      <b>Ресурсы и портал</b>
      <ul>
        <li><img src=".github/assets/icons/octo_globe.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Официальные релизы:</b> <a href="https://github.com/OctoCore-Dev/obxodka/releases">GitHub Releases</a></li>
        <li><img src=".github/assets/icons/octo_doc.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>История версий:</b> <a href="CHANGELOG.md">CHANGELOG.md</a></li>
        <li><img src=".github/assets/icons/octo_doc.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Архитектура ядра:</b> <a href="docs/ARCHITECTURE.md">docs/ARCHITECTURE.md</a></li>
        <li><img src=".github/assets/icons/octo_tools.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Диагностика:</b> <a href="docs/TROUBLESHOOTING.md">docs/TROUBLESHOOTING.md</a></li>
        <li><img src=".github/assets/icons/octo_terminal.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Дайджест ТСПУ:</b> <a href="docs/BLOCKING_DIGEST.md">docs/BLOCKING_DIGEST.md</a></li>
        <li><img src=".github/assets/icons/octo_shield.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Безопасность:</b> <a href=".github/SECURITY.md">.github/SECURITY.md</a></li>
      </ul>
    </td>
    <td width="50%">
      <b>Правовые положения и комплаенс</b>
      <ul>
        <li><img src=".github/assets/icons/octo_shield.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Политика конфиденциальности:</b> <a href="PRIVACY_POLICY.md">PRIVACY_POLICY.md</a></li>
        <li><img src=".github/assets/icons/octo_doc.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Условия использования:</b> <a href="TERMS_OF_SERVICE.md">TERMS_OF_SERVICE.md</a></li>
        <li><img src=".github/assets/icons/octo_lock.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Товарные знаки и бренд:</b> <a href="TRADEMARK.md">TRADEMARK.md</a></li>
        <li><img src=".github/assets/icons/octo_terminal.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Участие в разработке:</b> <a href=".github/CONTRIBUTING.md">CONTRIBUTING.md</a></li>
        <li><img src=".github/assets/icons/octo_globe.svg" width="18" height="18" style="vertical-align:-3px;"/> <b>Поддержка и обсуждения:</b> <a href="https://github.com/OctoCore-Dev/obxodka/discussions">GitHub Discussions</a></li>
      </ul>
    </td>
  </tr>
</table>

---

<p align="center">
  <sub>Разработано командой <a href="https://github.com/OctoCore-Dev">OctoCore</a>. Лицензия кода <a href="LICENSE">GNU AGPLv3</a> • Правила бренда <a href="TRADEMARK.md">TRADEMARK.md</a>.</sub>
</p>
