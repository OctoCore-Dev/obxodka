## Описание изменений

Кратко опишите, что было изменено, добавлено или исправлено в этом Pull Request.

Связанные тикеты (Issues):
Closes #

---

## Тип изменений

- [ ] Исправление ошибки (Bug fix)
- [ ] Новая функция (Feature)
- [ ] Оптимизация производительности (Performance improvement)
- [ ] Безопасность (Security fix)
- [ ] Документация (Documentation)
- [ ] Тесты (Unit / Integration tests)

---

## Чек-лист перед отправкой

- [ ] Код компилируется без ошибок и предупреждений
  (`dotnet build src/apps/obxodka.Maui/obxodka.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release`)
- [ ] Модульные тесты проходят успешно
  (`dotnet test tests/obxodka.Client.Tests/obxodka.Client.Tests.csproj -c Release --filter "Category!=Integration&Category!=UI&Category!=WireLevel"`)
- [ ] Код соответствует стандартам качества (Zero-Allocation в горячих путях, без секретов и блокирующих вызовов)
- [ ] Изменения предлагаются под лицензией [GNU AGPLv3](LICENSE) с соблюдением [TRADEMARK.md](TRADEMARK.md)
- [ ] Обновлена сопутствующая документация (`.md`) при изменении протоколов или архитектуры
