# Ночной отчёт 2026-10-14: ActiveReports целиком на Xake

Задача ночи: довести `xake-ar/build.fsx` от 15 core-библиотек до всего `ar.meta.win.sln` (десктоп-дизайнер,
WPF-вьюер, приложения, тесты, nupkg), этап за этапом, с замерами против msbuild. Решения пользователя
(2026-10-13): локальный git для `~/Projects-work/ar/xake-ar`, источники из `ar-net-meta`, WPF гибридом,
`Svg.Tests` вне GCCN, пакеты Xake из night-фида, эталон — macOS-msbuild, VSIX/Babel/подпись вне охвата.

Где что лежит: `xake-ar/` (git, 16 коммитов за ночь) — `build.fsx`, описания/локи по брендам, `PLAN.md`,
`tests/model.md`, `tests/gaps.md` (62 гэпа S1–S57+, 48 помечены исправленными), `tests/results/<дата>-<этап>/`,
`tests/cases/` (эталонные и сравнивающие скрипты). Xake: ветки `pr/desc-fidelity` → `pr/win-import` →
{`pr/win-import-2`, `pr/wpf-exe-satellites`} → `night-2` → `pr/win-import-3` (worktrees под `.claude/worktrees/`),
отчёты в `docs/features/hermetic-build/fsc-rework/{desc-fidelity-report, win-import-report,
critic-fidelity, critic-fidelity-fixes, win-import-2-report, wpf-exe-satellites, night-2-integration,
win-import-3-report, critic-night-2}.md`. Ничего не запушено и не слито. Night-пакеты `3.7.1…3.7.8-night`
/ `0.3.1…0.3.8-night` в `xake-ar/feed/`.

## Результат

**Собирается всё.** 94 проекта `ar.meta.win.sln` для MESCIUS и 93 для GCCN, Debug и Release, все TFM
(net472, netstandard2.0, net8.0, net8.0-windows): core, движок, экспорты, ai-assistance, WinForms
(`Design.Win` — дизайнер), WPF (`Viewer.Wpf`, `WpfViewer` гибридом), пять exe (`Designer`, `DesignerClassic`,
`Viewer`, `WpfViewer`, `ThemeEditor`), 36 тестовых проектов, сателлиты ja/zh-CN, 45 nupkg на бренд.
`dotnet test` из папки Xake проходит так же, как из msbuild (ExpressionInfo 2936/2936; Rdl 1971 passed /
9 failed на обеих сторонах — macOS-специфика).

**Побайтовая идентичность с msbuild** (не цель, но получилась почти даром и служила детектором гэпов):
в финале **185/185 (MESCIUS) и 183/183 (GCCN) dll+exe и pdb идентичны во всех четырёх ячейках**, сателлиты
8/8, doc xml 92/92, ресурсы 4844/4844, apphost 2/2. Остались binding redirects в `.exe.config` (S55) и
`deps.json` для net8.0-windows/RID (S56, S58). Эталон выравнивается `ContinuousIntegrationBuild=true` + pathmap
(`tests/cases/align.targets`); обычный локальный `dotnet build` не идентичен по построению.

**Бенчмарки** (M5 Pro 18 ядер, чистые bin/obj и out, тёплый NuGet-кеш, load < 6, по два прогона):

| набор | msbuild чистая | Xake чистая | msbuild no-op | Xake no-op | pack msbuild / Xake |
|---|---|---|---|---|---|
| этап 1, 24 библиотеки | 12.4–15.3 с + restore 1.2–2.0 | 8.6–9.1 с | 1.5 с | 1.0 с | — |
| этап 3, 49 библиотек | 23.3–24.4 с + 2 с | 16.5–17.3 с | 3.0 с | 1.7 с | 13.4 с / 1.3 с |
| этап 5a, 85 проектов | 29.7–33.1 с + 2.8 с | 21.0–22.0 с | 4.5–5.9 с | 2.3 с | — |
| финал, 94 проекта, 0.3.8 | 30.4–41.9 с + 3.2–4.4 с | 28.7–31.6 с (+ deploy: 33.8–35.0) | 5.4–7.2 с | 3.0–3.1 с | 12.3–13.6 с / 3.9–4.1 с |

**Финальная матрица** (`xake-ar/tests/results/2026-10-14-final/results.md`, 0.3.8, `CSC_SERVER=off` во всех прогонах):

| метрика | msbuild | Xake | msbuild ÷ Xake |
|---|---|---|---|
| restore / проверка локов | 3.2–4.4 с | 1.2–1.4 с | 2.4–2.9× |
| чистая сборка одной ячейки | 30.4–41.9 с (бимодально) | 28.7–31.6 с | 1.07–1.24× |
| no-op | 5.4–7.2 с | 3.0–3.1 с | 1.8–2.3× |
| правка файла в `Export.Text.Page` (89 сборок) | 14–19 с | 21–22 с | **0.67–0.87×** |
| правка файла в `DataEngine` (122 сборки) | 20–28 с | 27–33 с | **0.68–0.95×** |
| CPU чистой сборки | 615–634 с | 351–362 с | 1.75× |
| четыре ячейки подряд (тёплое дерево) | 116 с | 98 с | 1.19× |
| четыре ячейки чистыми | 153 с | `build` по очереди в одном процессе 110 с; `build-par` 103–105 с | 1.39× / **1.46×** |
| pack | 12.3–13.6 с | 3.9–4.1 с | 3.0–3.5× |

Выводы по скорости, без прикрас:

- Преимущество на чистой сборке с ростом набора сжалось: 1.5× на 24 библиотеках → 1.1–1.2× на 94 проектах.
  Добавились WPF (msbuild-шаг внутри сборки), exe, deploy-копирование.
- **Инкремент медленнее msbuild**: Xake запускает отдельный csc на каждую цель, msbuild держит compiler
  server. Все замеры шли с `CSC_SERVER=off`; на малых компиляциях сервер давал 2–4× (`csc-server.md`).
  Это первый рычаг.
- **Все ячейки одним процессом** — сценарий ожидаемого разгромного преимущества (msbuild не может
  параллелить ячейки, bin/obj общие) — дал только 1.46×, а не 4–5×. `build-par` насыщает 18 ядер
  (среднее 13.5 csc, простоев нет), но критический путь раздувается с 27 с до 63 с от конкуренции за CPU,
  и оба чистых прогона упали на `WpfViewer`: markup-проект WPF общий для брендов (S53). То есть выигрыш
  упирается уже не в планировщик, а в объём работы: ~350 с CPU на ячейку × 4 на 18 ядрах ≈ 80 с минимум.
  Дальше растить преимущество можно только сокращением работы: csc server, общий вывод вместо копий,
  кешированный BAML, отказ от повторной компиляции одинаковых между ячейками целей.
- Зависаний нет; крах csc S17 (exit 134) один раз при load 47–68, вне таймингов.

**Дополнение утром: все ячейки одним процессом с горячим csc server** (`-d CSC_SERVER=on`, тёплый
NuGet-кеш, `out/` и `.xake` удалены, машина свободна; msbuild — четыре ячейки по очереди с его горячим
compiler server и node reuse, bin/obj удалены):

| сценарий | msbuild | Xake | ratio |
|---|---|---|---|
| четыре ячейки чистыми | 116.6 с (23.5 + 35.5 + 22.6 + 34.9) | `build-par` 58.8 с (упал на S53) / 62.0 с; `build` по очереди в одном процессе 60.1 с | **1.9×** |
| no-op всех ячеек | 4 × 5.4–7.2 с ≈ 22–29 с | 3.7–4.2 с | **6–7×** |

Сервер почти вдвое сократил чистую сборку (103–110 с → 59–62 с). `build-par` и `build` по очереди дали
одинаковое время: параллелизм между ячейками сам по себе выигрыша не даёт, машина насыщена и так; дальше
только сокращение работы (общий вывод, BAML-кеш, общие между ячейками цели). Один из двух `build-par`
упал на гонке markup `Viewer.Wpf` между брендами (S53). msbuild GCCN-ячейки завершились с rc=1 из-за
`Svg.Tests` (CS0433, у Xake исключён), остальное собрано. Скрипты: `~/.claude/jobs/7147ff60/tmp/par-*.sh`.

## Что сделано по этапам (xake-ar)

| этап | коммит | итог |
|---|---|---|
| 0 | e7b3126 | источники из `../ar-net-meta` (`AR_ROOT`), extra root `$(ArRoot)` |
| 1 | b5d6710 | +9 core-библиотек (document, dataproviders, excel, reportitems-chart, chart): 24 во всех ячейках |
| 2 | 295a1cf | +19 (`ActiveReports`, `Interop`, 9 `Export.*`, 8 ai): 62/88 сборок на 0.3.1 |
| 6 (частично) | 1b66c54 | pack: 41 nupkg на бренд, nuspec/readme/targets идентичны; pack 1.4 с vs 10.7 с |
| 2b | 74f8568 | на 0.3.2: 60/63 идентичны (было 0) |
| 3 | bed3ead | WinForms + дизайнер: 100/100 собраны, 94/100 идентичны, ресурсы 1168/1168 |
| 5a | b17b7b3 | 36 тестовых проектов: 162/172, smoke `dotnet test` |
| 4, 5b, финал | 2595ae7…e2189ca | WPF, exe, сателлиты; на 0.3.8 остались 3 workaround-а (S52, S53, S57); 185/185 идентичны во всех ячейках; матрица |

## Что сделано в Xake (четыре раунда фиксов + интеграция + два критика)

- **desc-fidelity** (0.3.2): assemblyInfo из SDK, порядок исходников как у msbuild, defines по конфигурации,
  `DisableDiagnosticTracing`, NETStandard-фасады, zh-CN. dataengine+page 30/30 идентичны.
- **win-import** (0.3.3/0.3.4): `$(...)` в Compile, projitems-ресурсы, `net8.0-windows` + WindowsDesktop
  ref pack, фасады net472, `ResXFileRef`/типизированные resx, `DependentUpon`-имена, SourceLink/embed, `/refout`,
  конфликты ссылок; правки по критику (ключи SourceLink, локи не зависят от состояния сборки, переводы строк
  хоста, `_OR_GREATER`, параллельный импорт 48 с → 5 с).
- **win-import-2** (0.3.6): точные правила фасад/фреймворк-vs-пакет (S32/S37/S38), Direct-пакеты платформенных
  TFM, implicit references WinForms/WPF, `ImplicitUsings`, порядок ресурсов, windows-1251, `WarningsNotAsErrors`.
- **wpf-exe-satellites** (0.3.5): WPF-гибрид (msbuild только для четырёх XAML-тасков), exe/apphost/
  runtimeconfig/deps.json, сателлиты через csc.
- **night-2** (0.3.7): слияние двух веток, 9 конфликтных файлов. **win-import-3** (0.3.8): Test.Sdk, анализаторы,
  фасады по правилу SDK, Include/ExcludeAssets, per-framework ресурсы, транзитивные проекты, `PlatformTarget`,
  `obj/xake/<brand>/`, `Locks.deployWith`.
- Тесты hermetic: 226 → 285, core 334/1; `build test` зелёный на каждом раунде.

## Критика (critic-fidelity.md, critic-night-2.md) — требует решений

1. **База веток**: всё стоит на 74bf4d9 (ночная интеграция), которого нет в `feature/hermetic-build`;
   merge-base bb0e990, 38 коммитов и конфликты в `.gitignore`/`roadmap.md`. Предложена последовательность PR:
   night base → `src/dotnet` (System.Resources.Extensions) → restore graph → compile fidelity → satellites →
   applications (после BN1) → deploy → WPF (после записи исключения в interview.md).
2. **BN1 (блокер)**: для `appHost ""` лок записывает apphost-пак машины импорта (osx-arm64) — каждый exe-лок,
   включая тесты, упадёт на Linux/Windows CI.
3. **BN2 (блокер)**: WPF-шаг зовёт msbuild во время `build`, что противоречит interview.md («msbuild только в
   import/update-locks»); исключение нигде не записано. Решение ниже (п. 5 следующего цикла) снимает его.
4. **Факты хоста/SDK в описаниях** (interview Q1/Q3): формат `usings` SDK 8 vs 10, `appHost`,
   `runtimeConfig.properties`, версии пакетов в `packageSources`/`copy`, переводы строк хоста в локах.
5. Разрешение ссылок — цепочка частных случаев, а не алгоритм msbuild; `deployWith` копирует всё из папки
   вывода; `generateWith` 370 строк; `.editorconfig` выше репозитория не хешируется.

## Замечания пользователя 2026-10-14 (утро) — приняты как решения следующего цикла

1. **Раскладка вывода**: одна папка на бренд+конфигурацию+TFM (`out/<бренд>/<cfg>/<tfm>/*.dll`), без
   per-project папок и без `deploy/`-копий закрытия; копируются только пакетные рантайм-ассеты и test data.
   Пакетная раскладка — описанием «что идёт в какой nupkg», не копированием.
2. **Минималистичный pack**: nuspec как есть + токены из `xake.project.json`, `lib/<tfm>` из общей папки;
   без `nugetManifest`/psmdcp/`pack-tokens` через msbuild. Побайтовое равенство пакетов не цель.
3. **Сателлиты** остаются в библиотеке Xake, из скрипта уходят (преждевременно).
4. **Библиотечный модуль** с готовыми правилами/рецептами (`import::`, `lock::`, `build::`, `pack::`,
   раскладка, бренды, extra roots); пользовательский `build.fsx` — один экран.
5. **BAML**: агрессивная предкомпиляция и кеширование — генерация `.g.cs`/`.baml` отдельной кешируемой фазой
   уровня `update-locks` (входы: xaml, ссылки, версия WPF-тасков; выходы в `generated/` под fingerprints),
   `build` только компилирует csc. Это же снимает блокер BN2.

План цикла: [cycle-2.md](cycle-2.md).

## Открытые вопросы к пользователю

Решение 2026-10-15: `fsc-rework/night-changes-decisions-ru.md` на `pr/apphost-resx` (Xake не эмулирует msbuild).

- ~~Порядок слияния веток Xake и судьба ночной базы 74bf4d9~~ — решено: вопрос о минимальной ветке снят
  последовательностью PR 0–4 (PR 0: merge `dev`, затем `74bf4d9` отдельным мержем).
- Факты хоста/SDK в описаниях — **открыто как решение 3a**: описание их больше не хранит, они уходят в lock;
  рекомендация: текст как записан, но LF, `$(SourceRevisionId)` токенизирован, пин SDK в lock'е.
- 9 падающих тестов `Rdl.Tests` на macOS: игнорировать для бенчмарков или разбирать.
- ~~Windows-эталон net472 на виртуалке~~ — идентичность не цель (решено 2026-10-14/15), эталон не нужен.
