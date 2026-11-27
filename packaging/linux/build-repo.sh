#!/usr/bin/env bash
set -euo pipefail

# генерация индексов apt, rpm и pacman для github pages
DIST_DIR="${1:?Укажите каталог с .deb/.rpm/.pkg.tar.zst пакетами}"
OUT_DIR="${2:?Укажите выходной каталог сайта репозитория}"
VERSION="${3:?Укажите версию релиза (например 6.4.1)}"
BASE_URL="${4:-https://pkg.obxodka.one}"

rm -rf "$OUT_DIR"
mkdir -p \
  "$OUT_DIR/apt/pool/main" \
  "$OUT_DIR/apt/dists/stable/main/binary-amd64" \
  "$OUT_DIR/apt/dists/stable/main/binary-arm64" \
  "$OUT_DIR/rpm/x86_64" \
  "$OUT_DIR/rpm/aarch64" \
  "$OUT_DIR/arch/x86_64" \
  "$OUT_DIR/arch/aarch64"

HAS_GPG="false"
GPG_KEY_ID=""
if [ -n "${GPG_PRIVATE_KEY:-}" ]; then
  echo "[GPG] Импорт закрытого ключа подписи репозитория..."
  export GNUPGHOME
  GNUPGHOME="$(mktemp -d)"
  chmod 700 "$GNUPGHOME"
  printf '%s\n' "$GPG_PRIVATE_KEY" | gpg --batch --import
  GPG_KEY_ID="$(gpg --list-secret-keys --with-colons | awk -F: '/^sec:/ {print $5; exit}')"
  if [ -n "$GPG_KEY_ID" ]; then
    HAS_GPG="true"
    gpg --armor --export "$GPG_KEY_ID" > "$OUT_DIR/gpg.key"
    cp "$OUT_DIR/gpg.key" "$OUT_DIR/gpg.asc"
    echo "[GPG] Ключ $GPG_KEY_ID импортирован и экспортирован в gpg.key."
  fi
else
  echo "::warning::[GPG] Секрет GPG_PRIVATE_KEY не задан — пакеты и метаданные будут опубликованы без GPG-подписи."
fi

gpg_sign_detach() {
  local target="$1"
  local sig="${2:-${target}.sig}"
  if [ -n "${GPG_PASSPHRASE:-}" ]; then
    gpg --batch --yes --pinentry-mode loopback --passphrase "$GPG_PASSPHRASE" \
      --local-user "$GPG_KEY_ID" --detach-sign --output "$sig" "$target"
  else
    gpg --batch --yes --local-user "$GPG_KEY_ID" --detach-sign --output "$sig" "$target"
  fi
}

gpg_sign_armor() {
  local target="$1"
  local sig="$2"
  if [ -n "${GPG_PASSPHRASE:-}" ]; then
    gpg --batch --yes --pinentry-mode loopback --passphrase "$GPG_PASSPHRASE" \
      --local-user "$GPG_KEY_ID" --armor --detach-sign --output "$sig" "$target"
  else
    gpg --batch --yes --local-user "$GPG_KEY_ID" --armor --detach-sign --output "$sig" "$target"
  fi
}

gpg_clearsign() {
  local target="$1"
  local out="$2"
  if [ -n "${GPG_PASSPHRASE:-}" ]; then
    gpg --batch --yes --pinentry-mode loopback --passphrase "$GPG_PASSPHRASE" \
      --local-user "$GPG_KEY_ID" --clearsign --output "$out" "$target"
  else
    gpg --batch --yes --local-user "$GPG_KEY_ID" --clearsign --output "$out" "$target"
  fi
}

# копирование пакетов по веткам репозитория
find "$DIST_DIR" -type f -name '*.deb' -exec cp {} "$OUT_DIR/apt/pool/main/" \;
find "$DIST_DIR" -type f -name '*.x86_64.rpm' -exec cp {} "$OUT_DIR/rpm/x86_64/" \;
find "$DIST_DIR" -type f -name '*.aarch64.rpm' -exec cp {} "$OUT_DIR/rpm/aarch64/" \;
find "$DIST_DIR" -type f -name '*-x86_64.pkg.tar.zst' -exec cp {} "$OUT_DIR/arch/x86_64/" \;
find "$DIST_DIR" -type f -name '*-aarch64.pkg.tar.zst' -exec cp {} "$OUT_DIR/arch/aarch64/" \;

# индексы apt
echo "[APT] Генерация индексов Packages и Release..."
(
  cd "$OUT_DIR/apt"
  dpkg-scanpackages --arch amd64 pool/main > dists/stable/main/binary-amd64/Packages
  gzip -9c dists/stable/main/binary-amd64/Packages > dists/stable/main/binary-amd64/Packages.gz

  dpkg-scanpackages --arch arm64 pool/main > dists/stable/main/binary-arm64/Packages
  gzip -9c dists/stable/main/binary-arm64/Packages > dists/stable/main/binary-arm64/Packages.gz

  apt-ftparchive \
    -o APT::FTPArchive::Release::Origin="OctoCore" \
    -o APT::FTPArchive::Release::Label="Obxodka VPN" \
    -o APT::FTPArchive::Release::Suite="stable" \
    -o APT::FTPArchive::Release::Codename="stable" \
    -o APT::FTPArchive::Release::Architectures="amd64 arm64" \
    -o APT::FTPArchive::Release::Components="main" \
    -o APT::FTPArchive::Release::Description="Official Obxodka VPN APT Repository (CLI & Avalonia Desktop)" \
    release dists/stable > dists/stable/Release

  if [ "$HAS_GPG" = "true" ]; then
    gpg_sign_armor dists/stable/Release dists/stable/Release.gpg
    gpg_clearsign dists/stable/Release dists/stable/InRelease
  fi
)

# индексы rpm
echo "[RPM] Генерация индексов createrepo_c..."
for arch in x86_64 aarch64; do
  if ls "$OUT_DIR/rpm/$arch"/*.rpm >/dev/null 2>&1; then
    createrepo_c --update "$OUT_DIR/rpm/$arch"
    if [ "$HAS_GPG" = "true" ]; then
      gpg_sign_armor "$OUT_DIR/rpm/$arch/repodata/repomd.xml" "$OUT_DIR/rpm/$arch/repodata/repomd.xml.asc"
    fi
  fi
done

cat << EOF > "$OUT_DIR/rpm/obxodka.repo"
[obxodka]
name=Obxodka VPN Repository (CLI & Desktop)
baseurl=${BASE_URL}/rpm/\$basearch
enabled=1
gpgcheck=0
repo_gpgcheck=$( [ "$HAS_GPG" = "true" ] && echo "1" || echo "0" )
gpgkey=${BASE_URL}/gpg.key
EOF

# база pacman
echo "[ARCH] Генерация баз obxodka.db.tar.gz через repo-add..."
for arch in x86_64 aarch64; do
  if ls "$OUT_DIR/arch/$arch"/*.pkg.tar.zst >/dev/null 2>&1; then
    if [ "$HAS_GPG" = "true" ]; then
      for pkg in "$OUT_DIR/arch/$arch"/*.pkg.tar.zst; do
        gpg_sign_detach "$pkg" "${pkg}.sig"
      done
    fi

    if command -v repo-add >/dev/null 2>&1; then
      (cd "$OUT_DIR/arch/$arch" && repo-add obxodka.db.tar.gz ./*.pkg.tar.zst)
    elif command -v docker >/dev/null 2>&1; then
      docker run --rm -v "$OUT_DIR/arch/$arch:/work" -w /work archlinux:latest \
        bash -c 'repo-add /work/obxodka.db.tar.gz /work/*.pkg.tar.zst'
    else
      echo "::warning::[ARCH] repo-add и docker недоступны — индекс Arch пропущен."
    fi

    # замена симлинков на файлы для github pages
    if [ -L "$OUT_DIR/arch/$arch/obxodka.db" ]; then
      rm -f "$OUT_DIR/arch/$arch/obxodka.db"
      cp "$OUT_DIR/arch/$arch/obxodka.db.tar.gz" "$OUT_DIR/arch/$arch/obxodka.db"
    fi
    if [ -L "$OUT_DIR/arch/$arch/obxodka.files" ]; then
      rm -f "$OUT_DIR/arch/$arch/obxodka.files"
      cp "$OUT_DIR/arch/$arch/obxodka.files.tar.gz" "$OUT_DIR/arch/$arch/obxodka.files"
    fi

    if [ "$HAS_GPG" = "true" ] && [ -f "$OUT_DIR/arch/$arch/obxodka.db.tar.gz" ]; then
      gpg_sign_detach "$OUT_DIR/arch/$arch/obxodka.db.tar.gz" "$OUT_DIR/arch/$arch/obxodka.db.tar.gz.sig"
      cp "$OUT_DIR/arch/$arch/obxodka.db.tar.gz.sig" "$OUT_DIR/arch/$arch/obxodka.db.sig"
    fi
  fi
done

# копирование установщика и индексной страницы
cp packaging/linux/install.sh "$OUT_DIR/install.sh"
chmod +x "$OUT_DIR/install.sh"
touch "$OUT_DIR/.nojekyll"

cat << EOF > "$OUT_DIR/index.html"
<!DOCTYPE html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Obxodka VPN — Репозиторий пакетов Linux (v${VERSION})</title>
  <style>
    :root { color-scheme: dark; }
    body { font-family: "Segoe UI", system-ui, -apple-system, sans-serif; background: #141418; color: #e6e8ef; max-width: 860px; margin: 40px auto; padding: 0 20px; line-height: 1.6; }
    h1, h2 { color: #60cdff; }
    pre { background: #1e1f26; border: 1px solid rgba(255,255,255,0.1); border-radius: 10px; padding: 14px 16px; overflow-x: auto; }
    code { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; color: #9be7ff; }
    a { color: #60cdff; }
    .badge { display: inline-block; padding: 3px 10px; border-radius: 999px; background: rgba(96,205,255,0.14); border: 1px solid rgba(96,205,255,0.35); font-size: 0.88em; }
  </style>
</head>
<body>
  <h1>Obxodka VPN Package Repository</h1>
  <p><span class="badge">v${VERSION}</span> Официальный репозиторий пакетов <code>obxodka</code> (CLI) и <code>obxodka-desktop</code> (Avalonia GUI) для Linux.</p>

  <h2>Быстрая установка (APT / DNF / Pacman)</h2>
  <pre><code># Установить CLI (obxodka + obx)
curl -fsSL ${BASE_URL}/install.sh | sudo sh

# Установить CLI + Avalonia Desktop GUI
curl -fsSL ${BASE_URL}/install.sh | sudo sh -s -- --all

# Установить только Avalonia Desktop GUI
curl -fsSL ${BASE_URL}/install.sh | sudo sh -s -- --desktop</code></pre>

  <h2>Windows (Winget)</h2>
  <pre><code>winget install OctoCore.Obxodka.Cli</code></pre>

  <h2>Ресурсы</h2>
  <ul>
    <li><a href="https://github.com/OctoCore-Dev/obxodka/releases">Все релизы и SHA256SUMS на GitHub Releases</a></li>
    <li><a href="${BASE_URL}/install.sh">install.sh</a></li>
    <li><a href="${BASE_URL}/rpm/obxodka.repo">rpm/obxodka.repo</a></li>
    <li><a href="${BASE_URL}/gpg.key">gpg.key</a> (GPG-ключ подписи: $( [ "$HAS_GPG" = "true" ] && echo "активен" || echo "ожидает настройки" ))</li>
  </ul>
</body>
</html>
EOF

echo "[SUCCESS] Репозиторий Linux-пакетов успешно собран в $OUT_DIR (v${VERSION}, GPG=${HAS_GPG})."
