<p align="center">
  <img src=".github/assets/banner.png" alt="Obxodka VPN Banner" width="100%" style="border-radius: 16px; box-shadow: 0 10px 30px rgba(0,0,0,0.5);" />
</p>

<div align="center">

# 🐙 Obxodka VPN Client

**Продвинутый Stealth VPN-клиент нового поколения для Windows и Android.**  
*Абсолютная свобода. Полная анонимность. Открытый исходный код.*

<br/>

[![Latest Release](https://img.shields.io/github/v/release/OctoCore-Dev/obxodka?style=for-the-badge&logo=github&color=00e5ff&label=Latest%20Version)](https://github.com/OctoCore-Dev/obxodka/releases)
[![CI/CD Build](https://img.shields.io/github/actions/workflow/status/OctoCore-Dev/obxodka/release.yml?style=for-the-badge&logo=githubactions&logoColor=white&label=Build%20%26%20Deploy)](https://github.com/OctoCore-Dev/obxodka/actions)
[![Google Play](https://img.shields.io/badge/Google_Play-Available-00C853?style=for-the-badge&logo=googleplay&logoColor=white)](https://play.google.com/store/apps/details?id=com.octocore.obxodka)
[![Microsoft Store](https://img.shields.io/badge/Microsoft_Store-Available-0078D4?style=for-the-badge&logo=windows&logoColor=white)](https://apps.microsoft.com/store/detail/9NZXP5WR803J)
[![Community Rating](https://img.shields.io/badge/Rating-5.0_%E2%98%85%E2%98%85%E2%98%85%E2%98%85%E2%98%85-FFD700?style=for-the-badge&logo=star&logoColor=black)](https://obxodka.one/Reviews)
[![Website](https://img.shields.io/badge/Website-obxodka.one-8B5CF6?style=for-the-badge&logo=googlechrome&logoColor=white)](https://obxodka.one)

<br/>

[![.NET 10 MAUI](https://img.shields.io/badge/.NET-10.0_MAUI-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Platforms](https://img.shields.io/badge/Platforms-Windows_10%2B_%7C_Android_10%2B-00A4EF?style=flat-square&logo=windows)](https://obxodka.one)
[![Protocol](https://img.shields.io/badge/Protocol-gRPC_Multiplexing-02b875?style=flat-square&logo=grpc)](https://grpc.io/)
[![Security](https://img.shields.io/badge/Security-mTLS_&_AES--256--GCM-red?style=flat-square&logo=letsencrypt)](https://obxodka.one)
[![License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)](LICENSE)

</div>

---

## ✨ Ключевые возможности

* 🚀 **Инновационный движок Octopus:** Полный обход любых систем глубокого анализа пакетов (DPI / ТСПУ).
* 🛡️ **mTLS & Двусторонняя криптография:** Каждое устройство получает уникальный динамический `.pfx` сертификат сессии.
* ⚡ **Высокая скорость (Wintun Layer 3):** Нативный драйвер нулевых задержек для Windows и Android VpnService.
* 🌓 **Современный UI:** Поддержка Темной и Светлой темы, Fluent Design 2, адаптивный интерфейс.
* 📊 **Честный почасовой биллинг:** Покупка часов без навязанных подписок.

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

## 🛡️ Открытый исходный код и Zero-Trust безопасность

Мы верим, что безопасность не должна быть "чёрным ящиком". Весь клиентский код Obxodka открыт для аудита:

- 🕵️‍♂️ **Никакой телеметрии и шпионских модулей.**
- 🦠 **Никаких скрытых процессов** (майнеров, ботнетов).
- ⚙️ **Прозрачные привилегии:** повышенные права используются исключительно для управления сетевым адаптером Wintun.
- 🔐 **Безопасное хранилище:** токены шифруются аппаратно через Windows DPAPI и Android Keystore.

---

## 🛠️ Сборка из исходников (Local Build)

Вы можете собрать клиент самостоятельно:

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

---

## 🐙 Архитектура Octopus Engine

| Технология | Назначение |
| :--- | :--- |
| **Виртуальный адаптер** | Интеграция с высокоскоростным драйвером [Wintun](https://www.wintun.net/) (Layer 3). |
| **Stealth-инкапсуляция** | IP-пакеты мультиплексируются внутри HTTP/2 gRPC потоков. Трафик неотличим от обычного веб-серфинга. |
| **mTLS Аутентификация** | Индивидуальный клиентский сертификат шифрования на каждое устройство. |
| **Криптография** | Промышленный стандарт **TLS 1.3** и **AES-256-GCM**. |
| **Domain Fronting** | Динамическая подмена SNI для обхода агрессивных сетевых блокировок. |

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
