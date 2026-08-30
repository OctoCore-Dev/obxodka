# Руководство по участию в разработке (Contributing to Obxodka)

Спасибо за интерес к развитию проекта **Obxodka**! Мы рады любому вкладу: от отчётов об ошибках и предложений новых функций до оптимизации кода и улучшения документации.

---

## 🛠️ Требования для локальной разработки

* **.NET 10 SDK** (или новее)
* **Visual Studio 2026 / Rider / VS Code** с поддержкой .NET MAUI
* Рабочие нагрузки: `maui-windows`, `maui-android`

```powershell
# Установка необходимых рабочих нагрузок MAUI
dotnet workload install maui-windows maui-android
```

---

## 🚀 Как отправить свой вклад (Workflow)

1. **Форкните** репозиторий на GitHub.
2. Создайте свою ветку функции от `main`:
   ```bash
   git checkout -b feature/awesome-feature
   ```
3. Внесите изменения и убедитесь, что проект компилируется без ошибок:
   ```powershell
   dotnet build obxodka.csproj
   ```
4. Запустите модульные тесты:
   ```powershell
   dotnet test tests/obxodka.Tests/obxodka.Tests.csproj
   ```
5. Закоммитьте изменения с понятным сообщением:
   ```bash
   git commit -m "feat: добавлена поддержка функции X"
   ```
6. Отправьте ветку в свой форк:
   ```bash
   git push origin feature/awesome-feature
   ```
7. Откройте **Pull Request** в репозиторий `OctoCore-Dev/obxodka`.

---

## 🐛 Сообщения об ошибках (Issues)

Если вы обнаружили баг:
1. Проверьте существующие [Issues](https://github.com/OctoCore-Dev/obxodka/issues), чтобы убедиться, что проблема ещё не зарегистрирована.
2. Создайте новый Issue, используя шаблон **Bug Report**.
3. Укажите версию операционной системы, версию приложения и шаги для воспроизведения.

---

## 💬 Связь с разработчиками

По любым вопросам или предложениям вы можете написать нам:
📧 **contact@octocore.dev**
🌐 **[obxodka.one](https://obxodka.one)**
