<p align="center">
  <img src=".github/assets/banner.png" alt="Obxodka VPN Banner" width="100%" style="border-radius: 16px; box-shadow: 0 10px 30px rgba(0,0,0,0.5);" />
</p>

<p align="center">
  <img src=".github/assets/animated_header.svg" alt="Obxodka Live Engine Status" width="100%" />
</p>

<div align="center">

# 🐙 Obxodka VPN Client

### *Продвинутый Stealth VPN-клиент нового поколения для Windows и Android.*
**Абсолютная свобода в сети • Устойчивость к DPI и ТСПУ • Открытый исходный код**

<br/>

[![Latest Release](https://img.shields.io/github/v/release/OctoCore-Dev/obxodka?style=for-the-badge&logo=github&color=00e5ff&label=Latest%20Version)](https://github.com/OctoCore-Dev/obxodka/releases)
[![CI/CD Build](https://img.shields.io/github/actions/workflow/status/OctoCore-Dev/obxodka/release.yml?style=for-the-badge&logo=githubactions&logoColor=white&label=Build%20%26%20Deploy)](https://github.com/OctoCore-Dev/obxodka/actions)
[![Google Play](https://img.shields.io/badge/Google_Play-Available-00C853?style=for-the-badge&logo=googleplay&logoColor=white)](https://play.google.com/store/apps/details?id=com.octocore.obxodka)
[![Microsoft Store](https://img.shields.io/badge/Microsoft_Store-Available-0078D4?style=for-the-badge&logo=windows&logoColor=white)](https://apps.microsoft.com/store/detail/9NZXP5WR803J)
[![Community Rating](https://img.shields.io/badge/Rating-5.0_%E2%98%85%E2%98%85%E2%98%85%E2%98%85%E2%98%85-FFD700?style=for-the-badge&logo=star&logoColor=black)](https://obxodka.one/Reviews)
[![Website](https://img.shields.io/badge/Website-obxodka.one-8B5CF6?style=for-the-badge&logo=googlechrome&logoColor=white)](https://obxodka.one)

<br/>

[![.NET 10 MAUI](https://img.shields.io/badge/.NET-10.0_MAUI-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![C# 14](https://img.shields.io/badge/Language-C%23_14-239120?style=flat-square&logo=c-sharp)](https://learn.microsoft.com/dotnet/csharp/)
[![gRPC Multiplex](https://img.shields.io/badge/Protocol-gRPC_HTTP%2F2-02b875?style=flat-square&logo=grpc)](https://grpc.io/)
[![Security](https://img.shields.io/badge/Security-mTLS_&_AES--256--GCM-red?style=flat-square&logo=letsencrypt)](https://obxodka.one)
[![Wintun Layer 3](https://img.shields.io/badge/Kernel_Driver-Wintun-orange?style=flat-square&logo=windows)](https://www.wintun.net/)
[![License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)](LICENSE)

</div>

<br/>

<p align="center">
  <img src=".github/assets/live_metrics.svg" alt="Live Telemetry Metrics" width="100%" />
</p>

---

## 🚀 Почему Obxodka?

Традиционные протоколы (WireGuard, OpenVPN, IPsec) используют специфические заголовки пакетов и порты UDP, которые мгновенно распознаются и блокируются современными государственными и провайдерскими системами анализа трафика (**DPI / ТСПУ**).

**Obxodka** решает эту проблему принципиально иначе: мы упаковываем весь сетевой трафик в стандартные защищенные **HTTP/2 gRPC потоки через порт 443**. Для внешнего наблюдателя и систем фильтрации ваша активность выглядит как обычный просмотр видео или веб-сёрфинг.

---

## ⚡ Интерактивная архитектура Octopus Engine

Динамическая схема прохождения пакетов через конвейер маскировки и криптографический тоннель:

```mermaid
flowchart LR
    subgraph Client["💻 Клиентское устройство (Windows / Android)"]
        direction TB
        App["🐙 Obxodka Client App (UI / Logic)"]
        Adapter["⚡ Wintun / VpnService (Layer 3)"]
        Stealth["🎭 Stealth Encapsulator (HTTP/2 gRPC)"]
        Crypto["🔐 mTLS Dynamic Crypto (AES-256-GCM)"]
        App --> Adapter --> Stealth --> Crypto
    end

    subgraph ISP["🛡️ Провайдер / ТСПУ (DPI-фильтрация)"]
        direction TB
        DPI{"🕵️ Глубокий анализ пакетов"}
        Pass["✅ 100% Пропуск (Трафик неотличим от обычного HTTPS)"]
        DPI ==> Pass
    end

    subgraph Server["☁️ Серверный кластер Obxodka Core"]
        direction TB
        mTLS_GW["🔑 mTLS Gateway"]
        OctoCore["⚡ Octopus Core Node"]
        CleanNet["🌍 Свободный и чистый Интернет"]
        mTLS_GW --> OctoCore --> CleanNet
    end

    Crypto ==>|Шифрованный поток :443| DPI
    Pass ==>|gRPC Multiplexing| mTLS_GW

    classDef clientStyle fill:#1a1c23,stroke:#00e5ff,stroke-width:2px,color:#fff;
    classDef dpiStyle fill:#2d1b36,stroke:#ff007f,stroke-width:2px,color:#fff;
    classDef serverStyle fill:#13271f,stroke:#00ff88,stroke-width:2px,color:#fff;
    
    class Client clientStyle;
    class ISP dpiStyle;
    class Server serverStyle;
```

---

## 🔐 Протокол аутентификации mTLS Zero-Trust

Никаких статических паролей и общих ключей: каждое устройство проходит динамическую верификацию сертификата:

```mermaid
sequenceDiagram
    autonumber
    actor User as 👤 Пользователь
    participant Client as 🐙 Obxodka App
    participant Auth as 🔑 Auth Server
    participant Gateway as ⚡ Stealth Node

    User->>Client: Авторизация в аккаунт
    Client->>Auth: Запрос динамического сертификата (mTLS)
    Auth-->>Client: Выдача зашифрованного сессионного .pfx сертификата
    Client->>Gateway: Установка mTLS соединения через порт :443
    Gateway-->>Client: Взаимное подтверждение подлинности (Handshake OK)
    Client->>Gateway: Туннелирование трафика с полной защитой от DPI
```

---

## 📊 Сравнение технологий и протоколов

| Протокол / Технология | Устойчивость к DPI / ТСПУ | Скорость и задержка | Шифрование сессии | Мультиплексирование | Защита от блокировок |
| :--- | :---: | :---: | :---: | :---: | :---: |
| 🐙 **Obxodka (Octopus Engine)** | 🟢 **100% (Не детектируется)** | ⚡ **< 1ms задержка (Wintun)** | 🔒 **mTLS + AES-256-GCM** | 🚀 **HTTP/2 gRPC Streams** | 🛡️ **Максимальная** |
| 🛡️ **WireGuard** | 🔴 **0% (Блокируется по UDP)** | ⚡ **Высокая** | 🔒 ChaCha20-Poly1305 | ❌ Нет | ❌ Блокируется |
| 🔒 **OpenVPN** | 🔴 **10% (Легко детектируется)** | 🐢 **Средняя/Низкая (TAP)** | 🔒 TLS / AES-CBC | ❌ Нет | ❌ Блокируется |
| 👥 **Shadowsocks / VLESS** | 🟡 **60% (Частично блокируется)** | ⚡ **Высокая** | 🔒 AEAD / TLS | ⚠️ Ограничено | ⚠️ Частичная |

---

## 🎨 Галерея интерфейса

<p align="center">
  <img src="Resources/Images/Previews/vpn_on_dark.png" width="48%" alt="Dark Theme" style="border-radius: 12px; margin-right: 2%;" />
  <img src="Resources/Images/Previews/vpn_on_light.png" width="48%" alt="Light Theme" style="border-radius: 12px;" />
</p>
<p align="center">
  <img src="Resources/Images/Previews/vpn_off_dark.png" width="48%" alt="Disconnected" style="border-radius: 12px; margin-right: 2%;" />
  <img src="Resources/Images/Previews/login.png" width="48%" alt="Login Screen" style="border-radius: 12px;" />
</p>

---

## 🗺️ Дорожная карта развития (Roadmap 2026)

- [x] 🚀 **Релиз клиента для Android** в [Google Play Store](https://play.google.com/store/apps/details?id=com.octocore.obxodka)
- [x] 🪟 **Релиз клиента для Windows 10/11** в [Microsoft Store](https://apps.microsoft.com/store/detail/9NZXP5WR803J)
- [x] ⚡ **Движок Octopus:** Wintun Layer 3 + Stealth HTTP/2 gRPC
- [x] 🔄 **Автоматическая синхронизация отзывов:** Google Play + Microsoft Store на сайте [obxodka.one/Reviews](https://obxodka.one/Reviews)
- [x] 🛡️ **CI/CD Авто-деплой:** непрерывная сборка и публикация в магазины через GitHub Actions
- [ ] 🍎 **Разработка клиентов под iOS и macOS**
- [ ] 🌐 **Режим Mesh-Routing и децентрализованные релейные ноды**
- [ ] 🛑 **Встроенный AdBlock & Anti-Phishing фильтр на уровне DNS**

---

## ❓ Часто задаваемые вопросы (FAQ)

<details>
<summary><b>🔍 Почему Obxodka не блокируется, когда блокируют другие VPN?</b></summary>
<br>

Большинство обычных VPN используют протоколы с фиксированными сигнатурами (WireGuard handshake, OpenVPN TLS handshake). Системы DPI легко видят такие пакеты и сбрасывают соединение. **Obxodka** инкапсулирует трафик в стандартные gRPC-потоки поверх TLS 1.3 на 443 порту — для любого провайдера это выглядит как обычный защищенный просмотр веб-сайтов крупных IT-корпораций.
</details>

<details>
<summary><b>🛡️ Ведёт ли Obxodka логи посещений (Logs)?</b></summary>
<br>

**Категорически нет.** Вся архитектура построена по принципу Zero-Log. Серверы не хранят историю посещенных сайтов, IP-адреса назначения или DNS-запросы. Трафик проходит через оперативную память в зашифрованном виде и мгновенно уничтожается.
</details>

<details>
<summary><b>💻 Как собрать проект из исходников самостоятельно?</b></summary>
<br>

```bash
# 1. Клонирование репозитория
git clone https://github.com/OctoCore-Dev/obxodka.git
cd obxodka

# 2. Установка рабочих нагрузок .NET MAUI
dotnet workload install maui-windows maui-android

# 3. Сборка клиента под Windows
dotnet build obxodka.csproj -f net10.0-windows10.0.19041.0 -c Release

# 4. Запуск модульных тестов
dotnet test tests/obxodka.Tests/obxodka.Tests.csproj
```
</details>

---

## 🤝 Сообщество и контакты

* 🌐 **Официальный сайт:** [obxodka.one](https://obxodka.one)
* 💬 **Форум и обсуждения:** [GitHub Discussions](https://github.com/OctoCore-Dev/obxodka/discussions)
* 🐛 **Сообщить об ошибке:** [GitHub Issues](https://github.com/OctoCore-Dev/obxodka/issues)
* 📧 **Контакты и поддержка:** [contact@octocore.dev](mailto:contact@octocore.dev)
* 📜 **Кодекс поведения:** [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
* 🔒 **Политика безопасности:** [SECURITY.md](SECURITY.md)

---

<p align="center">
  <sub>Разработано с ❤️ командой <a href="https://github.com/OctoCore-Dev">OctoCore</a>. Лицензия <a href="LICENSE">MIT</a>.</sub>
</p>
