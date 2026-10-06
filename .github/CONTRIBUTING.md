# 🛠️ Руководство по участию в разработке (Contributing to Obxodka)

Спасибо за интерес к развитию проекта **Obxodka**! Мы рады любому конструктивному вкладу: от отчётов об ошибках и предложений новых функций до оптимизации низкоуровневого кода, скинов и улучшения документации.

---

## 💻 Требования для локальной разработки

* **.NET 10 SDK** (версия 10.0.100 или новее)
* **Visual Studio 2026 / Rider / VS Code** с расширениями для C# 14 и .NET MAUI
* Установленные рабочие нагрузки MAUI: `maui-windows`, `maui-android`

```powershell
# Установка необходимых рабочих нагрузок MAUI
dotnet workload install maui-windows maui-android
```

---

## 🚀 Процесс разработки и сборки (Workflow)

1. **Форкните** репозиторий [OctoCore-Dev/obxodka](https://github.com/OctoCore-Dev/obxodka) на GitHub.
2. Создайте свою ветку от `develop`:
   ```bash
   git checkout -b feature/awesome-feature
   ```
3. Соберите проект:
   ```powershell
   # Сборка MAUI клиента под Windows
   dotnet build src/apps/obxodka.Maui/obxodka.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug

   # Или сборка корневого решения
   dotnet build obxodka.sln -c Debug
   ```
4. Запустите модульные тесты:
   ```powershell
   dotnet test tests/obxodka.Client.Tests/obxodka.Client.Tests.csproj
   ```
   *Все 311+ тестов должны успешно проходить без ошибок и предупреждений.*
5. Убедитесь в чистоте кода:
   * **0 предупреждений компилятора (0 warnings, 0 errors)**.
   * Соблюдение правил архитектурной изоляции и Zero-Allocation в горячих путях обработки пакетов (`ArrayPool<byte>.Shared`).
6. Закоммитьте изменения:
   ```bash
   git commit -m "feat: добавлена поддержка функции X"
   ```
7. Отправьте ветку в свой форк и создайте **Pull Request** в ветку `develop` репозитория `OctoCore-Dev/obxodka`.

---

## ⚖️ Соглашение и лицензия

Отправляя код в проект Obxodka, вы подтверждаете согласие с условиями [Лицензии (Source-Available)](../LICENSE), [Политикой конфиденциальности](../PRIVACY_POLICY.md) и [Условиями использования](../TERMS_OF_SERVICE.md).

---

## 🐛 Сообщения об ошибках (Issues)

Если вы обнаружили баг:
1. Проверьте существующие [Issues](https://github.com/OctoCore-Dev/obxodka/issues), чтобы убедиться, что проблема ещё не зарегистрирована.
2. Создайте новый Issue, используя шаблон **Bug Report**.
3. Укажите версию операционной системы, версию приложения и шаги для воспроизведения.

---

## 💬 Связь с командой

* 📧 **Email:** [contact@octocore.dev](mailto:contact@octocore.dev)
* 🌐 **Сайт:** [https://obxodka.one](https://obxodka.one)
* 💬 **Обсуждения:** [GitHub Discussions](https://github.com/OctoCore-Dev/obxodka/discussions)
