#!/usr/bin/env sh
set -eu

# установщик obxodka для linux
REPO_BASE="${OBXODKA_REPO_URL:-https://octocore-dev.github.io/obxodka}"
GITHUB_REPO="OctoCore-Dev/obxodka"
MODE="cli"

for arg in "$@"; do
  case "$arg" in
    --cli) MODE="cli" ;;
    --desktop) MODE="desktop" ;;
    --all) MODE="all" ;;
    -h|--help)
      echo "Использование: install.sh [--cli | --desktop | --all]"
      echo "  --cli      Установить obxodka CLI (команды obxodka / obx и systemd-юнит) [по умолчанию]"
      echo "  --desktop  Установить obxodka-desktop (Avalonia GUI)"
      echo "  --all      Установить CLI и Avalonia Desktop"
      exit 0
      ;;
    *)
      echo "[ERROR] Неизвестный флаг: $arg" >&2
      exit 2
      ;;
  esac
done

if [ "$(id -u)" -ne 0 ]; then
  echo "[ERROR] Запустите установщик с правами root (через sudo)." >&2
  exit 1
fi

ARCH_RAW="$(uname -m)"
case "$ARCH_RAW" in
  x86_64|amd64)
    DEB_ARCH="amd64"
    RPM_ARCH="x86_64"
    ARCH_PKG="x86_64"
    ;;
  aarch64|arm64)
    DEB_ARCH="arm64"
    RPM_ARCH="aarch64"
    ARCH_PKG="aarch64"
    ;;
  *)
    echo "[ERROR] Неподдерживаемая архитектура: $ARCH_RAW (поддерживаются x86_64 и arm64)." >&2
    exit 1
    ;;
esac

PKGS=""
case "$MODE" in
  cli) PKGS="obxodka" ;;
  desktop) PKGS="obxodka-desktop" ;;
  all) PKGS="obxodka obxodka-desktop" ;;
esac

fetch() {
  url="$1"
  out="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$url" -o "$out"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$out" "$url"
  else
    echo "[ERROR] Требуется curl или wget." >&2
    exit 1
  fi
}

repo_has_signed_gpg() {
  if command -v curl >/dev/null 2>&1; then
    curl -fsSI "${REPO_BASE}/gpg.key" >/dev/null 2>&1
  else
    wget -q --spider "${REPO_BASE}/gpg.key" >/dev/null 2>&1
  fi
}

install_from_github_release() {
  ext="$1"
  arch_suffix="$2"
  installer="$3"

  TMP_DIR="$(mktemp -d)"
  trap 'rm -rf "$TMP_DIR"' EXIT

  echo "[INFO] Получение последнего релиза ${GITHUB_REPO} с GitHub Releases..."
  RELEASE_JSON="${TMP_DIR}/release.json"
  fetch "https://api.github.com/repos/${GITHUB_REPO}/releases/latest" "$RELEASE_JSON"

  TAG="$(sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$RELEASE_JSON" | head -n 1)"
  if [ -z "$TAG" ]; then
    echo "[ERROR] Не удалось определить последний тег релиза." >&2
    exit 1
  fi

  BASE_DL="https://github.com/${GITHUB_REPO}/releases/download/${TAG}"
  fetch "${BASE_DL}/SHA256SUMS.txt" "${TMP_DIR}/SHA256SUMS.txt"

  for pkg in $PKGS; do
    ASSET="$(grep -Eo "(^|[[:space:]])${pkg}[_-][0-9][^[:space:]]*${arch_suffix}\.${ext}" "${TMP_DIR}/SHA256SUMS.txt" | awk '{print $1}' | head -n 1 || true)"
    if [ -z "$ASSET" ]; then
      echo "[ERROR] Пакет ${pkg} (${arch_suffix}.${ext}) не найден в релизе ${TAG}." >&2
      exit 1
    fi

    echo "[INFO] Скачивание ${ASSET} (${TAG})..."
    fetch "${BASE_DL}/${ASSET}" "${TMP_DIR}/${ASSET}"

    echo "[VERIFY] Проверка SHA256 для ${ASSET}..."
    EXPECTED="$(grep "  ${ASSET}\$" "${TMP_DIR}/SHA256SUMS.txt" | awk '{print $1}')"
    ACTUAL="$(sha256sum "${TMP_DIR}/${ASSET}" | awk '{print $1}')"
    if [ -z "$EXPECTED" ] || [ "$EXPECTED" != "$ACTUAL" ]; then
      echo "[ERROR] Контрольная сумма SHA256 не совпала для ${ASSET}!" >&2
      exit 1
    fi

    case "$installer" in
      apt) apt-get install -y "${TMP_DIR}/${ASSET}" ;;
      dnf) dnf install -y "${TMP_DIR}/${ASSET}" ;;
      zypper) zypper --non-interactive install --allow-unsigned-rpm "${TMP_DIR}/${ASSET}" ;;
      pacman) pacman -U --noconfirm "${TMP_DIR}/${ASSET}" ;;
    esac
  done
}

echo "[OBXODKA] Установка (${MODE}, ${ARCH_RAW})..."

if command -v apt-get >/dev/null 2>&1; then
  if repo_has_signed_gpg; then
    echo "[APT] Подключение официального подписанного репозитория Obxodka..."
    mkdir -p /usr/share/keyrings /etc/apt/sources.list.d
    TMP_KEY="$(mktemp)"
    fetch "${REPO_BASE}/gpg.key" "$TMP_KEY"
    gpg --dearmor --yes -o /usr/share/keyrings/obxodka.gpg "$TMP_KEY"
    rm -f "$TMP_KEY"
    chmod 0644 /usr/share/keyrings/obxodka.gpg
    echo "deb [arch=${DEB_ARCH} signed-by=/usr/share/keyrings/obxodka.gpg] ${REPO_BASE}/apt stable main" \
      > /etc/apt/sources.list.d/obxodka.list
    apt-get update
    # shellcheck disable=SC2086
    apt-get install -y $PKGS
  else
    echo "[APT] Подписанный GPG-репозиторий ещё не активирован — установка напрямую из GitHub Releases с проверкой SHA256..."
    install_from_github_release "deb" "${DEB_ARCH}" "apt"
  fi
elif command -v dnf >/dev/null 2>&1; then
  if repo_has_signed_gpg; then
    echo "[DNF] Подключение официального репозитория Obxodka..."
    fetch "${REPO_BASE}/rpm/obxodka.repo" /etc/yum.repos.d/obxodka.repo
    # shellcheck disable=SC2086
    dnf install -y $PKGS
  else
    echo "[DNF] Установка напрямую из GitHub Releases с проверкой SHA256..."
    install_from_github_release "rpm" "${RPM_ARCH}" "dnf"
  fi
elif command -v zypper >/dev/null 2>&1; then
  if repo_has_signed_gpg; then
    echo "[ZYPPER] Подключение официального репозитория Obxodka..."
    zypper --non-interactive ar -f "${REPO_BASE}/rpm/${RPM_ARCH}" obxodka || true
    zypper --gpg-auto-import-keys refresh obxodka
    # shellcheck disable=SC2086
    zypper --non-interactive install $PKGS
  else
    install_from_github_release "rpm" "${RPM_ARCH}" "zypper"
  fi
elif command -v pacman >/dev/null 2>&1; then
  if repo_has_signed_gpg; then
    echo "[PACMAN] Подключение официального репозитория Obxodka..."
    TMP_KEY="$(mktemp)"
    fetch "${REPO_BASE}/gpg.key" "$TMP_KEY"
    FPR="$(gpg --show-keys --with-colons "$TMP_KEY" | awk -F: '/^fpr:/ {print $10; exit}')"
    pacman-key --add "$TMP_KEY"
    if [ -n "$FPR" ]; then
      pacman-key --lsign-key "$FPR"
    fi
    rm -f "$TMP_KEY"
    if ! grep -q '^\[obxodka\]' /etc/pacman.conf; then
      printf '\n[obxodka]\nServer = %s/arch/$arch\n' "${REPO_BASE}" >> /etc/pacman.conf
    fi
    # shellcheck disable=SC2086
    pacman -Sy --noconfirm $PKGS
  else
    echo "[PACMAN] Установка напрямую из GitHub Releases с проверкой SHA256..."
    install_from_github_release "pkg.tar.zst" "${ARCH_PKG}" "pacman"
  fi
else
  echo "[ERROR] Не найден поддерживаемый пакетный менеджер (apt, dnf, zypper, pacman)." >&2
  echo "        Скачайте портативный архив вручную: https://github.com/${GITHUB_REPO}/releases/latest" >&2
  exit 1
fi

echo ""
echo "[OK] Obxodka успешно установлена!"
case "$MODE" in
  cli)
    echo "     Запуск CLI:  obx login  &&  obx connect"
    echo "     Диагностика: obx diag"
    ;;
  desktop)
    echo "     Запуск GUI:  obxodka-desktop (или из меню приложений 'Obxodka VPN')"
    ;;
  all)
    echo "     Запуск CLI:  obx login  &&  obx connect"
    echo "     Запуск GUI:  obxodka-desktop (или из меню приложений 'Obxodka VPN')"
    ;;
esac
