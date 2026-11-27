# Настройка автопубликации Winget и Linux-репозиториев (APT / DNF / Pacman)

Пайплайн [`.github/workflows/release.yml`](../../.github/workflows/release.yml) автоматически собирает пакеты `obxodka` (CLI) и `obxodka-desktop` (Avalonia GUI) при каждом релизе. Чтобы включить автоматическую публикацию в пакетные менеджеры, выполните разовую настройку ниже.

## 1. Windows — Winget (`OctoCore.Obxodka.Cli`)

1. Создайте форк репозитория [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs) на своём аккаунте GitHub (`Copy the master branch only`).
2. Подайте первую версию пакета один раз с помощью официального `wingetcreate` (или отправьте готовые multi-file манифесты из `packaging/winget/manifests/` с подставленными версией из `version.props` и реальным SHA256 прямо из ассетов последнего GitHub Release):
   ```powershell
   winget install Microsoft.WingetCreate
   wingetcreate submit --token <YOUR_PAT> ./winget-manifests
   ```
   Шаблоны multi-file манифестов хранятся в [`manifests/`](manifests/) (`InstallerType: zip`, `NestedInstallerType: portable`, `obxodka.exe`, локаль `en-US` + `ru-RU`, без `.cmd`/скриптов).
3. Добавьте в **Repository Secrets** (`Settings → Secrets and variables → Actions`):
   - `WINGET_TOKEN` — GitHub Classic PAT с правом `public_repo`.
4. Все последующие релизы в `main` будут автоматически открывать PR с новой версией и SHA256 в `microsoft/winget-pkgs` через `vedantmgoyal9/winget-releaser`.

## 2. Linux — Подписанный репозиторий APT / DNF / Pacman (GitHub Pages)

1. Включите GitHub Pages в репозитории: **Settings → Pages → Build and deployment → Source: GitHub Actions**.
2. Сгенерируйте выделенный GPG-ключ подписи пакетов (один раз):
   ```bash
   gpg --full-generate-key   # RSA 4096 или Ed25519, Name: "Obxodka Package Signing Key", Email: support@obxodka.one
   gpg --armor --export-secret-keys "Obxodka Package Signing Key" > obxodka-gpg-private.asc
   ```
3. Добавьте в **Repository Secrets**:
   - `GPG_PRIVATE_KEY` — содержимое `obxodka-gpg-private.asc`
   - `GPG_PASSPHRASE` — парольная фраза ключа (если задавалась)
4. Удалите локальный `obxodka-gpg-private.asc` после добавления в Secrets.
5. При каждом релизе в `main` джоб `[PUBLISH-LINUX-REPO]` пересобирает подписанные индексы:
   - **APT**: `https://octocore-dev.github.io/obxodka/apt`
   - **DNF / RPM**: `https://octocore-dev.github.io/obxodka/rpm/$basearch`
   - **Pacman**: `https://octocore-dev.github.io/obxodka/arch/$arch`
   - **Установщик**: `https://octocore-dev.github.io/obxodka/install.sh`
