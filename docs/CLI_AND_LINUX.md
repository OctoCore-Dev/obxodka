# <img src="../.github/assets/icons/octo_terminal.svg" width="30" height="30" alt="" style="vertical-align: -5px;" /> Установка и использование Obxodka CLI и Avalonia Desktop (Windows & Linux)

Полное руководство по установке, автообновлению и повседневной работе с консольным клиентом **`obxodka` (`obx`)** и графическим клиентом **Obxodka Desktop (Avalonia)** на Windows и Linux.

---

## 1. Windows — установка и автообновление CLI (`winget`)

### Через Windows Package Manager (`winget`) — рекомендуется
```powershell
# Установка CLI (автоматически добавляет команды obxodka и obx + драйвер wintun.dll)
winget install OctoCore.Obxodka.Cli

# Автоматическое обновление до новой версии
winget upgrade OctoCore.Obxodka.Cli
```

### Ручная установка из GitHub Releases
1. Скачайте `obxodka-cli-win-x64.zip` и `SHA256SUMS.txt` со страницы [GitHub Releases](https://github.com/OctoCore-Dev/obxodka/releases/latest).
2. Проверьте контрольную сумму и распакуйте архив (сохраняя папку `Engine\wintun.dll` рядом с `obxodka.exe`):
   ```powershell
   Get-FileHash .\obxodka-cli-win-x64.zip -Algorithm SHA256
   ```
3. Зарегистрируйте постоянный алиас `obx` и автодополнение PowerShell:
   ```powershell
   .\obxodka.exe alias --install
   obx completion powershell | Out-File -Append $PROFILE
   ```

> **Примечание:** для создания L3-адаптера Wintun и системных маршрутов на Windows команду `obx connect` необходимо выполнять в терминале, запущенном **от имени Администратора**.

---

## 2. Linux — установка CLI (`obxodka`) и Avalonia Desktop (`obxodka-desktop`)

### Быстрая установка одной командой (Ubuntu / Debian / Fedora / Arch / openSUSE)
Скрипт автоматически определяет дистрибутив и архитектуру (`x86_64` / `arm64`), подключает официальный подписанный GPG-репозиторий Obxodka (либо ставит свежий пакет из GitHub Releases с проверкой `SHA256SUMS.txt`):

```bash
# Только CLI (команды obxodka и obx + systemd-юнит + completions)
curl -fsSL https://pkg.obxodka.one/install.sh | sudo sh

# Только графический клиент Avalonia Desktop (obxodka-desktop + ярлык в меню)
curl -fsSL https://pkg.obxodka.one/install.sh | sudo sh -s -- --desktop

# Всё сразу: CLI + Avalonia Desktop
curl -fsSL https://pkg.obxodka.one/install.sh | sudo sh -s -- --all
```

---

### Ручное подключение пакетных менеджеров (с автообновлениями)

#### Debian / Ubuntu / Linux Mint / Pop!_OS (`apt`)
```bash
sudo mkdir -p /usr/share/keyrings
curl -fsSL https://pkg.obxodka.one/gpg.key \
  | sudo gpg --dearmor -o /usr/share/keyrings/obxodka.gpg

echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/obxodka.gpg] https://pkg.obxodka.one/apt stable main" \
  | sudo tee /etc/apt/sources.list.d/obxodka.list

sudo apt update
sudo apt install -y obxodka           # CLI
sudo apt install -y obxodka-desktop   # Avalonia Desktop GUI
```
Дальнейшие обновления приходят автоматически через `sudo apt update && sudo apt upgrade`.

#### Fedora / RHEL / Rocky / Nobara (`dnf`)
```bash
sudo curl -fsSL https://pkg.obxodka.one/rpm/obxodka.repo \
  -o /etc/yum.repos.d/obxodka.repo

sudo dnf install -y obxodka           # CLI
sudo dnf install -y obxodka-desktop   # Avalonia Desktop GUI
```
Дальнейшие обновления приходят автоматически через `sudo dnf upgrade`.

#### Arch Linux / Manjaro / CachyOS / SteamOS (`pacman`)
```bash
curl -fsSL https://pkg.obxodka.one/gpg.key -o /tmp/obxodka.gpg
sudo pacman-key --add /tmp/obxodka.gpg
sudo pacman-key --lsign-key "$(gpg --show-keys --with-colons /tmp/obxodka.gpg | awk -F: '/^fpr:/ {print $10; exit}')"
rm -f /tmp/obxodka.gpg

printf '\n[obxodka]\nServer = https://pkg.obxodka.one/arch/$arch\n' \
  | sudo tee -a /etc/pacman.conf

sudo pacman -Sy obxodka obxodka-desktop
```
Дальнейшие обновления приходят автоматически через `sudo pacman -Syu`.

#### Установка готового пакета вручную из GitHub Releases
Все `.deb`, `.rpm`, `.pkg.tar.zst` и портативные `.tar.gz` архивы публикуются в каждом релизе на [GitHub Releases](https://github.com/OctoCore-Dev/obxodka/releases/latest) вместе с `SHA256SUMS.txt`:
```bash
# Проверка контрольной суммы
sha256sum -c SHA256SUMS.txt --ignore-missing

# Пример установки локального файла
sudo apt install ./obxodka_*_amd64.deb
sudo dnf install ./obxodka-*.x86_64.rpm
sudo pacman -U ./obxodka-*-x86_64.pkg.tar.zst
```

---

## 3. Запуск Avalonia Desktop на Linux

После установки пакета `obxodka-desktop`:
- Откройте **Obxodka VPN** в меню приложений вашей среды рабочего стола (GNOME, KDE Plasma, Hyprland, Sway, XFCE) или выполните `obxodka-desktop` из терминала.
- Пост-инсталляционный скрипт пакета автоматически выдаёт бинарнику минимально необходимые сетевые привилегии Linux capabilities:
  ```bash
  sudo setcap cap_net_admin,cap_net_raw+ep /usr/lib/obxodka-desktop/obxodka.Avalonia
  ```
  Это позволяет поднимать системный L3 TUN-интерфейс (`tun0`) и маршруты без запуска всего GUI под полным `root`.
- При использовании портативного `.tar.gz` выполните `setcap` вручную или запускайте через `pkexec` / `sudo -E`.

---

## 4. Справочник команд CLI (`obx` / `obxodka`)

Команда `obx` является коротким алиасом для `obxodka`. Все команды поддерживают машиночитаемый вывод `--json` для скриптов и автоматизации.

| Команда | Алиас | Назначение |
| :--- | :--- | :--- |
| `obx login [--email <addr>] [--code <otp>]` | `auth` | Вход в аккаунт по email OTP и выпуск mTLS-сертификата устройства |
| `obx logout` | — | Выход из аккаунта и очистка сохранённых токенов |
| `obx whoami [--json]` | — | Профиль аккаунта и текущий баланс времени |
| `obx servers [--ping] [--json]` | `ls` | Список серверов кластера и параллельный замер пинга |
| `obx connect [id] [флаги]` | `start`, `up` | Подключение к VPN с живым TUI-дашбордом скорости и протокола |
| `obx disconnect` | `stop`, `down` | Бесшовное отключение активного туннеля |
| `obx status [--watch] [--json]` | `st` | Текущее состояние туннеля, транспорт, IP и трафик |
| `obx config get \| set <key> <val>` | — | Настройки: `kill-switch`, `adblock`, `block-ipv6`, `auto-reconnect`, `force-websocket`, `bypass-ru`, `sync-timezone` |
| `obx split list \| add \| remove <rule>` | — | Управление правилами раздельного туннелирования (домены / приложения) |
| `obx diag [--json]` | — | Диагностика прав (`CAP_NET_ADMIN` / Admin), TUN, DNS и доступности API |
| `obx alias [--install]` | — | Установка постоянного алиаса `obx` в профиль оболочки |
| `obx completion powershell\|bash\|zsh` | — | Генерация скрипта автодополнения аргументов |
| `obx version` | `-v`, `--version` | Версия CLI и целевая платформа |

### Флаги команды `connect`
```bash
obx connect [server-slug] \
  --protocol <auto|ws|xhttp|reality> \
  --kill-switch | --no-kill-switch \
  --adblock | --no-adblock \
  --block-ipv6 \
  --mtu <1240..1440> \
  --json
```

### Примеры повседневного использования
```bash
# 1. Авторизация
obx login --email user@example.com

# 2. Проверить доступность узлов и пинг
obx servers --ping

# 3. Подключиться к оптимальному серверу с AdBlock и Kill-Switch
obx connect --adblock --kill-switch

# 4. Подключиться к конкретной локации по протоколу Reality / Hydra
obx connect nl-amsterdam --protocol reality

# 5. Добавить сайт в обход VPN
obx split add gosuslugi.ru

# 6. Проверить состояние сети и прав
obx diag
```

---

## 5. Автозапуск CLI как фоновой службы `systemd` (Linux)

Пакет `obxodka` устанавливает системный юнит `/usr/lib/systemd/system/obxodka.service` с изоляцией прав (`NoNewPrivileges=true`, `AmbientCapabilities=CAP_NET_ADMIN CAP_NET_RAW`).

```bash
# Сначала выполните вход (создаст сессионный mTLS-профиль)
obx login

# Включить автозапуск при старте системы и запустить сейчас
sudo systemctl enable --now obxodka

# Проверить статус и логи
sudo systemctl status obxodka
journalctl -u obxodka -f
```
